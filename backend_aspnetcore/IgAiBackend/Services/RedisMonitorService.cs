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

        // 🌟 修正點：使用 RedisChannel.Literal 明確指定頻道名稱
        await subscriber.SubscribeAsync(RedisChannel.Literal("ai_task_completed"), async (channel, message) =>
        {
            Console.WriteLine("⚡ [Redis] 收到 AI 處理完成訊號，正在計算最新統計...");

            // 透過 Scope 取得 DB Context
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 計算最新統計
            var successStatuses = new[] { "OUTPUT", "COMPLETED", "MATCH_VSTACK" };
            var skipStatuses = new[] { "SKIP", "REJECTED", "NOFACE", "GARBAGE", "AMBIGUOUS", "UNCERTAIN" };

            var logs = await db.AiAnalysisLogs.Select(l => l.RecognitionStatus).ToListAsync();
            var stats = new
            {
                successCount = logs.Count(s => successStatuses.Contains(s)),
                skipCount = logs.Count(s => skipStatuses.Contains(s))
            };

            // 透過 SignalR 廣播給所有開著大盤的前端
            await _hubContext.Clients.All.SendAsync("UpdateStatistics", JsonSerializer.Serialize(stats));
        });
        await subscriber.SubscribeAsync(RedisChannel.Literal("system_alert_new"), async (channel, message) =>
        {
            Console.WriteLine("🚨 [Redis] 收到新系統告警，推播給前端...");
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 去資料庫抓最新的一筆告警
            var latestAlert = await db.SystemAlerts
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();

            if (latestAlert != null)
            {
                var alertDto = new
                {
                    type = latestAlert.AlertType,
                    message = $"[{latestAlert.SourceComponent}] {latestAlert.Message}",
                    timestamp = latestAlert.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                };
                // 觸發 Vue 前端的 NewAlert 事件
                await _hubContext.Clients.All.SendAsync("NewAlert", JsonSerializer.Serialize(alertDto));
            }
        });
    }
}