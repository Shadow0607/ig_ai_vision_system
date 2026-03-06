using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using IgAiBackend.Data;
using StackExchange.Redis; // 🌟 引入 Redis
using System.Text.Json;

namespace IgAiBackend.Helpers;

public static class PermissionHelper
{
    // 🌟 在參數中新增 IConnectionMultiplexer
    public static async Task<bool> HasPermissionAsync(ApplicationDbContext context, ClaimsPrincipal user, string routeName, string action, IConnectionMultiplexer redis)
    {
        var claim = user.FindFirst("RoleId")?.Value;
        if (!int.TryParse(claim, out int roleId)) roleId = 3; // 預設 Guest

        var db = redis.GetDatabase();
        // 設計 Redis Key，例如: ig_ai:perms:role:1
        string cacheKey = $"ig_ai:perms:role:{roleId}"; 
        
        List<string>? actionCodes = null;

        // 1. 嘗試從 Redis Hash 中讀取該路由的權限 (Zero-DB Query)
        var cachedData = await db.HashGetAsync(cacheKey, routeName);

        if (cachedData.HasValue)
        {
            // Cache Hit: 反序列化 JSON 陣列 (例如: ["VIEW", "UPDATE"])
            actionCodes = JsonSerializer.Deserialize<List<string>>(cachedData.ToString());
        }
        else
        {
            // 2. Cache Miss: 乖乖去查資料庫 (多表 JOIN)
            actionCodes = await context.RolePermissions
                .Include(rp => rp.SystemRoute)
                .Include(rp => rp.Action)
                .Where(rp => rp.RoleId == roleId && rp.SystemRoute.RouteName == routeName)
                .Select(rp => rp.Action.Code)
                .ToListAsync();

            // 3. 將查詢結果寫回 Redis Hash 供下次使用
            if (actionCodes.Any())
            {
                await db.HashSetAsync(cacheKey, routeName, JsonSerializer.Serialize(actionCodes));
                // 設定 24 小時過期，避免死資料佔用記憶體
                await db.KeyExpireAsync(cacheKey, TimeSpan.FromHours(24)); 
            }
        }

        // 4. 進行權限比對
        if (actionCodes == null || !actionCodes.Contains("VIEW")) return false;
        
        return actionCodes.Contains(action.ToUpper());
    }
}