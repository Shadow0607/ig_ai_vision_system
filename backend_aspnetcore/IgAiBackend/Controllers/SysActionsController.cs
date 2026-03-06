using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Data;   // 請替換成你實際的 DbContext 命名空間
using IgAiBackend.Models; // 請替換成你實際的 Models 命名空間
using System.Security.Claims;

namespace IgAiBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // 確保只有登入的使用者可以存取
    public class SysActionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public SysActionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // =========================================================
        // 1. 取得所有系統動作 (前端動態渲染權限表頭用)
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> GetAllActions()
        {
            var actions = await _context.SysActions
                .OrderBy(a => a.Id) // 確保前端表頭的順序固定 (例如 VIEW 永遠在前面)
                .ToListAsync();

            // 🌟 貼心處理：將屬性名稱對應成前端 Vue 已經寫好的格式
            var result = actions.Select(a => new
            {
                id = a.Id,
                code = a.Code,
                name = a.DisplayName // 對應前端 availableActions 裡的 name
            });

            return Ok(result);
        }

        // =========================================================
        // 2. 新增系統動作 (僅限管理員) - 預留未來擴充用
        // =========================================================
        [HttpPost]
        public async Task<IActionResult> CreateAction([FromBody] SysAction request)
        {
            if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") 
                return Forbid();

            if (await _context.SysActions.AnyAsync(a => a.Code == request.Code.ToUpper()))
            {
                return BadRequest(new { message = "此 Action Code 已存在" });
            }

            var newAction = new SysAction
            {
                Code = request.Code.ToUpper(),
                DisplayName = request.DisplayName
            };

            _context.SysActions.Add(newAction);
            await _context.SaveChangesAsync();

            return Ok(new { message = "動作新增成功", data = newAction });
        }

        // =========================================================
        // 3. 刪除系統動作 (僅限管理員) - 預留未來擴充用
        // =========================================================
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAction(int id)
        {
            if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") 
                return Forbid();

            var action = await _context.SysActions.FindAsync(id);
            if (action == null) return NotFound(new { message = "找不到該動作" });

            // 防呆機制：檢查是否已經有角色綁定了這個動作，避免誤刪導致系統崩潰
            var isUsed = await _context.RolePermissions.AnyAsync(rp => rp.ActionId == id);
            if (isUsed)
            {
                return BadRequest(new { message = "無法刪除！已有角色綁定此動作權限。" });
            }

            _context.SysActions.Remove(action);
            await _context.SaveChangesAsync();

            return Ok(new { message = "動作刪除成功" });
        }
    }
}