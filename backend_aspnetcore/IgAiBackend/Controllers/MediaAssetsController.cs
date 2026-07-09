using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;
using System.Text.Json;
using IgAiBackend.Models;
using Microsoft.Extensions.Options;
using IgAiBackend.Helpers;
using IgAiBackend.Services; // 🌟 引入自訂服務
using Minio;
using Minio.DataModel.Args;

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] 
public class MediaAssetsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<MediaAssetsController> _logger;
    private readonly IS3MediaStorageService _s3Service; // 🌟 注入 S3 共用服務
    
    // 雖然 S3 搬移邏輯已抽離，但 GetMediaStreamUrl 依然需要直接呼叫 Minio 來產生臨時連結
    private readonly IMinioClient _minioClient; 
    private readonly string _bucketName;

    public MediaAssetsController(
        ApplicationDbContext context, 
        ILogger<MediaAssetsController> logger,
        IS3MediaStorageService s3Service,
        IMinioClient minioClient,
        IOptions<MinioSettings> minioSettings)
    {
        _context = context;
        _logger = logger;
        _s3Service = s3Service;
        _minioClient = minioClient;
        _bucketName = minioSettings.Value.BucketName;
    }

    [HttpGet("{mediaId}/stream")]
    public async Task<IActionResult> GetMediaStreamUrl(long mediaId)
    {
        var media = await _context.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId);
        if (media == null) return NotFound();

        var presignedArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(media.FilePath)
            .WithExpiry(3600);

        string streamUrl = await _minioClient.PresignedGetObjectAsync(presignedArgs);

        return Ok(new { streamUrl = streamUrl }); 
    }

    // ==========================================
    // 1. 取得分類影像清單 
    // ==========================================
    [HttpGet("classified")]
    public async Task<IActionResult> GetClassifiedMedia(
        [FromQuery] string status = "OUTPUT",
        [FromQuery] string? systemName = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Include(l => l.Status)
            .AsQueryable();

        if (status == "REJECTED")
        {
            query = query.Where(l => l.Status!.Code == "REJECTED" || l.Status!.Code == "GARBAGE" || l.Status!.Code == "SKIP");
        }
        else if (status == "ALL")
        {
            query = query.Where(l => l.Status!.Code != "DOWNLOADED");
        }
        else
        {
            query = query.Where(l => l.Status!.Code == status);
        }

        if (!string.IsNullOrEmpty(systemName))
        {
            query = query.Where(l => l.MediaAsset.SystemName == systemName);
        }

        int totalItems = await query.CountAsync();
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var rawItems = await query
            .OrderByDescending(l => l.ProcessedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.ConfidenceScore,
                l.ProcessedAt,
                RecognitionStatus = l.Status!.Code,
                StatusDisplayName = l.Status!.DisplayName,
                StatusUiColor = l.Status!.UiColor,
                ReviewedBy = l.ReviewedBy,
                SystemName = l.MediaAsset.SystemName,
                FileName = l.MediaAsset.FileName,
                OriginalUsername = l.MediaAsset.OriginalUsername,
                FilePath = l.MediaAsset.FilePath 
            })
            .ToListAsync();

        var items = new List<object>();
        foreach (var l in rawItems)
        {
            string secureStreamUrl = "";
            try
            {
                if (!string.IsNullOrEmpty(l.FilePath))
                {
                    var presignedArgs = new PresignedGetObjectArgs()
                        .WithBucket(_bucketName)
                        .WithObject(l.FilePath)
                        .WithExpiry(3600);
                    secureStreamUrl = await _minioClient.PresignedGetObjectAsync(presignedArgs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[安全連結產生失敗] {l.FilePath}: {ex.Message}");
            }

            bool isManualOutput = l.RecognitionStatus == "OUTPUT" && (l.ConfidenceScore >= 1.0f || l.ReviewedBy != null);
            bool isManualReject = (l.RecognitionStatus == "REJECTED" || l.RecognitionStatus == "GARBAGE" || l.RecognitionStatus == "SKIP") && (l.ConfidenceScore <= 0.0f || l.ReviewedBy != null);

            items.Add(new
            {
                l.Id,
                l.ConfidenceScore,
                l.ProcessedAt,
                l.RecognitionStatus,
                StatusName = isManualOutput ? "👋 人工判定" : isManualReject ? "👋 人工排除" : l.StatusDisplayName,
                StatusColor = isManualOutput ? "#10b981" : isManualReject ? "#ef4444" : l.StatusUiColor,
                l.ReviewedBy,
                l.SystemName,
                l.FileName,
                l.OriginalUsername,
                Url = secureStreamUrl 
            });
        }

        return Ok(new
        {
            Items = items,
            TotalItems = totalItems,
            TotalPages = totalPages,
            CurrentPage = page
        });
    }

    // ==========================================
    // 2. 單筆分類更新 (🌟 使用共用 S3 服務改寫)
    // ==========================================
    [HttpPut("reclassify")]
    public async Task<IActionResult> ReclassifyMedia(
        [FromBody] ReclassifyRequestDto request,
        [FromServices] IConnectionMultiplexer redis)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", redis))
            return StatusCode(403, new { message = "🚫 權限不足。" });

        if (GetCurrentRoleId() == 3) return Forbid();

        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID" });

        var log = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .FirstOrDefaultAsync(l => l.Id == request.LogId);

        if (log == null) return NotFound(new { message = "找不到該筆紀錄" });

        string systemName = log.MediaAsset.SystemName;
        bool isOutput = targetStatus.Code == "OUTPUT";
        string targetFolder = isOutput ? "pos" : (targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code);

        try
        {
            // 🌟 1. 呼叫共用服務：如果 S3 搬移失敗（拋出 Exception），會直接跳到 catch，不會更新資料庫
            string newPath = await _s3Service.MoveMediaWithThumbnailAsync(
                log.MediaAsset.FilePath, 
                systemName, 
                targetFolder, 
                syncToOutput: isOutput
            );

            // 🌟 2. 只有 S3 成功才會執行到這裡
            string currentUser = User.Identity?.Name ?? "SystemAdmin";

            log.MediaAsset.FilePath = newPath;
            log.StatusId = request.NewStatusId;
            
            if (targetStatus.Code == "OUTPUT")
            {
                log.ConfidenceScore = 1.0f;
                log.ReviewedBy = currentUser;
            }
            else if (targetStatus.Code == "REJECTED" || targetStatus.Code == "GARBAGE" || targetStatus.Code == "SKIP")
            {
                log.ConfidenceScore = 0.0f;
                log.ReviewedBy = currentUser;
            }

            await _context.SaveChangesAsync();

            await TriggerFeatureBankRebuild(redis, systemName);

            return Ok(new { message = "分類更新成功，AI 已將此影像納入學習樣本！" });
        }
        catch (Exception ex)
        {
            _logger.LogError($"[Reclassify] S3 搬移失敗，已阻斷資料庫更新: {ex.Message}");
            return StatusCode(500, new { message = $"S3 檔案搬移失敗，請檢查檔案是否存在。" });
        }
    }

    // ==========================================
    // 3. 批量分類更新 (非同步 Message Queue 版)
    // ==========================================
    [HttpPut("batch-reclassify")]
    [Authorize]
    public async Task<IActionResult> BatchReclassifyMedia(
        [FromBody] BatchReclassifyRequestDto request,
        [FromServices] IConnectionMultiplexer redis)
    {
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", redis))
            return StatusCode(403, new { message = "🚫 權限不足。" });

        if (GetCurrentRoleId() == 3) return StatusCode(403, new { message = "🚫 嚴重越權：Guest 訪客帳號禁止操作。" });

        if (request.LogIds == null || !request.LogIds.Any())
            return BadRequest(new { message = "未提供待處理的 ID 清單。" });

        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的目標狀態 ID。" });

        var processingStatus = await _context.SysStatuses
            .FirstOrDefaultAsync(s => s.Category == "AI_RECOGNITION" && s.Code == "PROCESSING");

        if (processingStatus == null) return StatusCode(500, new { message = "系統缺少 PROCESSING 狀態。" });

        var logs = await _context.AiAnalysisLogs
            .Where(l => request.LogIds.Contains(l.Id))
            .ToListAsync();

        if (!logs.Any()) return NotFound(new { message = "找不到符合條件的紀錄。" });

        string currentUser = User.Identity?.Name ?? "SystemAdmin";

        foreach (var log in logs)
        {
            log.StatusId = processingStatus.Id; 
        }
        await _context.SaveChangesAsync();

        var batchTaskPayload = new
        {
            LogIds = request.LogIds,
            NewStatusId = request.NewStatusId,
            TargetStatusCode = targetStatus.Code,
            ReviewedBy = currentUser,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var db = redis.GetDatabase();
        await db.ListLeftPushAsync("ig_batch_reclassify_queue", JsonSerializer.Serialize(batchTaskPayload));

        return StatusCode(202, new { message = $"✅ 已受理 {logs.Count} 筆分類任務，正在背景安全搬移中..." });
    }

    // ==========================================
    // 輔助方法
    // ==========================================
    private int GetCurrentRoleId()
    {
        var claim = User.FindFirst("RoleId")?.Value;
        return int.TryParse(claim, out int id) ? id : 3;
    }

    private async Task TriggerFeatureBankRebuild(IConnectionMultiplexer redis, string systemName)
    {
        try
        {
            var db = redis.GetDatabase();
            var taskPayload = new
            {
                type = "BUILD_FEATURE_BANK",
                profile = systemName,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await db.ListLeftPushAsync("ig_processing_queue_high", JsonSerializer.Serialize(taskPayload));
        }
        catch (Exception ex)
        {
            _logger.LogError($"⚠️ 推送 AI 重建任務失敗: {ex.Message}");
        }
    }
}

// ==========================================
// DTO 區塊 
// ==========================================
public class ReclassifyRequestDto
{
    public int LogId { get; set; }
    public int NewStatusId { get; set; }
}

public class BatchReclassifyRequestDto
{
    public List<long> LogIds { get; set; } = new List<long>();
    public int NewStatusId { get; set; }
}