using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.StaticFiles;
using StackExchange.Redis;
using System.Text.Json;

namespace IgAiBackend.Controllers;
[ApiController]
[Route("api/auth/roles")]
public class RolesController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public RolesController(ApplicationDbContext context) => _context = context;

    // R - 取得所有角色
    // R - 取得所有角色
    [HttpGet]
    public async Task<IActionResult> GetAll() 
    {
        var roles = await _context.Roles
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Code,
                UserCount = r.Users.Count() // 🌟 讓資料庫直接幫我們算好數量
            })
            .ToListAsync();

        return Ok(roles);
    }

    // C - 新增角色
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Models.Role role)
    {
        if (await _context.Roles.AnyAsync(r => r.Code == role.Code))
            return BadRequest("角色代碼已存在");
            
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();
        return Ok(role);
    }

    // U - 更新角色
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] Models.Role roleData)
    {
        var role = await _context.Roles.FindAsync(id);
        if (role == null) return NotFound();

        role.Name = roleData.Name;
        role.Code = roleData.Code; // 注意：修改 Code 可能影響現有邏輯
        
        await _context.SaveChangesAsync();
        return Ok(role);
    }

    // D - 刪除角色
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var role = await _context.Roles.Include(r => r.Users).FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound();
        if (role.Users.Any()) return BadRequest("無法刪除已有使用者關聯的角色");

        _context.Roles.Remove(role);
        await _context.SaveChangesAsync();
        return Ok();
    }
}