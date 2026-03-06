using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;
namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/sys-status")]
[Authorize] // 預設所有端點都需要登入
public class SysStatusController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConnectionMultiplexer _redis;

    public SysStatusController(ApplicationDbContext context, IConnectionMultiplexer redis)
    {
        _context = context;
        _redis = redis;
    }
    private async Task RefreshRedisCacheAsync()
    {
        var statuses = await _context.SysStatuses.ToListAsync();
        if (statuses.Any())
        {
            var dbRedis = _redis.GetDatabase();
            var hashEntries = statuses.Select(s => new HashEntry($"{s.Category}:{s.Code}", s.Id)).ToArray();
            
            await dbRedis.KeyDeleteAsync("sys:statuses");
            await dbRedis.HashSetAsync("sys:statuses", hashEntries);
        }
    }

    // ==========================================
    // 1. [Read] 取得所有狀態列表 (可選傳入 Category 進行過濾)
    // ==========================================
    [HttpGet]
    public async Task<IActionResult> GetAllStatuses([FromQuery] string? category)
    {
        var query = _context.SysStatuses.AsQueryable();

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(s => s.Category == category);
        }

        var statuses = await query
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Id)
            .ToListAsync();

        return Ok(statuses);
    }

    // ==========================================
    // 2. [Read] 依據 Category 取得特定類別的所有狀態 (前端最常用)
    // ==========================================
    [HttpGet("category/{category}")]
    public async Task<IActionResult> GetStatusesByCategory(string category)
    {
        var statuses = await _context.SysStatuses
            .Where(s => s.Category == category)
            .OrderBy(s => s.Id)
            .ToListAsync();

        if (!statuses.Any())
            return NotFound(new { message = $"找不到類別為 {category} 的狀態" });

        return Ok(statuses);
    }

    // ==========================================
    // 3. [Create] 新增一個系統狀態
    // ==========================================
    [HttpPost]
    public async Task<IActionResult> CreateStatus([FromBody] SysStatusCreateDto dto)
    {
        // 🛡️ 建議：這裡應該加入權限檢查，確保只有 Admin 可以新增狀態
        if (!await IsAdmin()) return StatusCode(403, new { message = "🚫 僅限管理員執行此操作。" });

        // 檢查是否已存在相同的 Category + Code
        var exists = await _context.SysStatuses
            .AnyAsync(s => s.Category == dto.Category && s.Code == dto.Code);
        
        if (exists) return BadRequest(new { message = $"已存在相同的狀態碼 ({dto.Category} - {dto.Code})" });

        var newStatus = new SysStatus
        {
            Category = dto.Category.ToUpper(), // 強制轉大寫標準化
            Code = dto.Code.ToUpper(),
            DisplayName = dto.DisplayName,
            UiColor = dto.UiColor
        };

        _context.SysStatuses.Add(newStatus);
        await _context.SaveChangesAsync();
        await RefreshRedisCacheAsync();

        return CreatedAtAction(nameof(GetAllStatuses), new { id = newStatus.Id }, new { message = "狀態建立成功", data = newStatus });
    }

    // ==========================================
    // 4. [Update] 更新系統狀態 (通常只允許改顯示名稱或顏色)
    // ==========================================
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] SysStatusUpdateDto dto)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "🚫 僅限管理員執行此操作。" });

        var status = await _context.SysStatuses.FindAsync(id);
        if (status == null) return NotFound(new { message = "找不到該狀態紀錄" });

        // 注意：基於系統穩定性，通常不允許修改 Category 和 Code，因為程式邏輯可能依賴它們
        status.DisplayName = dto.DisplayName ?? status.DisplayName;
        status.UiColor = dto.UiColor ?? status.UiColor;

        await _context.SaveChangesAsync();
        await RefreshRedisCacheAsync();

        return Ok(new { message = "狀態更新成功", data = status });
    }

    // ==========================================
    // 5. [Delete] 刪除系統狀態
    // ==========================================
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteStatus(int id)
    {
        if (!await IsAdmin()) return StatusCode(403, new { message = "🚫 僅限管理員執行此操作。" });

        var status = await _context.SysStatuses.FindAsync(id);
        if (status == null) return NotFound(new { message = "找不到該狀態紀錄" });

        // ⚠️ 危險操作警告：因為 SysStatus 被許多資料表當作 Foreign Key (例如 AiAnalysisLogs, MediaAssets)
        // 直接刪除可能會觸發關聯錯誤。實務上建議用 try-catch 攔截 FK 衝突。
        try
        {
            _context.SysStatuses.Remove(status);
            await _context.SaveChangesAsync();
            await RefreshRedisCacheAsync();
            return Ok(new { message = "狀態刪除成功" });
        }
        catch (DbUpdateException)
        {
            return BadRequest(new { message = "無法刪除此狀態，因為系統中已有其他資料正在使用它（例如已有圖片被標記為此狀態）。請先清除關聯資料。" });
        }
    }

    // ==========================================
    // 內部權限輔助方法
    // ==========================================
    private async Task<bool> IsAdmin()
    {
        var claim = User.FindFirst("RoleId")?.Value;
        if (int.TryParse(claim, out int roleId))
        {
            // 根據你的 ApplicationDbContext，RoleId = 1 是 Admin
            return roleId == 1; 
        }
        return false;
    }
}

// ==========================================
// DTO 模型 (避免 Over-posting 漏洞)
// ==========================================
public class SysStatusCreateDto
{
    public required string Category { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public string? UiColor { get; set; }
}

public class SysStatusUpdateDto
{
    public string? DisplayName { get; set; }
    public string? UiColor { get; set; }
}