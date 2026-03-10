using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using IgAiBackend.Helpers;
namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ConfigController> _logger;
    private readonly string _bucketName;
    private readonly string _minioEndpoint;

    // ConfigController.cs 建構子建議修改

    // ConfigController.cs 的建構子修改
    public ConfigController(ApplicationDbContext context, IConnectionMultiplexer redis, ILogger<ConfigController> logger)
    {
        _context = context;
        _redis = redis;
        _logger = logger;

        // 🌟 核心修正：改為從環境變數讀取，確保對接 .env 內容
        _bucketName = Environment.GetEnvironmentVariable("MINIO_BUCKET_NAME") ?? "ig-ai-assets";
        _minioEndpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT") ?? "localhost:9000";
    }

    // ==========================================
    // 🌟 2. 獲取人物清單 (包含手動上傳的特徵照片)
    // ==========================================
    [HttpGet("persons")]
    public async Task<IActionResult> GetAllPersons()
    {
        // =========================================================
        // 1. 第一段查詢：取得人物與其關聯的社群帳號 (乾淨俐落的 SQL)
        // =========================================================
        var personsDb = await _context.TargetPersons
            .Include(p => p.SocialAccounts)
            .ThenInclude(sa => sa.Platform)
            .ToListAsync(); // 直接拉回記憶體

        // =========================================================
        // 2. 第二段查詢：取得所有手動上傳的特徵照片 (避免在 Select 裡面跨表)
        // =========================================================
        var avatarsDb = await _context.MediaAssets
            .Where(m => m.SourceType != null && m.SourceType.Code == "MANUAL_UPLOAD")
            .Select(m => new { m.SystemName, m.Id, m.FilePath })
            .ToListAsync(); // 直接拉回記憶體

        // 將照片依照 SystemName 分組，轉換成 Dictionary 讓後續配對瞬間完成
        var avatarsDict = avatarsDb
            .GroupBy(a => a.SystemName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // =========================================================
        // 3. 記憶體組合 (LINQ to Objects)：完全沒有 EF Core 翻譯報錯風險
        // =========================================================
        var result = personsDb.Select(p => new
        {
            p.Id,
            p.SystemName,
            p.DisplayName,
            p.Threshold,
            p.IsActive,
            
            // 組合帳號
            Accounts = p.SocialAccounts.Select(sa => new 
            { 
                sa.Id, 
                sa.AccountIdentifier, 
                sa.AccountName, 
                sa.IsMonitored, 
                PlatformId = sa.PlatformId, 
                PlatformCode = sa.Platform?.Code,
                AccountTypeId = sa.AccountTypeId 
            }),

            // 🌟 修正：加上 (object) 強制轉型，解決編譯器三元運算子型別不一致的錯誤
            avatars = avatarsDict.TryGetValue(p.SystemName, out var avatarList)
                ? avatarList.Select(a => (object)new
                {
                    id = a.Id,
                    url = $"http://{_minioEndpoint}/{_bucketName}/{a.FilePath}"
                }).ToList()
                : new List<object>()
        });

        return Ok(result);
    }

    [HttpPost("persons")]
    public async Task<IActionResult> CreatePerson([FromBody] CreatePersonDto dto)
    {
        if (await _context.TargetPersons.AnyAsync(p => p.SystemName == dto.SystemName)) return BadRequest(new { message = "系統代號已存在" });

        var person = new TargetPerson { SystemName = dto.SystemName, DisplayName = dto.DisplayName, Threshold = dto.Threshold ?? 0.45, IsActive = true };
        _context.TargetPersons.Add(person);
        await _context.SaveChangesAsync();

        if (dto.InitialAccount != null && !string.IsNullOrEmpty(dto.InitialAccount.Identifier))
        {
            _context.SocialAccounts.Add(new SocialAccount { PersonId = person.Id, PlatformId = dto.InitialAccount.PlatformId, AccountTypeId = dto.InitialAccount.AccountTypeId, AccountIdentifier = dto.InitialAccount.Identifier, IsMonitored = true });
            await _context.SaveChangesAsync();
        }
        return Ok(person);
    }

    [HttpPut("persons/{id}")]
    public async Task<IActionResult> UpdatePerson(int id, [FromBody] UpdatePersonDto dto)
    {
        var person = await _context.TargetPersons.FindAsync(id);
        if (person == null) return NotFound(new { message = "找不到該人物" });
        if (dto.DisplayName != null) person.DisplayName = dto.DisplayName;
        if (dto.Threshold.HasValue) person.Threshold = dto.Threshold.Value;
        if (dto.IsActive.HasValue) person.IsActive = dto.IsActive.Value;
        await _context.SaveChangesAsync();
        return Ok(new { message = "人物設定已更新" });
    }

    [HttpPost("persons/{personId}/accounts")]
    public async Task<IActionResult> AddAccount(int personId, [FromBody] AddAccountDto dto)
    {
        var exists = await _context.SocialAccounts.AnyAsync(sa => sa.PersonId == personId && sa.PlatformId == dto.PlatformId && sa.AccountIdentifier == dto.AccountIdentifier);
        if (exists) return BadRequest(new { message = "該帳號已存在" });
        _context.SocialAccounts.Add(new SocialAccount { PersonId = personId, PlatformId = dto.PlatformId, AccountTypeId = dto.AccountTypeId ?? 1, AccountIdentifier = dto.AccountIdentifier, AccountName = dto.AccountName, IsMonitored = true });
        await _context.SaveChangesAsync();
        return Ok(new { message = "已成功新增社群帳號" });
    }

    [HttpDelete("accounts/{accountId}")]
    public async Task<IActionResult> DeleteAccount(int accountId)
    {
        var account = await _context.SocialAccounts.FindAsync(accountId);
        if (account == null) return NotFound();
        _context.SocialAccounts.Remove(account);
        await _context.SaveChangesAsync();
        return Ok(new { message = "社群帳號已移除" });
    }

    [HttpDelete("persons/{id}")]
    public async Task<IActionResult> DeletePerson(int id)
    {
        var person = await _context.TargetPersons.FindAsync(id);
        if (person == null) return NotFound();
        _context.TargetPersons.Remove(person);
        await _context.SaveChangesAsync();
        return Ok(new { message = "已永久刪除該人物與其關聯的社群帳號" });
    }

    [HttpGet("platforms")]
    public async Task<IActionResult> GetPlatforms() => Ok(await _context.Platforms.Select(p => new { p.Id, p.Name, p.Code }).ToListAsync());

    [HttpGet("account-types")]
    public async Task<IActionResult> GetAccountTypes() => Ok(await _context.AccountTypes.Select(t => new { t.Id, t.Name, t.Code }).ToListAsync());

    [HttpPut("accounts/{accountId}")]
    public async Task<IActionResult> UpdateAccount(int accountId, [FromBody] UpdateAccountDto dto)
    {
        var account = await _context.SocialAccounts.FindAsync(accountId);
        if (account == null) return NotFound();
        if (dto.AccountName != null) account.AccountName = dto.AccountName;
        if (dto.AccountIdentifier != null) account.AccountIdentifier = dto.AccountIdentifier;
        if (dto.PlatformId.HasValue) account.PlatformId = dto.PlatformId.Value;
        if (dto.AccountTypeId.HasValue) account.AccountTypeId = dto.AccountTypeId.Value;
        if (dto.IsMonitored.HasValue) account.IsMonitored = dto.IsMonitored.Value;
        await _context.SaveChangesAsync();
        return Ok(new { message = "帳號更新成功" });
    }

    // ==========================================
    // 🌟 11. 手動上傳 (加入 5 張數量限制防護)
    // ==========================================
   [HttpPost("persons/{systemName}/upload")]
    public async Task<IActionResult> UploadManualPhotos(string systemName, List<IFormFile> files, [FromServices] IMinioClient minioClient)
    {
        if (!FileValidationHelper.IsValid(files, out string error)) return BadRequest(new { message = error });

        var person = await _context.TargetPersons.FirstOrDefaultAsync(p => p.SystemName == systemName);
        if (person == null) return NotFound(new { message = "找不到該目標人物" });

        // 🌟 1. 預先查好所有的狀態 ID，避免在迴圈內重複查詢
        var imgId = await _context.SysStatuses.Where(s => s.Category == "MEDIA_TYPE" && s.Code == "IMAGE").Select(s => s.Id).FirstOrDefaultAsync();
        var vidId = await _context.SysStatuses.Where(s => s.Category == "MEDIA_TYPE" && s.Code == "VIDEO").Select(s => s.Id).FirstOrDefaultAsync();
        var manualId = await _context.SysStatuses.Where(s => s.Category == "SOURCE_TYPE" && s.Code == "MANUAL_UPLOAD").Select(s => s.Id).FirstOrDefaultAsync();
        var dlId = await _context.SysStatuses.Where(s => s.Category == "DOWNLOAD_STATUS" && s.Code == "DOWNLOADED").Select(s => s.Id).FirstOrDefaultAsync();
        var seedId = await _context.SysStatuses.Where(s => s.Category == "AI_RECOGNITION" && s.Code == "SEED_PHOTO").Select(s => s.Id).FirstOrDefaultAsync();

        int currentCount = await _context.MediaAssets.CountAsync(m => m.SystemName == systemName && m.SourceTypeId == manualId);
        if (currentCount + files.Count > 5) return BadRequest(new { message = $"該人物已達到 5 張特徵圖上限，目前已有 {currentCount} 張。" });

        try
        {
            var db = _redis.GetDatabase();
            int successCount = 0;

            foreach (var file in files)
            {
                if (file.Length == 0) continue;
                string fileName = $"manual_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{file.FileName}";
                string headerKey = $"{systemName}/profile_header/{fileName}"; 
                string posKey = $"{systemName}/pos/{fileName}";               

                using (var stream = file.OpenReadStream())
                {
                    await minioClient.PutObjectAsync(new PutObjectArgs().WithBucket(_bucketName).WithObject(headerKey).WithStreamData(stream).WithObjectSize(file.Length).WithContentType(file.ContentType));
                }

                await minioClient.CopyObjectAsync(new CopyObjectArgs().WithBucket(_bucketName).WithObject(posKey).WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(headerKey)));

                // 🌟 2. 改用 ID 寫入 MediaAssets
                var newMedia = new MediaAsset
                {
                    PersonId = person.Id,
                    SystemName = systemName,
                    FileName = fileName,
                    FilePath = headerKey,
                    MediaTypeId = file.ContentType.Contains("video") ? vidId : imgId,
                    SourceTypeId = manualId,
                    DownloadStatusId = dlId,
                    CreatedAt = DateTime.UtcNow
                };
                _context.MediaAssets.Add(newMedia);
                await _context.SaveChangesAsync();

                // 🌟 3. 改用 ID 寫入 AiAnalysisLog
                _context.AiAnalysisLogs.Add(new AiAnalysisLog
                {
                    MediaId = newMedia.Id,
                    StatusId = seedId,
                    ConfidenceScore = 1.0f,
                    ProcessedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                var taskPayload = new { task_id = newMedia.Id, profile = systemName, file_path = posKey, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), stage = "MANUAL_UPLOAD" };
                await db.ListLeftPushAsync("ig_processing_queue", JsonSerializer.Serialize(taskPayload));
                successCount++;
            }
            return Ok(new { message = $"成功上傳 {successCount} 張照片並同步至訓練區" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[致命錯誤] 上傳失敗: {ex.Message}");
            return StatusCode(500, new { message = "伺服器內部錯誤", error = ex.Message });
        }
    }

    // ==========================================
    // 🌟 12. 刪除手動上傳的特徵照片
    // ==========================================
    [HttpDelete("persons/media/{mediaId}")]
    public async Task<IActionResult> DeleteManualPhoto(long mediaId, [FromServices] IMinioClient minioClient)
    {
        // 🌟 修正：media_assets 的 id 是 bigint，這裡必須用 long 接收
        var media = await _context.MediaAssets.FindAsync(mediaId);
        if (media == null) return NotFound(new { message = "找不到該照片" });

        try
        {
            await minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(media.FilePath));
            // 建議同時刪除 pos 下的備份檔 (可選)
            //string posPath = media.FilePath.Replace("/profile_header/", "/pos/");
            //await minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(posPath));
        }
        catch { /* 忽略 S3 找不到檔案的錯誤 */ }

        _context.MediaAssets.Remove(media);
        await _context.SaveChangesAsync();
        return Ok(new { message = "照片已成功移除" });
    }
}

public class CreatePersonDto { public required string SystemName { get; set; } public string? DisplayName { get; set; } public double? Threshold { get; set; } public InitialAccountDto? InitialAccount { get; set; } }
public class UpdatePersonDto { public string? DisplayName { get; set; } public double? Threshold { get; set; } public bool? IsActive { get; set; } }
public class AddAccountDto { public required string AccountIdentifier { get; set; } public string? AccountName { get; set; } public int? AccountTypeId { get; set; } public int PlatformId { get; set; } }
public class InitialAccountDto { public int PlatformId { get; set; } public int AccountTypeId { get; set; } public string Identifier { get; set; } = string.Empty; }
public class UpdateAccountDto { public string? AccountName { get; set; } public string? AccountIdentifier { get; set; } public int? PlatformId { get; set; } public int? AccountTypeId { get; set; } public bool? IsMonitored { get; set; } }