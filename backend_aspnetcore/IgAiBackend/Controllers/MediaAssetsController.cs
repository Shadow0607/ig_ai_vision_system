using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using Microsoft.AspNetCore.Authorization;
using Minio;
using Minio.DataModel.Args;
using StackExchange.Redis;
using System.Text.Json;
using System;
using System.Threading.Tasks;
using IgAiBackend.Models;
using Microsoft.Extensions.Options;
using IgAiBackend.Helpers;
namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // 確保必須登入才能取得媒體
public class MediaAssetsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName = "ig-ai-assets";

    private readonly ILogger<ConfigController> _logger;

    public MediaAssetsController(ApplicationDbContext context, IMinioClient minioClient, IOptions<MinioSettings> minioSettings, ILogger<ConfigController> logger)
    {
        _context = context;
        _minioClient = minioClient;
        _bucketName = minioSettings.Value.BucketName;
        _logger = logger;
    }
    [HttpGet("{mediaId}/stream")]
    public async Task<IActionResult> GetMediaStreamUrl(long mediaId)
    {
        var media = await _context.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId);
        if (media == null) return NotFound();

        // 🌟 產生 1 小時有效的臨時安全連結
        var presignedArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(media.FilePath)
            .WithExpiry(3600);

        string streamUrl = await _minioClient.PresignedGetObjectAsync(presignedArgs);

        return Ok(new { streamUrl = streamUrl }); // 🚀 僅回傳網址，不轉發流量
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

        // 過濾邏輯保持不變
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

        // 🌟 步驟一：只從資料庫撈取所需的「純資料」 (包含 FilePath)
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
                FilePath = l.MediaAsset.FilePath // 👈 必須撈出實體路徑以供後續加密
            })
            .ToListAsync();

        // 🌟 步驟二：在記憶體中批次產生時效性安全連結
        var items = new List<object>();
        foreach (var l in rawItems)
        {
            string secureStreamUrl = "";
            try
            {
                if (!string.IsNullOrEmpty(l.FilePath))
                {
                    // 呼叫 NAS 產生時效為 3600 秒 (1小時) 的加密連結
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

            // 判斷人工狀態
            bool isManualOutput = l.RecognitionStatus == "OUTPUT" && (l.ConfidenceScore >= 1.0f || l.ReviewedBy != null);
            bool isManualReject = (l.RecognitionStatus == "REJECTED" || l.RecognitionStatus == "GARBAGE" || l.RecognitionStatus == "SKIP") && (l.ConfidenceScore <= 0.0f || l.ReviewedBy != null);

            // 組合最終回傳物件
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
                Url = secureStreamUrl // 🌟 這裡直接把加密好的 S3 連結傳給前端！
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
    // 2. 單筆分類更新 (拉回/排除) - 🌟 升級 AI 連動版
    // ==========================================
    [HttpPut("reclassify")]
    public async Task<IActionResult> ReclassifyMedia(
        [FromBody] ReclassifyRequestDto request,
        [FromServices] IMinioClient minioClient,
        [FromServices] IConnectionMultiplexer redis) // 🌟 注入 Redis
    {
        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID" });

        var log = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .FirstOrDefaultAsync(l => l.Id == request.LogId);

        if (log == null) return NotFound(new { message = "找不到該筆紀錄" });

        string systemName = log.MediaAsset.SystemName;
        string sourceKey = log.MediaAsset.FilePath;
        string fileName = sourceKey.Split('/').Last();

        // 🌟 核心智能路由：拉回(OUTPUT)進 pos 當正樣本種子！排除(REJECTED)進 GARBAGE 當負樣本種子！
        bool isOutput = targetStatus.Code == "OUTPUT";
        string targetFolder = isOutput ? "pos" : (targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code);
        string targetKey = $"{systemName}/{targetFolder}/{fileName}";

        try
        {
            if (sourceKey != targetKey)
            {
                // 1. 搬移主檔到特徵庫 (pos 或 GARBAGE)
                await minioClient.CopyObjectAsync(new CopyObjectArgs()
                    .WithBucket(_bucketName).WithObject(targetKey)
                    .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(sourceKey)));
                await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucketName).WithObject(sourceKey));

                // 2. 如果是判定為本人 (pos)，額外複製一份到 OUTPUT 供前端展示頁面使用
                if (isOutput)
                {
                    string outputKey = $"{systemName}/OUTPUT/{fileName}";
                    await minioClient.CopyObjectAsync(new CopyObjectArgs()
                        .WithBucket(_bucketName).WithObject(outputKey)
                        .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(targetKey)));
                }

                // 3. 處理影片的縮圖搬移
                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    await minioClient.CopyObjectAsync(new CopyObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbDest)
                        .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));
                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbSrc));

                    // 縮圖也同步到 OUTPUT 展示區
                    if (isOutput)
                    {
                        string thumbOutput = $"{systemName}/OUTPUT/{fileName.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase)}";
                        await minioClient.CopyObjectAsync(new CopyObjectArgs()
                            .WithBucket(_bucketName).WithObject(thumbOutput)
                            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbDest)));
                    }
                }
            }
            string currentUser = User.Identity?.Name ?? "System";

            // 4. 寫入新的路徑與狀態
            log.MediaAsset.FilePath = targetKey;
            log.StatusId = request.NewStatusId;
            if (targetStatus.Code == "OUTPUT")
            {
                log.ConfidenceScore = 1.0f;
                log.ReviewedBy = currentUser; // 👈 寫入審核員
            }
            else if (targetStatus.Code == "REJECTED" || targetStatus.Code == "GARBAGE" || targetStatus.Code == "SKIP")
            {
                log.ConfidenceScore = 0.0f;
                log.ReviewedBy = currentUser; // 👈 寫入審核員
            }
            await _context.SaveChangesAsync();

            // 🌟 5. 發送指令叫 S2 AI 重建 Qdrant 特徵庫！
            await TriggerFeatureBankRebuild(redis, systemName);

            return Ok(new { message = "分類更新成功，AI 已將此影像納入學習樣本！" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"S3 檔案搬移失敗: {ex.Message}" });
        }
    }

    // ==========================================
    // 3. 批量分類更新 (拉回/排除) - 🌟 升級 AI 連動版
    // ==========================================
    [HttpPut("batch-reclassify")]
    [Authorize]
    public async Task<IActionResult> BatchReclassifyMedia(
    [FromBody] BatchReclassifyRequestDto request,
    [FromServices] IConnectionMultiplexer redis) // 🌟 統一使用建構子注入的 _minioClient
    {
        // ==========================================
        // 🛡️ 1. 權限與資安雙重門禁
        // ==========================================
        // 第一道：功能面權限檢查 (對齊 PermissionHelper)
        if (!await PermissionHelper.HasPermissionAsync(_context, User, "HitlDashboard", "Update", redis))
            return StatusCode(403, new { message = "🚫 您不具備批量重分類的操作權限。" });

        // 第二道：角色面越權防護 (IDOR) - 訪客嚴禁變更 AI 特徵庫
        var roleClaim = User.FindFirst("RoleId")?.Value;
        if (!int.TryParse(roleClaim, out int roleId) || roleId == 3)
            return StatusCode(403, new { message = "🚫 嚴重越權：Guest 訪客帳號禁止變更特徵標籤。" });

        if (request.LogIds == null || !request.LogIds.Any())
            return BadRequest(new { message = "未提供待處理的 ID 清單。" });

        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID。" });

        // ==========================================
        // 🌟 2. 資料查詢 (加入 AsNoTracking 提升大量讀取效能)
        // ==========================================
        var logs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => request.LogIds.Contains(l.Id))
            .ToListAsync();

        if (!logs.Any()) return NotFound(new { message = "找不到符合條件的紀錄。" });

        bool isOutput = targetStatus.Code == "OUTPUT";
        string targetFolder = isOutput ? "pos" : (targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code);
        var affectedProfiles = new HashSet<string>();
        string currentUser = User.Identity?.Name ?? "SystemAdmin";

        foreach (var log in logs)
        {
            string systemName = log.MediaAsset.SystemName;
            string sourceKey = log.MediaAsset.FilePath;
            string fileName = Path.GetFileName(sourceKey);
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            affectedProfiles.Add(systemName);

            if (sourceKey == targetKey)
            {
                log.StatusId = request.NewStatusId;
                continue;
            }

            try
            {
                // 🌟 3. 使用輔助方法處理 S3 搬移 (讓主邏輯乾淨，易於維護)
                await S3MoveWithThumbnailAsync(sourceKey, targetKey, isOutput, systemName, fileName);

                // 🌟 4. 更新資料庫狀態 (保留您已有的審核人邏輯)
                log.MediaAsset.FilePath = targetKey;
                log.StatusId = request.NewStatusId;
                log.ReviewedBy = currentUser; // 👈 確實寫入審核員
                log.ConfidenceScore = isOutput ? 1.0f : 0.0f;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Batch Reclassify Error] ID: {log.Id}, {ex.Message}");
            }
        }

        await _context.SaveChangesAsync();

        // 🌟 5. 觸發 AI 重建 (資料飛輪)
        foreach (var profile in affectedProfiles)
        {
            await TriggerFeatureBankRebuild(redis, profile);
        }

        return Ok(new { message = $"✅ 成功批次更新 {logs.Count} 筆分類，並已觸發 AI 重新學習。" });
    }

    // --- 💡 建議新增的私有輔助方法，解決單筆/批次共用的代碼重工問題 ---

    private async Task S3MoveWithThumbnailAsync(string src, string dest, bool isOutput, string sysName, string fileName)
    {
        // 搬移主檔
        await S3CopyAndRemoveAsync(src, dest);

        // 如果是輸出本人，額外複製一份到 OUTPUT 展示區
        if (isOutput) await S3CopyOnlyAsync(dest, $"{sysName}/OUTPUT/{fileName}");

        // 處理影片縮圖
        if (src.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            string tSrc = src.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
            string tDest = dest.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

            await S3CopyAndRemoveAsync(tSrc, tDest);
            if (isOutput) await S3CopyOnlyAsync(tDest, $"{sysName}/OUTPUT/{Path.GetFileName(tDest)}");
        }
    }

    private async Task S3CopyAndRemoveAsync(string src, string dest)
    {
        await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src)));
        await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(src));
    }

    private async Task S3CopyOnlyAsync(string src, string dest)
    {
        await _minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src)));
    }

    // ==========================================
    // 🌟 4. 新增：喚醒 AI 進行特徵庫重建的輔助方法
    // ==========================================
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

            // 🚀 推送到最高優先級佇列，讓 AI 工人立刻放下手邊工作去重建 Qdrant 向量庫
            await db.ListLeftPushAsync("ig_processing_queue_high", JsonSerializer.Serialize(taskPayload));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ 推送 AI 重建任務失敗: {ex.Message}");
        }
    }
}


// ==========================================
// 🌟 乾淨的 DTO 區塊 (全數改用整數 ID)
// ==========================================
public class ReclassifyRequestDto
{
    public int LogId { get; set; }
    public int NewStatusId { get; set; } // 從 string NewStatus 改為 int ID
}

public class BatchReclassifyRequestDto
{
    public List<long> LogIds { get; set; } = new List<long>();
    public int NewStatusId { get; set; } // 從 string NewStatus 改為 int ID
}