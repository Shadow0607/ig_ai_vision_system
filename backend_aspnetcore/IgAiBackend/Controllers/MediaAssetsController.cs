using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using Microsoft.AspNetCore.Authorization;
using Minio;
using Minio.DataModel.Args;
using StackExchange.Redis;       // 🌟 新增：用於推播 AI 任務
using System.Text.Json;          // 🌟 新增：用於序列化 JSON Payload

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MediaAssetsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly string _minioEndpoint = "localhost:9000"; // 應從 Config 讀取
    private readonly string _bucketName = "ig-ai-assets";

    public MediaAssetsController(ApplicationDbContext context)
    {
        _context = context;
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
            .Include(l => l.Status) // 🌟 必須 Include Status，才能讀取到 Code
            .AsQueryable();

        // 🌟 修正：利用 Status!.Code 進行字串判斷
        if (status == "REJECTED") {
            query = query.Where(l => l.Status!.Code == "REJECTED" || l.Status!.Code == "GARBAGE" || l.Status!.Code == "SKIP");
        } else if (status == "ALL") {
            query = query.Where(l => l.Status!.Code != "DOWNLOADED"); // ALL 的情況
        } else {
            query = query.Where(l => l.Status!.Code == status);
        }

        if (!string.IsNullOrEmpty(systemName))
        {
            query = query.Where(l => l.MediaAsset.SystemName == systemName);
        }

        int totalItems = await query.CountAsync();
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var items = await query
            .OrderByDescending(l => l.ProcessedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.ConfidenceScore,
                l.ProcessedAt,
                RecognitionStatus = l.Status!.Code, // 保留 Code 讓前端寫邏輯判斷 (如顯示/隱藏按鈕)
                
                // 🌟 架構師優化：直接將動態字典餵給前端，解放前端 Hardcode
                StatusName = l.Status!.DisplayName,
                StatusColor = l.Status!.UiColor,    

                SystemName = l.MediaAsset.SystemName,
                FileName = l.MediaAsset.FileName,
                Url = $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}"
            })
            .ToListAsync();

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

            // 4. 寫入新的路徑與狀態
            log.MediaAsset.FilePath = targetKey;
            log.StatusId = request.NewStatusId; 
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

                if (isOutput) {
                    await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject($"{systemName}/OUTPUT/{fileName}").WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(targetKey)));
                }

                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(thumbDest).WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));
                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc));

                    if (isOutput) {
                        await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject($"{systemName}/OUTPUT/{fileName.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase)}").WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbDest)));
                    }
                }

                log.MediaAsset.FilePath = targetKey;
                log.StatusId = request.NewStatusId; 
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