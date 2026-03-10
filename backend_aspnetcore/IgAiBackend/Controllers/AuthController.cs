using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography;
using StackExchange.Redis;
using System.Text.Json; // 🌟 新增：用於 Redis 快取的 JSON 序列化

namespace IgAiBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IConnectionMultiplexer _redis;

    public AuthController(ApplicationDbContext context, IConfiguration configuration, IConnectionMultiplexer redis)
    {
        _context = context;
        _configuration = configuration;
        _redis = redis;
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    // =========================================================
    // 🌟 核心共用邏輯：取得權限 (包含 Redis 快取與 LINQ 壓平)
    // =========================================================
    private async Task<List<object>> GetFormattedPermissionsAsync(int roleId)
    {
        var cacheKey = $"ig_ai:perms:role:{roleId}";
        var db = _redis.GetDatabase();

        try
        {
            // 1. 嘗試從 Redis 讀取快取 (Zero-DB Query)
            var cachedPerms = await db.StringGetAsync(cacheKey);
            if (!cachedPerms.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize<List<object>>(cachedPerms!) ?? new List<object>();
            }
        }
        catch { /* 忽略 Redis 連線錯誤，降級使用 DB 查詢 */ }

        // 2. 快取未命中，執行資料庫查詢與 LINQ 分群
        // 2. 快取未命中，執行資料庫查詢與 LINQ 分群
        var permissionsQuery = await _context.RolePermissions
            .AsNoTracking()
            .Include(rp => rp.SystemRoute)
            .Include(rp => rp.Action)
            .Where(rp => rp.RoleId == roleId)
            .Where(p => p.SystemRoute != null && p.Action != null) // 防呆：過濾髒資料
            .GroupBy(p => new
            {
                p.SystemRoute.RouteName,
                p.SystemRoute.Title,
                p.SystemRoute.Path,
                p.SystemRoute.IsPublic,
                p.SystemRoute.Icon
            })
            .Select(g => new
            {
                routeName = g.Key.RouteName,
                title = g.Key.Title,
                path = g.Key.Path,
                icon = g.Key.Icon,
                isPublic = g.Key.IsPublic,
                // 🌟 核心轉換：將群組內所有 Action.Code 選出來，並使用 Distinct() 防止重複
                actions = g.Select(p => p.Action.Code).Distinct().ToList()
            })
            .ToListAsync(); // 🌟 修正點 1：將原本的 ToList() 改為 ToListAsync()，這樣才能被 await

        // 🌟 修正點 2：等資料從 DB 非同步拉回來後，再於記憶體中轉換為 List<object>
        var permissions = permissionsQuery.Cast<object>().ToList();

        try
        {
            // 3. 寫入 Redis 快取，設定過期時間 (例如 24 小時)
            await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(permissions), TimeSpan.FromHours(24));
        }
        catch { /* 忽略 Redis 寫入錯誤 */ }

        return permissions;
    }

    // =========================================================
    // 1. 登入
    // =========================================================
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _context.Users
            .Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Username == request.Username);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { message = "帳號或密碼錯誤" });

        if (!user.IsActive) return StatusCode(403, new { message = "帳號已被停用" });

        // 🌟 使用高效能的快取讀取方法
        var formattedPermissions = await GetFormattedPermissionsAsync(user.RoleId);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.Code),
            new Claim("RoleId", user.RoleId.ToString())
        };

        var privateKeyPem = Environment.GetEnvironmentVariable("JWT_PRIVATE_KEY")?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(privateKeyPem)) throw new InvalidOperationException("系統設定錯誤：缺少 JWT_PRIVATE_KEY");

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var securityKey = new RsaSecurityKey(rsa);

        var token = new JwtSecurityToken(
            issuer: "IgAiSystem", audience: "IgAiSystemFrontend", claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        );

        Response.Cookies.Append("auth_token", new JwtSecurityTokenHandler().WriteToken(token), new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = DateTime.UtcNow.AddHours(24)
        });

        return Ok(new
        {
            message = "登入成功",
            user = new { username = user.Username, role = user.Role.Code },
            permissions = formattedPermissions 
        });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] LoginRequest request)
    {
        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
            return BadRequest(new { message = "此帳號已經被註冊了" });

        var newUser = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            RoleId = 2, IsActive = true, CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(newUser);
        await _context.SaveChangesAsync();
        return Ok(new { message = "註冊成功" });
    }

    [HttpGet("guest-token")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGuestToken()
    {
        int guestRoleId = 3; // 假設 3 是訪客
        var formattedPermissions = await GetFormattedPermissionsAsync(guestRoleId);
        
        var claims = new[] {
            new Claim(ClaimTypes.NameIdentifier, "GuestUser"),
            new Claim(ClaimTypes.Role, "Guest")
        };

        var privateKeyPem = Environment.GetEnvironmentVariable("JWT_PRIVATE_KEY")?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(privateKeyPem)) throw new InvalidOperationException("系統設定錯誤");

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var token = new JwtSecurityToken(
            issuer: "IgAiSystem", audience: "IgAiSystemFrontend", claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)
        );

        Response.Cookies.Append("auth_token", new JwtSecurityTokenHandler().WriteToken(token), new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Expires = DateTime.UtcNow.AddHours(24)
        });

        return Ok(new
        {
            message = "訪客登入成功",
            user = new { username = "訪客 (Guest)", role = "Guest" },
            permissions = formattedPermissions
        });
    }

    // =========================================================
    // 4. 取得個人資訊 (GetMe) - 套用架構師建議
    // =========================================================
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userIdStr == "GuestUser" || string.IsNullOrEmpty(userIdStr))
            return await GetGuestResponse();

        if (!int.TryParse(userIdStr, out int userId)) return Unauthorized();

        var user = await _context.Users
            .Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Id == userId);

        if (user == null) return Unauthorized();

        // 🌟 使用高效能的快取讀取方法
        var formattedPermissions = await GetFormattedPermissionsAsync(user.RoleId);

        return Ok(new
        {
            username = user.Username,
            role = user.Role.Code,
            permissions = formattedPermissions
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("auth_token");
        return Ok(new { message = "已登出" });
    }

    [HttpGet("routes")]
    public async Task<IActionResult> GetSystemRoutes()
    {
        return Ok(await _context.SystemRoutes.ToListAsync());
    }

    [HttpGet("roles/{roleId}/permissions")]
    public async Task<IActionResult> GetRolePermissions(int roleId)
    {
        var rawPerms = await _context.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .Include(rp => rp.Action)
            .ToListAsync();

        var formattedPerms = rawPerms
            .GroupBy(rp => rp.RouteId)
            .Select(g => new
            {
                RouteId = g.Key,
                AllowedActionIds = g.Select(x => x.ActionId).ToList()
            })
            .ToList();

        return Ok(formattedPerms);
    }

    [HttpPut("roles/{roleId}/permissions")]
    public async Task<IActionResult> UpdateRolePermissions(int roleId, [FromBody] List<UpdateRolePermissionsDto> dtos)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var existingPerms = await _context.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync();
            _context.RolePermissions.RemoveRange(existingPerms);

            var newPerms = new List<RolePermission>();
            foreach (var dto in dtos)
            {
                if (dto.AllowedActionIds != null)
                {
                    foreach (var actionId in dto.AllowedActionIds)
                    {
                        newPerms.Add(new RolePermission { RoleId = roleId, RouteId = dto.RouteId, ActionId = actionId });
                    }
                }
            }
            
            if (newPerms.Any()) await _context.RolePermissions.AddRangeAsync(newPerms);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            // 🌟 權限變更後，主動清除該角色的 Redis 快取，強制下次讀取最新 DB 資料
            try { await _redis.GetDatabase().KeyDeleteAsync($"ig_ai:perms:role:{roleId}"); } catch { }
            var cacheKey = $"ig_ai:api_perms_hash:role:{roleId}";
            await _redis.GetDatabase().KeyDeleteAsync(cacheKey);
            return Ok(new { message = "權限更新成功" });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, new { message = "權限更新失敗", error = ex.Message });
        }
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetAllUsers()
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();
        var users = await _context.Users.Include(u => u.Role).Select(u => new { u.Id, u.Username, u.RoleId, RoleName = u.Role.Name, u.IsActive, u.CreatedAt }).ToListAsync();
        return Ok(users);
    }

    [HttpPut("users/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();
        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound();
        targetUser.RoleId = request.RoleId; targetUser.IsActive = request.IsActive;
        await _context.SaveChangesAsync(); return Ok(new { message = "更新成功" });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return StatusCode(403, new { message = "權限不足" });
        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound();
        _context.Users.Remove(targetUser); await _context.SaveChangesAsync(); return Ok(new { message = "已刪除" });
    }

    [HttpPut("users/{id}/reset-password")]
    public async Task<IActionResult> ResetUserPassword(int id, [FromBody] ResetPasswordRequest request)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "Admin") return Forbid();
        var targetUser = await _context.Users.FindAsync(id);
        if (targetUser == null) return NotFound();
        targetUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _context.SaveChangesAsync(); return Ok(new { message = "密碼重置成功" });
    }

    private async Task<IActionResult> GetGuestResponse()
    {
        int guestRoleId = 3;
        var guestPermissions = await GetFormattedPermissionsAsync(guestRoleId);

        return Ok(new
        {
            username = "GuestUser",
            role = "Guest",
            permissions = guestPermissions
        });
    }
}

public class UpdateUserRequest { public int RoleId { get; set; } public bool IsActive { get; set; } }
public class ResetPasswordRequest { public required string NewPassword { get; set; } }

public class UpdateRolePermissionsDto
{
    public int RouteId { get; set; }
    public List<int> AllowedActionIds { get; set; } = new List<int>();
}