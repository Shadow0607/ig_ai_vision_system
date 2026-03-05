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

    [HttpGet("classified")]
    public async Task<IActionResult> GetClassifiedMedia(
        [FromQuery] string status = "OUTPUT", 
        [FromQuery] string? systemName = null,
        [FromQuery] int page = 1,         // 🌟 新增：當前頁碼 (預設第 1 頁)
        [FromQuery] int pageSize = 50)    // 🌟 新增：每頁筆數 (預設 50 筆)
    {
        var query = _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .AsQueryable();
        if (status == "REJECTED") {
            query = query.Where(l => l.RecognitionStatus == "REJECTED" || l.RecognitionStatus == "GARBAGE" || l.RecognitionStatus == "SKIP");
        } else {
            query = query.Where(l => l.RecognitionStatus == status);
        }

        // 🌟 1. 計算總筆數與總頁數
        int totalItems = await query.CountAsync();
        int totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        // 🌟 2. 進行分頁切割 (Skip & Take)
        var items = await query
            .OrderByDescending(l => l.ProcessedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.ConfidenceScore,
                l.ProcessedAt,
                l.RecognitionStatus,
                SystemName = l.MediaAsset.SystemName,
                FileName = l.MediaAsset.FileName,
                Url = $"http://{_minioEndpoint}/{_bucketName}/{l.MediaAsset.FilePath}"
            })
            .ToListAsync();

        // 🌟 3. 回傳包含分頁資訊的複合式物件
        return Ok(new 
        { 
            Items = items, 
            TotalItems = totalItems, 
            TotalPages = totalPages, 
            CurrentPage = page 
        });
    }
    // 1. 修正 DTO：給予預設值解決 Null 警告
    public class ReclassifyRequestDto
    {
        public int LogId { get; set; }
        public string NewStatus { get; set; } = string.Empty; // 🌟 加上預設值
    }

    // 2. 修正 MinIO 搬移邏輯
    [HttpPut("reclassify")]
    public async Task<IActionResult> ReclassifyMedia([FromBody] ReclassifyRequestDto request, [FromServices] IMinioClient minioClient)
    {
        var log = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .FirstOrDefaultAsync(l => l.Id == request.LogId);

        if (log == null) return NotFound(new { message = "找不到該筆紀錄" });

        string systemName = log.MediaAsset.SystemName;
        string sourceKey = log.MediaAsset.FilePath;
        string fileName = sourceKey.Split('/').Last();

        // 決定目標資料夾
        string targetFolder = request.NewStatus == "OUTPUT" ? "OUTPUT" :
                              request.NewStatus == "REJECTED" ? "GARBAGE" : request.NewStatus;

        string targetKey = $"{systemName}/{targetFolder}/{fileName}";

        try
        {
            if (sourceKey != targetKey)
            {
                // 🌟 修正：改用 WithCopyObjectSource
                await minioClient.CopyObjectAsync(new CopyObjectArgs()
                    .WithBucket(_bucketName).WithObject(targetKey)
                    .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(sourceKey)));

                await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                    .WithBucket(_bucketName).WithObject(sourceKey));

                if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
                    string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

                    // 🌟 修正：改用 WithCopyObjectSource
                    await minioClient.CopyObjectAsync(new CopyObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbDest)
                        .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(thumbSrc)));

                    await minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                        .WithBucket(_bucketName).WithObject(thumbSrc));
                }
            }

            // 更新資料庫
            log.MediaAsset.FilePath = targetKey;
            log.RecognitionStatus = request.NewStatus;
            await _context.SaveChangesAsync();

            return Ok(new { message = "分類更新成功" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"S3 檔案搬移失敗: {ex.Message}" });
        }
    }
    // 2. 在 MediaAssetsController 類別內加入批量處理 API
    [HttpPut("batch-reclassify")]
    public async Task<IActionResult> BatchReclassifyMedia([FromBody] BatchReclassifyRequestDto request, [FromServices] IMinioClient minioClient)
    {
        if (request.LogIds == null || !request.LogIds.Any())
            return BadRequest(new { message = "未提供任何 ID" });

        var logs = await _context.AiAnalysisLogs
            .Include(l => l.MediaAsset)
            .Where(l => request.LogIds.Contains(l.Id))
            .ToListAsync();

        string targetFolder = request.NewStatus == "OUTPUT" ? "OUTPUT" :
                              request.NewStatus == "REJECTED" ? "GARBAGE" : request.NewStatus;

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

                log.MediaAsset.FilePath = targetKey;
                log.RecognitionStatus = request.NewStatus;
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
public class ReclassifyRequestDto
{
    public int LogId { get; set; }
    public string NewStatus { get; set; } = string.Empty;// "OUTPUT", "REJECTED" 等
}

public class BatchReclassifyRequestDto
{
    public List<long> LogIds { get; set; } = new List<long>(); // 🌟 這裡把 List<int> 改成 List<long>
    public string NewStatus { get; set; } = string.Empty;
}