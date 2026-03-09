using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;
using Minio;
using Minio.DataModel.Args;
using System.Text.Json;
using System.Security.Claims;

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/repost-review")]
[Authorize] // 必須登入
public class RepostReviewController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName = "ig-ai-assets";

    public RepostReviewController(
        ApplicationDbContext context, 
        IConnectionMultiplexer redis, 
        IMinioClient minioClient)
    {
        _context = context;
        _redis = redis;
        _minioClient = minioClient;
    }

    // ==========================================
    // 1. 取得待審核的轉發與限動清單 (HitlDashboard 呼叫)
    // ==========================================
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingReposts()
    {
        // 優化：只撈取最近 14 天內的資料，避免前端 OOM 崩潰
        var pendingList = await _context.MediaAssets
            .Where(m => m.DownloadStatusId == 25 && m.OriginalUsername != null && m.CreatedAt >= DateTime.UtcNow.AddDays(-14))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new {
                m.Id,
                m.OriginalUsername,
                m.OriginalShortcode,
                m.SourceIsVerified,
                m.SourceTypeId, // 29:POST, 30:REEL, 31:STORY
                ThumbnailUrl = m.FilePath, // STORY 會是 MinIO 隔離路徑，POST 會是 IG URL
                m.CreatedAt
            })
            .ToListAsync();

        return Ok(pendingList);
    }

    // ==========================================
    // 2. 人工決斷：保留 (KEEP) 或 刪除 (DELETE)
    // ==========================================
    [HttpPost("decide")]
    public async Task<IActionResult> DecideRepost([FromBody] DecideRepostDto request)
    {
        // 🛡️ 資安防護：權限校驗 (僅 Admin=1 或 Reviewer=2 可操作)
        var roleIdClaim = User.FindFirst("RoleId")?.Value;
        if (!int.TryParse(roleIdClaim, out int roleId) || roleId > 2)
        {
            return StatusCode(403, new { message = "權限不足，僅允許管理員或審核員操作" });
        }

        var asset = await _context.MediaAssets.FindAsync(request.MediaId);
        if (asset == null) return NotFound(new { message = "找不到該媒體資產" });

        var dbRedis = _redis.GetDatabase();

        // 🌟 導入 Entity Framework 分散式交易，確保 DB 與 Redis 狀態一致
        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            if (request.Action.ToUpper() == "KEEP")
            {
                if (asset.SourceTypeId == 31) 
                {
                    // 【軌道 C】STORY 限時動態 (已在 MinIO 隔離區)
                    // 狀態直接升級為 DOWNLOADED (26)
                    asset.DownloadStatusId = 26; 
                    await _context.SaveChangesAsync();
                    
                    // 推入 AI 處理佇列，啟動 S2 分析
                    var queuePayload = JsonSerializer.Serialize(new { media_id = asset.Id });
                    await dbRedis.ListRightPushAsync("ig_processing_queue", queuePayload);
                }
                else 
                {
                    // 【軌道 B】POST/REEL (Metadata-First，無實體檔案)
                    // 狀態維持 PENDING，但將 Shortcode 推入專屬下載佇列，讓 S1 執行真實下載
                    var payload = JsonSerializer.Serialize(new { 
                        media_id = asset.Id, 
                        shortcode = asset.OriginalShortcode 
                    });
                    await dbRedis.ListRightPushAsync("ig_manual_download_queue", payload);
                }

                await transaction.CommitAsync();
                return Ok(new { message = "✅ 已成功放行" });
            }
            else if (request.Action.ToUpper() == "DELETE")
            {
                // 【清理邏輯】若為 STORY 且檔案在 MinIO 隔離區，必須實體刪除以釋放 NAS 空間
                if (asset.SourceTypeId == 31 && !string.IsNullOrEmpty(asset.FilePath) && asset.FilePath.Contains("quarantine/"))
                {
                    var objectName = asset.FilePath.Split(new[] { _bucketName + "/" }, StringSplitOptions.None).LastOrDefault();
                    if (!string.IsNullOrEmpty(objectName))
                    {
                        await _minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                            .WithBucket(_bucketName)
                            .WithObject(objectName));
                    }
                }

                // 狀態改為 SKIPPED (28)，未來防呆查重時會直接忽略此 Shortcode
                asset.DownloadStatusId = 28; 
                await _context.SaveChangesAsync();
                
                await transaction.CommitAsync();
                return Ok(new { message = "🗑️ 已標記捨棄並清理檔案" });
            }

            return BadRequest(new { message = "無效的 Action 參數" });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            // 可寫入 system_alerts
            return StatusCode(500, new { message = $"系統異常: {ex.Message}" });
        }
    }
}

// ==========================================
// DTO 模型 (防止 Over-posting 漏洞)
// ==========================================
public class DecideRepostDto 
{
    public long MediaId { get; set; }
    public required string Action { get; set; } // "KEEP" 或 "DELETE"
}