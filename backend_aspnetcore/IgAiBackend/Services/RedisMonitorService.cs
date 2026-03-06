using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Text.Json;
using IgAiBackend.Hubs;
using IgAiBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace IgAiBackend.Services;

// 背景服務：負責監聽 Redis 並觸發 SignalR 廣播
public class RedisMonitorService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<MonitorHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;

    public RedisMonitorService(IConnectionMultiplexer redis, IHubContext<MonitorHub> hubContext, IServiceProvider serviceProvider)
    {
        _redis = redis;
        _hubContext = hubContext;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // 🌟 1. 監聽 AI 處理完成訊號
        await subscriber.SubscribeAsync(RedisChannel.Literal("ai_task_completed"), async (channel, message) =>
        {
            Console.WriteLine("⚡ [Redis] 收到 AI 處理完成訊號，正在計算最新統計...");

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var successStatuses = new[] { "OUTPUT", "COMPLETED", "MATCH_VSTACK" };
            var skipStatuses = new[] { "SKIP", "REJECTED", "NOFACE", "GARBAGE", "AMBIGUOUS", "UNCERTAIN" };

            // 🌟 修正點：加上 Include(l => l.Status) 並選取 Status!.Code
            var logs = await db.AiAnalysisLogs
                .Include(l => l.Status)
                .Select(l => l.Status!.Code)
                .ToListAsync();

            var stats = new
            {
                successCount = logs.Count(s => successStatuses.Contains(s)),
                skipCount = logs.Count(s => skipStatuses.Contains(s))
            };

            await _hubContext.Clients.All.SendAsync("UpdateStatistics", JsonSerializer.Serialize(stats));
        });

        // 🌟 2. 監聽系統告警訊號
        await subscriber.SubscribeAsync(RedisChannel.Literal("system_alert_new"), async (channel, message) =>
        {
            Console.WriteLine("🚨 [Redis] 收到新系統告警，推播給前端...");
            
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 🌟 修正點：加上 Include(a => a.AlertType) 載入字典表
            var latestAlert = await db.SystemAlerts
                .Include(a => a.AlertType)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();

            if (latestAlert != null)
            {
                var alertDto = new
                {
                    // 🌟 修正點：讀取 AlertType!.Code
                    type = latestAlert.AlertType!.Code,
                    message = $"[{latestAlert.SourceComponent}] {latestAlert.Message}",
                    timestamp = latestAlert.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                };
                
                await _hubContext.Clients.All.SendAsync("NewAlert", JsonSerializer.Serialize(alertDto));
            }
        });
    }
}