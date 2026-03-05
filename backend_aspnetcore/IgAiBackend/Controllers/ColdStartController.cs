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
        if (!await HasPermission("ColdStartSetup", "View"))
            return StatusCode(403, new { message = "🚫 您沒有查看冷啟動數據的權限。" });

        var pendingSystemNames = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => l.RecognitionStatus == "INITIAL_REVIEW" || l.RecognitionStatus == "DOWNLOADED")
            .Select(l => l.MediaAsset.SystemName)
            .Distinct()
            .ToListAsync();

        var resultList = new List<object>();

        foreach (var sysName in pendingSystemNames)
        {
            var displayName = await _context.TargetPersons
                .Where(p => p.SystemName == sysName)
                .Select(p => p.DisplayName)
                .FirstOrDefaultAsync() ?? sysName;

            var query = _context.AiAnalysisLogs
                .Include(l => l.MediaAsset)
                .Where(l => (l.RecognitionStatus == "INITIAL_REVIEW" || l.RecognitionStatus == "DOWNLOADED")
                            && l.MediaAsset.SystemName == sysName);

            int totalCount = await query.CountAsync();

            var randomSamples = await query
                .OrderByDescending(l => l.MediaAsset.MediaType == "IMAGE")
                .ThenBy(r => Guid.NewGuid())
                .Take(20)
                .Select(l => new
                {
                    MediaId = l.MediaId,
                    // 🌟 主檔案網址 (可能是 .mp4 或 .jpg)
                    Url = $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}",
                    // 🌟 封面網址 (如果是影片，將 .mp4 替換成 .jpg 以取得封面圖)
                    PosterUrl = l.MediaAsset.FilePath.EndsWith(".mp4")
                                ? $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath.Replace(".mp4", ".jpg")}"
                                : $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}",
                    FileName = l.MediaAsset.FileName
                })
                .ToListAsync();

            if (randomSamples.Any())
            {
                resultList.Add(new
                {
                    SystemName = sysName,
                    DisplayName = displayName,
                    TotalPending = totalCount,
                    Images = randomSamples
                });
            }
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
        if (!await HasPermission("ColdStartSetup", "Update"))
            return StatusCode(403, new { message = "🚫 您不具備建立特徵庫的權限。" });
        var selectedLogs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => l.MediaAsset.SystemName == dto.SystemName
                     && (l.RecognitionStatus == "INITIAL_REVIEW" || l.RecognitionStatus == "DOWNLOADED")
                     && dto.SelectedMediaIds.Contains(l.MediaId))
            .ToListAsync();

        int successCount = 0;
        foreach (var log in selectedLogs)
        {
            // 🌟 呼叫非同步的 S3 搬移，並指示需要同步複製一份到 OUTPUT
            if (await MoveObjectInS3Async(log, dto.SystemName, "pos", "CONFIRMED", syncToOutput: true))
            {
                successCount++;
            }
        }

        // 處理負樣本 (Rejected) -> 移至 GARBAGE
        int rejectCount = 0;
        if (dto.RejectedMediaIds != null && dto.RejectedMediaIds.Any())
        {
            var rejectedLogs = await _context.AiAnalysisLogs
                .Include(l => l.MediaAsset)
                .Where(l => l.MediaAsset.SystemName == dto.SystemName
                         && (l.RecognitionStatus == "INITIAL_REVIEW" || l.RecognitionStatus == "DOWNLOADED")
                         && dto.RejectedMediaIds.Contains(l.MediaId))
                .ToListAsync();

            foreach (var log in rejectedLogs)
            {
                if (await MoveObjectInS3Async(log, dto.SystemName, "GARBAGE", "REJECTED", syncToOutput: false))
                {
                    rejectCount++;
                }
            }
        }

        await _context.SaveChangesAsync();

        if (successCount > 0 || rejectCount > 0)
        {
            await TriggerFeatureBankRebuild(dto.SystemName);
        }

        return Ok(new
        {
            message = $"處理完成 (入庫:{successCount}, 排除:{rejectCount})",
            processedCount = successCount
        });
    }

    // ==========================================
    // 4. [新增] 拒絕/刪除 API
    // ==========================================
    [HttpPost("reject")]
    public async Task<IActionResult> RejectSelection([FromBody] ColdStartRejectDto dto)
    {
        if (!await HasPermission("ColdStartSetup", "Delete"))
            return StatusCode(403, new { message = "🚫 您不具備排除樣本的權限（僅限管理員）。" });

        if (dto.RejectedMediaIds == null || !dto.RejectedMediaIds.Any())
        {
            return BadRequest("必須提供至少一個要刪除的 ID");
        }

        var rejectedLogs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => l.MediaAsset.SystemName == dto.SystemName
                        && (l.RecognitionStatus == "INITIAL_REVIEW" || l.RecognitionStatus == "DOWNLOADED")
                        && dto.RejectedMediaIds.Contains(l.MediaId))
            .ToListAsync();

        int rejectCount = 0;
        foreach (var log in rejectedLogs)
        {
            if (await MoveObjectInS3Async(log, dto.SystemName, "GARBAGE", "REJECTED", syncToOutput: false))
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
    private async Task<bool> MoveObjectInS3Async(AiAnalysisLog log, string systemName, string targetFolder, string newStatus, bool syncToOutput)
    {
        try
        {
            string sourceKey = log.MediaAsset.FilePath;
            // 🌟 修正：不使用 Path.GetFileName，改用字串分割獲取檔名
            string fileName = sourceKey.Split('/').Last();
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            if (sourceKey == targetKey) return true;

            // 1. 搬移主檔案 (MinIO 內部 Copy+Remove)
            await S3CopyAndRemove(sourceKey, targetKey);
            if (syncToOutput)
            {
                await S3CopyOnly(targetKey, $"{systemName}/OUTPUT/{fileName}");
            }

            // 2. 處理影片縮圖
            if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                await S3CopyAndRemove(thumbSrc, thumbDest);
                if (syncToOutput)
                {
                    string thumbFileName = thumbDest.Split('/').Last();
                    await S3CopyOnly(thumbDest, $"{systemName}/OUTPUT/{thumbFileName}");
                }
            }

            // 3. 更新資料庫路徑 (純 S3 Key)
            log.MediaAsset.FilePath = targetKey;
            log.RecognitionStatus = newStatus;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [S3 Move] 失敗: {ex.Message}");
            return false;
        }
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
    private async Task<bool> HasPermission(string routeName, string action)
    {
        var claim = User.FindFirst("RoleId")?.Value;
        int roleId = int.TryParse(claim, out int id) ? id : 3; // 預設 Guest

        var perm = await _context.RolePermissions
            .Include(rp => rp.SystemRoute)
            .FirstOrDefaultAsync(rp => rp.RoleId == roleId && rp.SystemRoute.RouteName == routeName);

        if (perm == null || !perm.CanView) return false;

        return action switch
        {
            "View" => perm.CanView,
            "Update" => perm.CanUpdate,
            "Delete" => perm.CanDelete,
            _ => false
        };
    }

    // ==========================================
    // 5. [新增] 手動批量上傳正/負樣本
    // ==========================================
    [HttpPost("{systemName}/upload")]
    [RequestSizeLimit(100_000_000)] // 允許上傳至 100MB (避免多檔案被擋)
    public async Task<IActionResult> UploadManualSamples(string systemName, [FromForm] List<IFormFile> files, [FromForm] bool isPositive)
    {
        // 檢查是否有上傳權限 (可以複用 Create 或 Update 權限)
        if (!await HasPermission("ColdStartSetup", "Update"))
            return StatusCode(403, new { message = "🚫 權限不足" });

        // 🌟 使用統一驗證工具
        if (!FileValidationHelper.IsValid(files, out string error))
        {
            return BadRequest(new { message = error });
        }

        int successCount = 0;
        string targetFolder = isPositive ? "pos" : "GARBAGE";
        string status = isPositive ? "CONFIRMED" : "REJECTED";

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            string ext = Path.GetExtension(file.FileName).ToLower();
            // 產生唯一檔名避免衝突
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

                // 如果是正樣本，同步複製一份到 OUTPUT (與 Confirm 行為一致)
                if (isPositive)
                {
                    using var stream2 = file.OpenReadStream(); // 重新讀取串流
                    var putArgsOut = new PutObjectArgs()
                        .WithBucket(_bucketName)
                        .WithObject($"{systemName}/OUTPUT/{newFileName}")
                        .WithStreamData(stream2)
                        .WithObjectSize(file.Length)
                        .WithContentType(file.ContentType);
                    await _minioClient.PutObjectAsync(putArgsOut);
                }

                // 寫入資料庫以保持系統一致性
                var mediaAsset = new MediaAsset
                {
                    SystemName = systemName,
                    FileName = newFileName,
                    FilePath = targetKey,
                    MediaType = (ext == ".mp4" || ext == ".mov") ? "VIDEO" : "IMAGE",
                    CreatedAt = DateTime.UtcNow
                };
                _context.MediaAssets.Add(mediaAsset);
                await _context.SaveChangesAsync(); // 先存檔取得 ID

                var log = new AiAnalysisLog
                {
                    MediaId = mediaAsset.Id,
                    RecognitionStatus = status,
                    ConfidenceScore = 1.0f, // 手動上傳視為 100% 準確
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

        // 觸發 Python 重建特徵庫
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