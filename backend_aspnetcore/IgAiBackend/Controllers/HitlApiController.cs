/* Controllers/HitlApiController.cs */
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Minio;
using Minio.DataModel.Args;

using Microsoft.AspNetCore.Authorization;
using IgAiBackend.Helpers;

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/hitl")]
[Authorize]
public class HitlApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;
    private readonly string _minioEndpoint;

    public HitlApiController(
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

    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingHitl()
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "View", _redis))
            return StatusCode(403, new { message = "🚫 您沒有查看覆核數據的權限。" });

        try
        {
            var pending = await _context.AiAnalysisLogs
                .Include(a => a.MediaAsset)
                .ThenInclude(m => m.Person)
                .Include(a => a.Status) // 🌟 必須 Include Status 才能讀取字典屬性
                .Where(a => a.Status!.Code == "PENDING")
                .Select(a => new
                {
                    Id = a.Id,
                    MediaId = a.MediaId,
                    PersonName = a.MediaAsset != null && a.MediaAsset.Person != null
                                 ? (a.MediaAsset.Person.DisplayName ?? a.MediaAsset.Person.SystemName)
                                 : "Unknown",
                    SystemName = a.MediaAsset != null && a.MediaAsset.Person != null
                                 ? a.MediaAsset.Person.SystemName
                                 : "Unknown",
                    Score = a.ConfidenceScore,
                    Url = a.MediaAsset != null ? $"http://{_minioEndpoint}/{_bucketName}/{a.MediaAsset.FilePath}" : "",
                    PosterUrl = a.MediaAsset != null
                                ? (a.MediaAsset.FilePath.EndsWith(".mp4")
                                    ? $"http://{_minioEndpoint}/{_bucketName}/{a.MediaAsset.FilePath.Replace(".mp4", ".jpg")}"
                                    : $"http://{_minioEndpoint}/{_bucketName}/{a.MediaAsset.FilePath}")
                                : "",
                                
                    // 🌟 架構師優化：一併帶回狀態的顯示名稱與顏色
                    StatusName = a.Status!.DisplayName,
                    StatusColor = a.Status!.UiColor
                })
                .ToListAsync();

            return Ok(pending);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Hitl Api Error] {ex.Message}");
            return StatusCode(500, new { message = "資料庫查詢錯誤，請查看後端 Log。" });
        }
    }
    [HttpPost("approve")]
    [Authorize] 
    public async Task<IActionResult> ApproveHitl([FromBody] HitlApproveDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", _redis))
            return StatusCode(403, new { message = "🚫 您不具備執行覆核操作的權限。" });
        

        long targetId = dto.ImageId ?? dto.Id ?? dto.MediaId ?? 0;

        // 🌟 1. 取得核准的狀態 ID
        var confirmedId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "HITL_CONFIRMED").Select(s => s.Id).FirstOrDefaultAsync();

        var log = await _context.AiAnalysisLogs
            .Include(a => a.MediaAsset)
            .Include(a => a.Status)
            .FirstOrDefaultAsync(a => (a.MediaId == targetId || a.Id == targetId) && a.Status!.Code == "PENDING");

        if (log == null || log.MediaAsset == null) return NotFound(new { message = "找不到該筆紀錄或已被處理" });

        // 🌟 2. 傳入 confirmedId
        if (await ProcessSingleApproveAsync(log, log.MediaAsset.SystemName, confirmedId)) 
        {
            await _context.SaveChangesAsync();
            await TriggerFeatureBankRebuild(log.MediaAsset.SystemName);
            return Ok(new { message = "審核通過，檔案已移至 pos 並複製至 OUTPUT" });
        }

        return BadRequest(new { message = "S3 檔案搬移失敗" });
    }

    [HttpPost("reject")]
    [Authorize]
    public async Task<IActionResult> RejectHitl([FromBody] HitlRejectDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Delete", _redis))
            return StatusCode(403, new { message = "🚫 您的帳號權限不足，無法執行排除操作（僅限管理員）。" });

        long targetId = dto.ImageId ?? dto.Id ?? dto.MediaId ?? 0;

        // 🌟 1. 取得拒絕的狀態 ID
        var rejectedId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "REJECTED").Select(s => s.Id).FirstOrDefaultAsync();

        var log = await _context.AiAnalysisLogs
            .Include(a => a.MediaAsset)
            .Include(a => a.Status)
            .FirstOrDefaultAsync(a => (a.MediaId == targetId || a.Id == targetId) && a.Status!.Code == "PENDING");

        if (log == null || log.MediaAsset == null) return NotFound(new { message = "找不到該筆紀錄或已被處理" });

        if (await ProcessSingleRejectAsync(log, log.MediaAsset.SystemName, rejectedId))
        {
            await _context.SaveChangesAsync();
            await TriggerFeatureBankRebuild(log.MediaAsset.SystemName);
            return Ok(new { message = "已拒絕，檔案已移至 GARBAGE" });
        }

        return BadRequest(new { message = "S3 檔案搬移失敗" });
    }

    [HttpPost("batch-approve")]
    [Authorize]
    public async Task<IActionResult> BatchApprove([FromBody] HitlBatchDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", _redis))
        {
            return StatusCode(403, new { message = "🚫 您的帳號權限不足，無法執行批次覆核。" });
        }

        if (dto.Ids == null || !dto.Ids.Any())
            return BadRequest(new { message = "未提供任何要處理的 ID" });

        var confirmedId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "HITL_CONFIRMED").Select(s => s.Id).FirstOrDefaultAsync();

        var logs = await _context.AiAnalysisLogs
            .Include(a => a.MediaAsset)
            .Include(a => a.Status)
            .Where(a => (dto.Ids.Contains(a.MediaId) || dto.Ids.Contains(a.Id)) && a.Status!.Code == "PENDING")
            .ToListAsync();

        if (!logs.Any())
            return NotFound(new { message = "找不到符合條件的紀錄" });

        int successCount = 0;
        var affectedProfiles = new HashSet<string>();

        foreach (var log in logs)
        {
            if (log.MediaAsset != null && await ProcessSingleApproveAsync(log, log.MediaAsset.SystemName, confirmedId))
            {
                successCount++;
                affectedProfiles.Add(log.MediaAsset.SystemName);
            }
        }

        await _context.SaveChangesAsync();
        foreach (var profile in affectedProfiles)
        {
            await TriggerFeatureBankRebuild(profile);
        }

        return Ok(new { message = $"批次處理完成，共通過 {successCount} 筆資料" });
    }

    // ----------------------------------------------------
    // 內部輔助與授權邏輯
    // ----------------------------------------------------
    private int GetCurrentRoleId()
    {
        var claim = User.FindFirst("RoleId")?.Value;
        return int.TryParse(claim, out int id) ? id : 3; // 預設 3 (Guest)
    }

    // 🌟 加入 newStatusId 參數，並移除 log.HitlConfirmed
    private async Task<bool> ProcessSingleApproveAsync(AiAnalysisLog log, string systemName, int newStatusId)
    {
        if (await MoveObjectInS3Async(log, systemName, "pos", newStatusId, syncToOutput: true))
        {
            log.HitlReviewedAt = DateTime.Now;
            return true;
        }
        return false;
    }

    // 🌟 加入 newStatusId 參數，並移除 log.HitlConfirmed
    private async Task<bool> ProcessSingleRejectAsync(AiAnalysisLog log, string systemName, int newStatusId)
    {
        if (await MoveObjectInS3Async(log, systemName, "GARBAGE", newStatusId, syncToOutput: false))
        {
            log.HitlReviewedAt = DateTime.Now;
            return true;
        }
        return false;
    }

    // 🌟 將 newStatus (string) 改成 newStatusId (int)
    private async Task<bool> MoveObjectInS3Async(AiAnalysisLog log, string systemName, string targetFolder, int newStatusId, bool syncToOutput)
    {
        try
        {
            string sourceKey = log.MediaAsset.FilePath;
            string fileName = sourceKey.Split('/').Last();
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            if (sourceKey == targetKey) return true;

            await S3CopyAndRemove(sourceKey, targetKey);
            if (syncToOutput)
            {
                await S3CopyOnly(targetKey, $"{systemName}/OUTPUT/{fileName}");
            }

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

            // 🌟 將狀態更新寫入 StatusId
            log.MediaAsset.FilePath = targetKey;
            log.StatusId = newStatusId; 

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [S3 Move Error] 搬移失敗: {ex.Message}");
            return false;
        }
    }

    private async Task S3CopyAndRemove(string src, string dest)
    {
        try
        {
            var cpSrcArgs = new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src);
            await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest).WithCopyObjectSource(cpSrcArgs));
            await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(src));
        }
        catch { /* 忽略例外 */ }
    }

    private async Task S3CopyOnly(string src, string dest)
    {
        try
        {
            var cpSrcArgs = new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src);
            await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest).WithCopyObjectSource(cpSrcArgs));
        }
        catch { /* 忽略例外 */ }
    }

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

            await db.ListLeftPushAsync("ig_processing_queue_high", JsonSerializer.Serialize(taskPayload));
        }
        catch { /* 忽略例外 */ }
    }
}

public class HitlApproveDto
{
    public long? Id { get; set; }
    public long? ImageId { get; set; }
    public long? MediaId { get; set; }
    public string? TargetPerson { get; set; }
}

public class HitlBatchDto
{
    public required List<long> Ids { get; set; }
}

public class HitlRejectDto
{
    public long? Id { get; set; }
    public long? ImageId { get; set; }
    public long? MediaId { get; set; }
}