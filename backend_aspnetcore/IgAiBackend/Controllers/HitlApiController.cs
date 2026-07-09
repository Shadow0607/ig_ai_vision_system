/* Controllers/HitlApiController.cs */
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using IgAiBackend.Helpers;
using IgAiBackend.Services;

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/hitl")]
[Authorize]
public class HitlApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly string _bucketName;
    private readonly string _minioEndpoint;
    
    // 🌟 注入共用的 S3 服務
    private readonly IS3MediaStorageService _s3Service;

    public HitlApiController(
        ApplicationDbContext context,
        IConnectionMultiplexer redis,
        IConfiguration config,
        IS3MediaStorageService s3Service) // 💡 移除了原本的 IMinioClient，讓控制器更專注
    {
        _context = context;
        _redis = redis;
        _s3Service = s3Service;

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
            var rawData = await _context.AiAnalysisLogs
                .Include(a => a.MediaAsset)
                .ThenInclude(m => m.Person)
                .Include(a => a.Status)
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
                    FilePath = a.MediaAsset != null ? a.MediaAsset.FilePath : "",
                    StatusName = a.Status!.DisplayName,
                    StatusColor = a.Status!.UiColor
                })
                .ToListAsync(); 

            var pending = rawData.Select(a => new
            {
                Id = a.Id,
                MediaId = a.MediaId,
                PersonName = a.PersonName,
                SystemName = a.SystemName,
                Score = a.Score,

                Url = !string.IsNullOrEmpty(a.FilePath)
                      ? $"http://{_minioEndpoint}/{_bucketName}/{a.FilePath}"
                      : "",

                PosterUrl = !string.IsNullOrEmpty(a.FilePath)
                            ? (a.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                                ? $"http://{_minioEndpoint}/{_bucketName}/{a.FilePath.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase)}"
                                : $"http://{_minioEndpoint}/{_bucketName}/{a.FilePath}")
                            : "",

                StatusName = a.StatusName,
                StatusColor = a.StatusColor
            });

            return Ok(pending);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Hitl Api 嚴重錯誤] \n{ex.ToString()}\n");
            return StatusCode(500, new { message = "資料庫查詢錯誤，請查看後端 Log。" });
        }
    }

    [HttpPost("approve")]
    [Authorize]
    public async Task<IActionResult> ApproveHitl([FromBody] HitlApproveDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", _redis))
            return StatusCode(403, new { message = "🚫 您不具備執行覆核操作的權限。" });

        var roleClaim = User.FindFirst("RoleId")?.Value;
        if (!int.TryParse(roleClaim, out int roleId) || roleId == 3)
        {
            return StatusCode(403, new { message = "🚫 嚴重越權：Guest 訪客帳號嚴禁變更 AI 特徵庫基準 (SEED_PHOTO / OUTPUT)！" });
        }

        long targetId = dto.ImageId ?? dto.Id ?? dto.MediaId ?? 0;

        var confirmedId = await _context.SysStatuses
            .Where(s => s.Category == "AI_RECOGNITION" && s.Code == "HITL_CONFIRMED")
            .Select(s => s.Id)
            .FirstOrDefaultAsync();

        if (confirmedId == 0)
            return StatusCode(500, new { message = "系統狀態未初始化：找不到 HITL_CONFIRMED 狀態。" });

        var log = await _context.AiAnalysisLogs
            .Include(a => a.MediaAsset)
            .Include(a => a.Status)
            .FirstOrDefaultAsync(a => (a.MediaId == targetId || a.Id == targetId) && a.Status!.Code == "PENDING");

        if (log == null || log.MediaAsset == null)
            return NotFound(new { message = "找不到該筆紀錄或已被處理" });

        string currentUser = User.Identity?.Name ?? "SystemAdmin";

        // 🌟 核心變更：S3 若成功，會一併把狀態更新處理好；失敗則什麼都不會發生
        if (await ProcessSingleApproveAsync(log, log.MediaAsset.SystemName, confirmedId, currentUser))
        {
            await _context.SaveChangesAsync();
            await TriggerFeatureBankRebuild(log.MediaAsset.SystemName);
            return Ok(new { message = "✅ 審核通過！檔案已納入特徵庫，AI 將於背景開始重新學習。" });
        }

        return BadRequest(new { message = "❌ S3 檔案搬移失敗，請檢查檔案是否存在或 NAS 連線狀態。" });
    }

    [HttpPost("reject")]
    [Authorize]
    public async Task<IActionResult> RejectHitl([FromBody] HitlRejectDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Delete", _redis))
            return StatusCode(403, new { message = "🚫 您的帳號權限不足，無法執行排除操作（僅限管理員）。" });

        long targetId = dto.ImageId ?? dto.Id ?? dto.MediaId ?? 0;

        var rejectedId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "REJECTED").Select(s => s.Id).FirstOrDefaultAsync();

        var log = await _context.AiAnalysisLogs
            .Include(a => a.MediaAsset)
            .Include(a => a.Status)
            .FirstOrDefaultAsync(a => (a.MediaId == targetId || a.Id == targetId) && a.Status!.Code == "PENDING");

        if (log == null || log.MediaAsset == null) return NotFound(new { message = "找不到該筆紀錄或已被處理" });

        string currentUser = User.Identity?.Name ?? "SystemAdmin";

        if (await ProcessSingleRejectAsync(log, log.MediaAsset.SystemName, rejectedId, currentUser))
        {
            await _context.SaveChangesAsync();
            await TriggerFeatureBankRebuild(log.MediaAsset.SystemName);
            return Ok(new { message = "已拒絕，檔案已移至 GARBAGE" });
        }

        return BadRequest(new { message = "❌ S3 檔案搬移失敗，請檢查檔案是否存在。" });
    }

    [HttpPost("batch-approve")]
    [Authorize]
    public async Task<IActionResult> BatchApprove([FromBody] HitlBatchDto dto)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", _redis))
            return StatusCode(403, new { message = "🚫 您的帳號權限不足。" });
        
        var roleId = GetCurrentRoleId();
        if (roleId == 3) return Forbid();

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
        string currentUser = User.Identity?.Name ?? "SystemAdmin";

        foreach (var log in logs)
        {
            if (log.MediaAsset != null && await ProcessSingleApproveAsync(log, log.MediaAsset.SystemName, confirmedId, currentUser))
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

    // ====================================================
    // 內部輔助與授權邏輯 (🌟 結合共用服務的新版本)
    // ====================================================
    private int GetCurrentRoleId()
    {
        var claim = User.FindFirst("RoleId")?.Value;
        return int.TryParse(claim, out int id) ? id : 3; 
    }

    // 🌟 核准操作邏輯：包含 S3 搬移與資料庫模型賦值
    private async Task<bool> ProcessSingleApproveAsync(AiAnalysisLog log, string systemName, int newStatusId, string currentUser)
    {
        try
        {
            // 呼叫共用服務，只有 S3 完美成功，才會回傳新的路徑並繼續往下走
            string newPath = await _s3Service.MoveMediaWithThumbnailAsync(log.MediaAsset.FilePath, systemName, "pos", syncToOutput: true);

            // S3 成功後，才變更 Entity Framework 追蹤的屬性
            log.MediaAsset.FilePath = newPath;
            log.StatusId = newStatusId;
            log.HitlReviewedAt = DateTime.Now;
            log.ReviewedBy = currentUser;
            log.ConfidenceScore = 1.0f; 

            return true;
        }
        catch (Exception ex)
        {
            // 只要 S3 報錯（包含 404），立刻攔截並回傳 false，資料庫實體屬性保持原樣
            Console.WriteLine($"❌ [S3 Move Error] 核准操作失敗，已阻斷資料庫更新: {ex.Message}");
            return false;
        }
    }

    // 🌟 拒絕操作邏輯：包含 S3 搬移與資料庫模型賦值
    private async Task<bool> ProcessSingleRejectAsync(AiAnalysisLog log, string systemName, int newStatusId, string currentUser)
    {
        try
        {
            string newPath = await _s3Service.MoveMediaWithThumbnailAsync(log.MediaAsset.FilePath, systemName, "GARBAGE", syncToOutput: false);

            log.MediaAsset.FilePath = newPath;
            log.StatusId = newStatusId;
            log.HitlReviewedAt = DateTime.Now;
            log.ReviewedBy = currentUser;
            log.ConfidenceScore = 0.0f; 

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [S3 Move Error] 拒絕操作失敗，已阻斷資料庫更新: {ex.Message}");
            return false;
        }
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

// ==========================================
// DTO 模型
// ==========================================
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