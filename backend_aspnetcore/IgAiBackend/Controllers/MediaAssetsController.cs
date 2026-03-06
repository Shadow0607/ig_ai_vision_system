using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using Microsoft.AspNetCore.Authorization;
using Minio;
using Minio.DataModel.Args;

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
    // 2. 單筆分類更新 (拉回/排除)
    // ==========================================
    [HttpPut("reclassify")]
    public async Task<IActionResult> ReclassifyMedia([FromBody] ReclassifyRequestDto request, [FromServices] IMinioClient minioClient)
    {
        // 🌟 1. 修正：直接用前端傳來的 ID 查詢系統狀態實體
        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID" });

        var log = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .FirstOrDefaultAsync(l => l.Id == request.LogId);

        if (log == null) return NotFound(new { message = "找不到該筆紀錄" });

        string systemName = log.MediaAsset.SystemName;
        string sourceKey = log.MediaAsset.FilePath;
        string fileName = sourceKey.Split('/').Last();

        // 🌟 2. 修正：透過查出來的 Code 決定資料夾走向
        string targetFolder = targetStatus.Code == "OUTPUT" ? "OUTPUT" :
                              targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code;

        string targetKey = $"{systemName}/{targetFolder}/{fileName}";

        try
        {
            if (sourceKey != targetKey)
            {
                await minioClient.CopyObjectAsync(new CopyObjectArgs()
                    .WithBucket(_bucketName).WithObject(targetKey)
                    .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(sourceKey)));

                await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucketName).WithObject(sourceKey));

                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    await minioClient.CopyObjectAsync(new CopyObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbDest)
                        .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));

                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbSrc));
                }
            }

            // 🌟 3. 寫入新的 StatusId
            log.MediaAsset.FilePath = targetKey;
            log.StatusId = request.NewStatusId; 
            await _context.SaveChangesAsync();

            return Ok(new { message = "分類更新成功" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"S3 檔案搬移失敗: {ex.Message}" });
        }
    }

    // ==========================================
    // 3. 批量分類更新 (拉回/排除)
    // ==========================================
    [HttpPut("batch-reclassify")]
    public async Task<IActionResult> BatchReclassifyMedia([FromBody] BatchReclassifyRequestDto request, [FromServices] IMinioClient minioClient)
    {
        if (request.LogIds == null || !request.LogIds.Any())
            return BadRequest(new { message = "未提供任何 ID" });

        // 🌟 1. 修正：直接用前端傳來的 ID 查詢系統狀態實體
        var targetStatus = await _context.SysStatuses.FindAsync(request.NewStatusId);
        if (targetStatus == null) return BadRequest(new { message = "無效的狀態 ID" });

        var logs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => request.LogIds.Contains(l.Id))
            .ToListAsync();

        // 🌟 2. 修正：透過查出來的 Code 決定資料夾走向
        string targetFolder = targetStatus.Code == "OUTPUT" ? "OUTPUT" :
                              targetStatus.Code == "REJECTED" ? "GARBAGE" : targetStatus.Code;

        foreach (var log in logs)
        {
            string systemName = log.MediaAsset.SystemName;
            string sourceKey = log.MediaAsset.FilePath;
            string fileName = sourceKey.Split('/').Last();
            string targetKey = $"{systemName}/{targetFolder}/{fileName}";

            if (sourceKey == targetKey) continue;

            try
            {
                await minioClient.CopyObjectAsync(new CopyObjectArgs()
                    .WithBucket(_bucketName).WithObject(targetKey)
                    .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(sourceKey)));

                await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucketName).WithObject(sourceKey));

                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    await minioClient.CopyObjectAsync(new CopyObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbDest)
                        .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));

                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbSrc));
                }

                // 🌟 3. 寫入新的 StatusId
                log.MediaAsset.FilePath = targetKey;
                log.StatusId = request.NewStatusId; 
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Batch Move Error] ID: {log.Id}, {ex.Message}");
            }
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = $"成功更新 {logs.Count} 筆分類" });
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