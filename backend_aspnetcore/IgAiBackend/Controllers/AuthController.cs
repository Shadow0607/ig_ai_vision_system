using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography; // 🌟 確保有引入這行

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(ApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    // =========================================================
    // 1. 登入：抓取角色、權限路由清單與發放 Token
    // =========================================================
    [HttpPost("login")]
    [AllowAnonymous] 
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "帳號或密碼錯誤" });
        }

        if (!user.IsActive) return StatusCode(403, new { message = "帳號已被停用" });

        var permissions = await _context.RolePermissions
            .Where(rp => rp.RoleId == user.RoleId)
            .Select(rp => new
            {
                path = rp.SystemRoute.Path,
                title = rp.SystemRoute.Title,
                icon = rp.SystemRoute.Icon,
                isPublic = rp.SystemRoute.IsPublic,
                routeName = rp.SystemRoute.RouteName,
                canView = rp.CanView,
                canCreate = rp.CanCreate,
                canUpdate = rp.CanUpdate,
                canDelete = rp.CanDelete
            })
            .ToListAsync();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Username),
            new Claim(ClaimTypes.Role, user.Role.Code),
            new Claim("RoleId", user.RoleId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // 🌟 核心修改 1：使用 RSA 私鑰與 RS256 演算法簽發 Token
        var privateKeyPem = Environment.GetEnvironmentVariable("JWT_PRIVATE_KEY")?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException("系統設定錯誤：缺少有效的 JWT_PRIVATE_KEY。");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var securityKey = new RsaSecurityKey(rsa);

        var token = new JwtSecurityToken(
            issuer: "IgAiSystem",
            audience: "IgAiSystemFrontend",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        );
        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true, // 防止 XSS 攻擊
            Secure = true,   // 確保只能在 HTTPS 下傳輸 (本機測試若沒 HTTPS 可暫時設 false，但強烈建議正式環境為 true)
            SameSite = SameSiteMode.Lax, // 允許跨站攜帶 Cookie (針對前後端分離)
            Expires = DateTime.UtcNow.AddHours(24)
        };
        Response.Cookies.Append("auth_token", tokenString, cookieOptions);

        // 回傳資料不再包含 Token
        return Ok(new
        {
            message = "登入成功",
            user = new { username = user.Username, role = user.Role.Code }
        });
    }

    // =========================================================
    // 2. 註冊：預設給予 RoleId = 2 (Reviewer)
    // =========================================================
    [HttpPost("register")]
    [AllowAnonymous] 
    public async Task<IActionResult> Register([FromBody] LoginRequest request)
    {
        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
        {
            return BadRequest(new { message = "此帳號已經被註冊了" });
        }

        var newUser = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            RoleId = 2,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(newUser);
        await _context.SaveChangesAsync();
        return Ok(new { message = "註冊成功" });
    }

    // =========================================================
    // 3. 取得訪客 Token
    // =========================================================
    [HttpGet("guest-token")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGuestToken()
    {
        var guestPermissions = await _context.RolePermissions
            .Where(rp => rp.RoleId == 3)
            .Select(rp => new
            {
                path = rp.SystemRoute.Path,
                title = rp.SystemRoute.Title,
                icon = rp.SystemRoute.Icon,
                isPublic = rp.SystemRoute.IsPublic,
                routeName = rp.SystemRoute.RouteName,
                canView = rp.CanView,
                canCreate = rp.CanCreate,
                canUpdate = rp.CanUpdate,
                canDelete = rp.CanDelete
            })
            .ToListAsync();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "GuestUser"),
            new Claim(ClaimTypes.Role, "Guest"),
            new Claim("RoleId", "3"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // 🌟 核心修改 2：訪客 Token 也要用同一把 RSA 私鑰簽發
        var privateKeyPem = Environment.GetEnvironmentVariable("JWT_PRIVATE_KEY")?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException("系統設定錯誤：缺少有效的 JWT_PRIVATE_KEY。");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var securityKey = new RsaSecurityKey(rsa);

        var token = new JwtSecurityToken(
            issuer: "IgAiSystem",
            audience: "IgAiSystemFrontend",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256) // 👉 改用 RsaSha256
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = DateTime.UtcNow.AddHours(24)
        };
        Response.Cookies.Append("auth_token", tokenString, cookieOptions);

        return Ok(new { message = "訪客登入成功", user = new { username = "訪客 (Guest)", role = "Guest" } });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var username = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var roleIdStr = User.FindFirst("RoleId")?.Value;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(roleIdStr)) 
            return Unauthorized();

        int roleId = int.Parse(roleIdStr);
        
        var permissions = await _context.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => new
            {
                path = rp.SystemRoute.Path,
                title = rp.SystemRoute.Title,
                icon = rp.SystemRoute.Icon,
                isPublic = rp.SystemRoute.IsPublic,
                routeName = rp.SystemRoute.RouteName,
                canView = rp.CanView,
                canCreate = rp.CanCreate,
                canUpdate = rp.CanUpdate,
                canDelete = rp.CanDelete
            })
            .ToListAsync();

        return Ok(new {
            username = username,
            role = User.FindFirst(ClaimTypes.Role)?.Value,
            permissions = permissions
        });
    }
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("auth_token");
        return Ok(new { message = "已登出" });
    }
    // =========================================================
    // 4. 取得系統路由清單 (給權限管理頁面用)
    // =========================================================
    [HttpGet("routes")]
    public async Task<IActionResult> GetSystemRoutes()
    {
        var routes = await _context.SystemRoutes.ToListAsync();
        return Ok(routes);
    }

    // =========================================================
    // 5. 取得特定角色的權限設定
    // =========================================================
    [HttpGet("roles/{roleId}/permissions")]
    public async Task<IActionResult> GetRolePermissions(int roleId)
    {
        var perms = await _context.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => new
            {
                rp.RouteId,
                rp.CanView,
                rp.CanCreate,
                rp.CanUpdate,
                rp.CanDelete
            })
            .ToListAsync();
        return Ok(perms);
    }

    // =========================================================
    // 6. 批次更新特定角色權限
    // =========================================================
    [HttpPut("roles/{roleId}/permissions")]
    public async Task<IActionResult> UpdateRolePermissions(int roleId, [FromBody] List<UpdateRolePermissionsDto> dtos)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();

        var roleExists = await _context.Roles.AnyAsync(r => r.Id == roleId);
        if (!roleExists) return NotFound(new { message = "找不到該角色" });

        var existingPerms = await _context.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync();
        _context.RolePermissions.RemoveRange(existingPerms);

        foreach (var dto in dtos)
        {
            _context.RolePermissions.Add(new RolePermission
            {
                RoleId = roleId,
                RouteId = dto.RouteId,
                CanView = dto.CanView,
                CanCreate = dto.CanCreate,
                CanUpdate = dto.CanUpdate,
                CanDelete = dto.CanDelete
            });
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = "角色權限更新成功" });
    }

    // =========================================================
    // 8. 使用者 CRUD (僅限 Admin)
    // =========================================================
    [HttpGet("users")]
    public async Task<IActionResult> GetAllUsers()
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();

        var users = await _context.Users
            .Include(u => u.Role)
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.RoleId,
                RoleName = u.Role.Name,
                u.IsActive,
                u.CreatedAt
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPut("users/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();

        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound();

        var currentUsername = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (targetUser.Username == currentUsername && (!request.IsActive || request.RoleId != 1))
        {
            return BadRequest(new { message = "您不能對自己進行降級或停權操作！" });
        }

        targetUser.RoleId = request.RoleId;
        targetUser.IsActive = request.IsActive;

        await _context.SaveChangesAsync();
        return Ok(new { message = "更新成功" });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin")
            return StatusCode(403, new { message = "權限不足，僅管理員可執行此操作" });

        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound(new { message = "找不到該名使用者" });

        var currentUsername = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                           ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (targetUser.Username == currentUsername)
        {
            return BadRequest(new { message = "您不能刪除正在使用的帳號！" });
        }

        _context.Users.Remove(targetUser);
        await _context.SaveChangesAsync();
        return Ok(new { message = "帳號已永久刪除" });
    }
    [HttpPut("users/{id}/reset-password")]
    public async Task<IActionResult> ResetUserPassword(int id, [FromBody] ResetPasswordRequest request)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();

        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound(new { message = "找不到該名使用者" });

        // 重新 Hash 新密碼
        targetUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        
        await _context.SaveChangesAsync();
        return Ok(new { message = "密碼重置成功" });
    }
}

public class UpdateUserRequest
{
    public int RoleId { get; set; }
    public bool IsActive { get; set; }
}

public class UpdateRolePermissionsDto
{
    public int RouteId { get; set; }
    public bool CanView { get; set; }
    public bool CanCreate { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
}
public class ResetPasswordRequest
{
    public required string NewPassword { get; set; }
}