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

        // 🌟 核心修正：換一個全新的 Key 名稱，避免跟前端的 JSON 選單打架！
        string cacheKey = $"ig_ai:api_perms_hash:role:{roleId}";
        List<string>? actionCodes = null;

        try
        {
            // 1. 嘗試從 Redis Hash 中讀取該路由的權限
            var cachedData = await db.HashGetAsync(cacheKey, routeName);

            if (cachedData.HasValue)
            {
                // Cache Hit: 反序列化純 JSON 陣列 (例如: ["VIEW", "UPDATE"])
                actionCodes = JsonSerializer.Deserialize<List<string>>(cachedData.ToString());
            }
        }
        catch (Exception ex)
        {
            // 萬一 Redis 裡面有髒資料導致反序列化失敗，把這個壞掉的 key 刪除，強制走 DB
            Console.WriteLine($"[Redis Warning] 權限快取讀取失敗，將重新查詢 DB: {ex.Message}");
            await db.KeyDeleteAsync(cacheKey);
        }

        if (actionCodes == null)
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
                try
                {
                    await db.HashSetAsync(cacheKey, routeName, JsonSerializer.Serialize(actionCodes));
                    await db.KeyExpireAsync(cacheKey, TimeSpan.FromHours(24));
                }
                catch { /* 忽略寫入快取失敗，不影響主流程 */ }
            }
        }

        // 4. 進行權限比對
        if (actionCodes == null || !actionCodes.Contains("VIEW")) return false;

        return actionCodes.Contains(action.ToUpper());
    }
}