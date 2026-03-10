using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Minio;
using Minio.DataModel.Args;
using IgAiBackend.Helpers;
namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ColdStartController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly IMinioClient _minioClient; // 🌟 注入 MinIO Client
    private readonly string _bucketName;
    private readonly string _minioEndpoint;

    public ColdStartController(
        ApplicationDbContext context,
        IConnectionMultiplexer redis,
        IMinioClient minioClient,
        IConfiguration config)
    {
        _context = context;
        _redis = redis;
        _minioClient = minioClient;

        _bucketName = config["Minio:BucketName"] ?? "ig-ai-assets";
        _minioEndpoint = config["Minio:Endpoint"] ?? "localhost:9000";
    }

    // ==========================================
    // 1. 取得待審核清單 (隨機抽樣 20 張)
    // ==========================================
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingReviews()
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "ColdStartSetup", "View", _redis)) 
            return StatusCode(403, new { message = "🚫 您沒有查看冷啟動數據的權限。" });

        // 🌟 修正：Include Status 並使用 Code 查詢
        var pendingSystemNames = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Include(l => l.Status)
            .Where(l => l.Status!.Code == "INITIAL_REVIEW" || l.Status!.Code == "PENDING")
            .Select(l => l.MediaAsset.SystemName)
            .Distinct()
            .ToListAsync();

        var resultList = new List<object>();

        foreach (var sysName in pendingSystemNames)
        {
            var displayName = await _context.TargetPersons.Where(p => p.SystemName == sysName).Select(p => p.DisplayName).FirstOrDefaultAsync() ?? sysName;

            var query = _context.AiAnalysisLogs
                .Include(l => l.MediaAsset).ThenInclude(m => m.MediaType)
                .Include(l => l.Status)
                .Where(l => (l.Status!.Code == "INITIAL_REVIEW" || l.Status!.Code == "PENDING") && l.MediaAsset.SystemName == sysName);

            int totalCount = await query.CountAsync();

            var randomSamples = await query
                .OrderByDescending(l => l.MediaAsset.MediaType!.Code == "IMAGE") // 🌟 修正
                .ThenBy(r => Guid.NewGuid())
                .Take(20)
                .Select(l => new
                {
                    MediaId = l.MediaId,
                    Url = $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}",
                    PosterUrl = l.MediaAsset.FilePath.EndsWith(".mp4") ? $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath.Replace(".mp4", ".jpg")}" : $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}",
                    FileName = l.MediaAsset.FileName
                }).ToListAsync();

            if (randomSamples.Any()) resultList.Add(new { SystemName = sysName, DisplayName = displayName, TotalPending = totalCount, Images = randomSamples });
        }
        return Ok(resultList);
    }

    // ==========================================
    // 2. 萬能圖片讀取 API (已廢棄，由前端直接呼叫 S3)
    // ==========================================
    [HttpGet("view/{systemName}/{folder}/{*fileName}")]
    public IActionResult ViewMedia(string systemName, string folder, string fileName)
    {
        return BadRequest(new { message = "此 API 已廢棄，請直接使用 S3 URL 讀取圖片。" });
    }

    // ==========================================
    // 3. 確認選擇 (Confirm) - 搬移至 pos 並複製至 OUTPUT
    // ==========================================
    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmSelection([FromBody] ColdStartConfirmDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "ColdStartSetup", "Update", _redis)) 
            return StatusCode(403, new { message = "🚫 權限不足。" });;
        
        // 🌟 取得新狀態的 ID
        var outputId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "OUTPUT").Select(s => s.Id).FirstOrDefaultAsync();
        var garbageId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "GARBAGE").Select(s => s.Id).FirstOrDefaultAsync();

        var selectedLogs = await _context.AiAnalysisLogs.Include(l => l.MediaAsset).Include(l => l.Status)
            .Where(l => l.MediaAsset.SystemName == dto.SystemName && (l.Status!.Code == "INITIAL_REVIEW" || l.Status!.Code == "PENDING") && dto.SelectedMediaIds.Contains(l.MediaId)).ToListAsync();

        int successCount = 0;
        foreach (var log in selectedLogs)
        {
            if (await MoveObjectInS3Async(log, dto.SystemName, "pos", outputId, syncToOutput: true)) successCount++;
        }

        int rejectCount = 0;
        if (dto.RejectedMediaIds != null && dto.RejectedMediaIds.Any())
        {
            var rejectedLogs = await _context.AiAnalysisLogs.Include(l => l.MediaAsset).Include(l => l.Status)
                .Where(l => l.MediaAsset.SystemName == dto.SystemName && (l.Status!.Code == "INITIAL_REVIEW" || l.Status!.Code == "PENDING") && dto.RejectedMediaIds.Contains(l.MediaId)).ToListAsync();

            foreach (var log in rejectedLogs)
            {
                if (await MoveObjectInS3Async(log, dto.SystemName, "GARBAGE", garbageId, syncToOutput: false)) rejectCount++;
            }
        }
        await _context.SaveChangesAsync();
        if (successCount > 0 || rejectCount > 0) await TriggerFeatureBankRebuild(dto.SystemName);
        return Ok(new { message = $"處理完成 (入庫:{successCount}, 排除:{rejectCount})", processedCount = successCount });
    }

    // ==========================================
    // 4. [新增] 拒絕/刪除 API
    // ==========================================
    [HttpPost("reject")]
    public async Task<IActionResult> RejectSelection([FromBody] ColdStartRejectDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "ColdStartSetup", "DELETE", _redis)) 
            return StatusCode(403, new { message = "🚫 權限不足。" });

        if (dto.RejectedMediaIds == null || !dto.RejectedMediaIds.Any())
        {
            return BadRequest("必須提供至少一個要刪除的 ID");
        }

        // 🌟 1. 取得 REJECTED 的狀態 ID
        var rejectedId = await _context.SysStatuses
            .Where(s => s.Category == "AI_RECOGNITION" && s.Code == "REJECTED")
            .Select(s => s.Id)
            .FirstOrDefaultAsync();

        // 🌟 2. 修正查詢條件：Include Status 並使用 Status.Code
        var rejectedLogs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Include(l => l.Status) // 必須 Include
            .Where(l => l.MediaAsset.SystemName == dto.SystemName
                        && (l.Status!.Code == "INITIAL_REVIEW" || l.Status!.Code == "PENDING")
                        && dto.RejectedMediaIds.Contains(l.MediaId))
            .ToListAsync();

        int rejectCount = 0;
        foreach (var log in rejectedLogs)
        {
            // 🌟 3. 傳入整數 ID (rejectedId) 而不是字串 "REJECTED"
            if (await MoveObjectInS3Async(log, dto.SystemName, "GARBAGE", rejectedId, syncToOutput: false))
            {
                rejectCount++;
            }
        }

        await _context.SaveChangesAsync();

        if (rejectCount > 0)
        {
            await TriggerFeatureBankRebuild(dto.SystemName);
        }

        return Ok(new { message = $"刪除完成", count = rejectCount });
    }

    // ==========================================
    // 🚀 S3 物件搬移核心邏輯 (支援影音雙軌同步)
    // ==========================================
    private async Task<bool> MoveObjectInS3Async(AiAnalysisLog log, string systemName, string targetFolder, int newStatusId, bool syncToOutput)
    {
        try
        {
            string sourceKey = log.MediaAsset.FilePath;
            string fileName = sourceKey.Split('/').Last();
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            if (sourceKey != targetKey)
            {
                await S3CopyAndRemove(sourceKey, targetKey);
                if (syncToOutput) await S3CopyOnly(targetKey, $"{systemName}/OUTPUT/{fileName}");
                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    await S3CopyAndRemove(thumbSrc, thumbDest);
                    if (syncToOutput) await S3CopyOnly(thumbDest, $"{systemName}/OUTPUT/{thumbDest.Split('/').Last()}");
                }
            }
            string currentUser = User.Identity?.Name ?? "System";
            // 🌟 寫入新的路徑與狀態
            log.MediaAsset.FilePath = targetKey;
            log.StatusId = newStatusId; 

            // 🌟 視覺強迫症專屬：人工確認拉回直接滿分，排除則歸零
            if (targetFolder == "pos" || targetFolder == "OUTPUT") {
                log.ConfidenceScore = 1.0f;
                log.ReviewedBy = currentUser;
            } else if (targetFolder == "GARBAGE") {
                log.ConfidenceScore = 0.0f;
                log.ReviewedBy = currentUser;
            }

            return true;
        }
        catch (Exception ex) { Console.WriteLine($"❌ [S3 Move] 失敗: {ex.Message}"); return false; }
    }

    // 輔助方法：S3 複製並刪除原檔 (Move)
    private async Task S3CopyAndRemove(string src, string dest)
    {
        try
        {
            var cpSrcArgs = new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src);
            await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest).WithCopyObjectSource(cpSrcArgs));
            await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(src));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [S3 Helper] 無法搬移物件 {src}: {ex.Message}");
        }
    }

    // 輔助方法：僅 S3 複製 (Copy)
    private async Task S3CopyOnly(string src, string dest)
    {
        try
        {
            var cpSrcArgs = new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src);
            await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest).WithCopyObjectSource(cpSrcArgs));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ [S3 Helper] 無法複製物件 {src}: {ex.Message}");
        }
    }

    // ==========================================
    // 觸發 Python 重建特徵庫
    // ==========================================
    private async Task TriggerFeatureBankRebuild(string systemName)
    {
        try
        {
            var db = _redis.GetDatabase();
            var taskPayload = new
            {
                type = "BUILD_FEATURE_BANK",
                profile = systemName,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // 🌟 改變發送目標：寫入 _high 高優先級佇列
            await db.ListLeftPushAsync("ig_processing_queue_high", JsonSerializer.Serialize(taskPayload));
            Console.WriteLine($"🚀 [Redis] 觸發大腦重建({systemName}) ⚡ 已送入高優先級通道");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [Redis Error] {ex.Message}");
        }
    }
    

    // ==========================================
    // 5. [新增] 手動批量上傳正/負樣本
    // ==========================================
    // ==========================================
    // 5. [新增] 手動批量上傳正/負樣本
    // ==========================================
    [HttpPost("{systemName}/upload")]
    [RequestSizeLimit(100_000_000)] // 允許上傳至 100MB (避免多檔案被擋)
    public async Task<IActionResult> UploadManualSamples(string systemName, [FromForm] List<IFormFile> files, [FromForm] bool isPositive)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "ColdStartSetup", "Update", _redis))
            return StatusCode(403, new { message = "🚫 權限不足" });

        if (!FileValidationHelper.IsValid(files, out string error))
        {
            return BadRequest(new { message = error });
        }

        // 🌟 1. 預先查好所有的狀態 ID，避免在迴圈內重複查詢資料庫
        var imgId = await _context.SysStatuses.Where(s => s.Category == "MEDIA_TYPE" && s.Code == "IMAGE").Select(s => s.Id).FirstOrDefaultAsync();
        var vidId = await _context.SysStatuses.Where(s => s.Category == "MEDIA_TYPE" && s.Code == "VIDEO").Select(s => s.Id).FirstOrDefaultAsync();
        var manualSourceId = await _context.SysStatuses.Where(s => s.Category == "SOURCE_TYPE" && s.Code == "MANUAL_UPLOAD").Select(s => s.Id).FirstOrDefaultAsync();
        var downloadedId = await _context.SysStatuses.Where(s => s.Category == "DOWNLOAD_STATUS" && s.Code == "DOWNLOADED").Select(s => s.Id).FirstOrDefaultAsync();

        string targetFolder = isPositive ? "pos" : "GARBAGE";
        // 🌟 根據正負樣本決定狀態 Code，正樣本為 SEED_PHOTO，負樣本為 GARBAGE
        string targetStatusCode = isPositive ? "SEED_PHOTO" : "GARBAGE"; 
        var targetStatusId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == targetStatusCode).Select(s => s.Id).FirstOrDefaultAsync();

        int successCount = 0;

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            string ext = Path.GetExtension(file.FileName).ToLower();
            string newFileName = $"manual_{Guid.NewGuid():N}{ext}";
            string targetKey = $"{systemName}/{targetFolder}/{newFileName}";

            try
            {
                using var stream = file.OpenReadStream();
                var putArgs = new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(targetKey)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(file.ContentType);

                await _minioClient.PutObjectAsync(putArgs);

                if (isPositive)
                {
                    using var stream2 = file.OpenReadStream(); 
                    var putArgsOut = new PutObjectArgs()
                        .WithBucket(_bucketName)
                        .WithObject($"{systemName}/OUTPUT/{newFileName}")
                        .WithStreamData(stream2)
                        .WithObjectSize(file.Length)
                        .WithContentType(file.ContentType);
                    await _minioClient.PutObjectAsync(putArgsOut);
                }

                // 🌟 2. 改用 ID 寫入 MediaAssets (加入缺失的 SourceTypeId 和 DownloadStatusId)
                var mediaAsset = new MediaAsset
                {
                    SystemName = systemName,
                    FileName = newFileName,
                    FilePath = targetKey,
                    MediaTypeId = (ext == ".mp4" || ext == ".mov") ? vidId : imgId,
                    SourceTypeId = manualSourceId,
                    DownloadStatusId = downloadedId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.MediaAssets.Add(mediaAsset);
                await _context.SaveChangesAsync(); 

                // 🌟 3. 改用 ID 寫入 AiAnalysisLog
                var log = new AiAnalysisLog
                {
                    MediaId = mediaAsset.Id,
                    StatusId = targetStatusId, 
                    ConfidenceScore = 1.0f, 
                    ReviewedBy = User.Identity?.Name ?? "System", // 👈 可以順手補上這行
                    ProcessedAt = DateTime.UtcNow
                };
                _context.AiAnalysisLogs.Add(log);

                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Manual Upload] 失敗: {ex.Message}");
            }
        }

        await _context.SaveChangesAsync();

        if (successCount > 0)
        {
            await TriggerFeatureBankRebuild(systemName);
        }

        return Ok(new { message = $"成功上傳 {successCount} 筆手動樣本" });
    }
}

// ==========================================
// DTO 模型
// ==========================================
public class ColdStartConfirmDto
{
    public required string SystemName { get; set; }
    public required List<long> SelectedMediaIds { get; set; }
    public List<long>? RejectedMediaIds { get; set; }
}

public class ColdStartRejectDto
{
    public required string SystemName { get; set; }
    public required List<long> RejectedMediaIds { get; set; }
}