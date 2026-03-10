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
namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // 確保必須登入才能取得媒體
public class MediaAssetsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName = "ig-ai-assets";

    public MediaAssetsController(ApplicationDbContext context, IMinioClient minioClient,IOptions<MinioSettings> minioSettings)
    {
        _context = context;
        _minioClient = minioClient;
        _bucketName = minioSettings.Value.BucketName;
    }
    [HttpGet("{mediaId}/stream")]
    public async Task<IActionResult> GetMediaStreamUrl(long mediaId)
    {
        try
        {
            // 1. 極速查詢資料庫，確認檔案存在 (使用 AsNoTracking 提升效能)
            var media = await _context.MediaAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == mediaId);

            if (media == null)
            {
                return NotFound(new { message = "找不到指定的媒體資產。" });
            }

            if (string.IsNullOrEmpty(media.FilePath))
            {
                return BadRequest(new { message = "該媒體的實體檔案路徑異常。" });
            }

            // 2. 建立 MinIO 預先簽名物件請求參數 (時效設定為 3600 秒 = 1 小時)
            var presignedArgs = new PresignedGetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(media.FilePath) // 這裡的 FilePath 必須是 S3 的 Object Key (例如: "dlwlrma/video123.mp4")
                .WithExpiry(3600);

            // 3. 呼叫 NAS 產生時效性安全連結
            string streamUrl = await _minioClient.PresignedGetObjectAsync(presignedArgs);

            // 4. 回傳給前端
            return Ok(new 
            { 
                mediaId = media.Id,
                streamUrl = streamUrl,
                expiresIn = 3600
            });
        }
        catch (Minio.Exceptions.MinioException e)
        {
            // 攔截 NAS 連線異常
            return StatusCode(500, new { message = $"NAS 儲存服務連線異常: {e.Message}" });
        }
        catch (Exception ex)
        {
            // 攔截其他系統異常
            return StatusCode(500, new { message = $"產生串流連結失敗: {ex.Message}" });
        }
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
    public async Task<IActionResult> BatchReclassifyMedia(
        [FromBody] BatchReclassifyRequestDto request,
        [FromServices] IMinioClient minioClient,
        [FromServices] IConnectionMultiplexer redis) // 🌟 注入 Redis
    {
        if (request.LogIds == null || !request.LogIds.Any())
            return BadRequest(new { message = "未提供任何 ID" });

        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID" });

        var logs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => request.LogIds.Contains(l.Id))
            .ToListAsync();

        bool isOutput = targetStatus.Code == "OUTPUT";
        string targetFolder = isOutput ? "pos" : (targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code);
        var affectedProfiles = new HashSet<string>(); // 紀錄需要重建特徵庫的人物

        foreach (var log in logs)
        {
            string systemName = log.MediaAsset.SystemName;
            string sourceKey = log.MediaAsset.FilePath;
            string fileName = sourceKey.Split('/').Last();
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            affectedProfiles.Add(systemName);

            if (sourceKey == targetKey)
            {
                log.StatusId = request.NewStatusId;
                continue;
            }

            try
            {
                await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(targetKey).WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(sourceKey)));
                await minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(sourceKey));

                if (isOutput)
                {
                    await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject($"{systemName}/OUTPUT/{fileName}").WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(targetKey)));
                }

                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(thumbDest).WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));
                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc));

                    if (isOutput)
                    {
                        await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject($"{systemName}/OUTPUT/{fileName.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase)}").WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbDest)));
                    }
                }
                string currentUser = User.Identity?.Name ?? "System";

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Batch Move Error] ID: {log.Id}, {ex.Message}");
            }
        }

        await _context.SaveChangesAsync();

        // 🌟 批次發送重建指令
        foreach (var profile in affectedProfiles)
        {
            await TriggerFeatureBankRebuild(redis, profile);
        }

        return Ok(new { message = $"成功更新 {logs.Count} 筆分類，並已觸發 AI 重新學習！" });
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