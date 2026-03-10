using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using IgAiBackend.Hubs;
using IgAiBackend.Data;
using IgAiBackend.Models;

namespace IgAiBackend.Services 
{
    public class RedisMonitorService : BackgroundService 
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IHubContext<MonitorHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RedisMonitorService> _logger;

        // 🌟 注入所需服務
        public RedisMonitorService(
            IConnectionMultiplexer redis, 
            IHubContext<MonitorHub> hubContext, 
            IServiceProvider serviceProvider,
            ILogger<RedisMonitorService> logger) 
        {
            _redis = redis;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) 
        {
            _logger.LogInformation("📡 Redis 與系統水位監控服務已啟動。");

            // ==========================================
            // 🌟 1. 設定 Redis 訂閱 (Pub/Sub 事件驅動)
            // ==========================================
            await SetupRedisSubscriptionsAsync(stoppingToken);

            // ==========================================
            // 🌟 2. 啟動硬碟容量監控 (背景每 5 分鐘執行，防護 NAS)
            // ==========================================
            _ = Task.Run(() => MonitorSystemCapacityAsync(stoppingToken), stoppingToken);

            // ==========================================
            // 🌟 3. 主迴圈：大盤心跳與佇列深度監控 (每 10 秒執行)
            // ==========================================
            while (!stoppingToken.IsCancellationRequested) 
            {
                try 
                {
                    var db = _redis.GetDatabase();
                    
                    // 監控 Redis 佇列深度 (爬蟲與 AI 的緩衝區)
                    long queueLength = await db.ListLengthAsync("ig_processing_queue");
                    // 💡 建議一併監控高優先級佇列
                    long highQueueLength = await db.ListLengthAsync("ig_processing_queue_high");
                    long totalQueue = queueLength + highQueueLength;

                    // 即時廣播給前端 Vue3 大盤
                    await _hubContext.Clients.All.SendAsync("ReceiveSystemMetrics", new {
                        queueLength = totalQueue,
                        timestamp = DateTime.UtcNow
                    }, stoppingToken);

                    // 異常防護：若任務堆積過多，自動寫入告警
                    if (totalQueue > 1000)
                    {
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            
                            // 檢查最後一次發送堆積告警的時間，避免每 10 秒瘋狂寫入 DB
                            var lastAlert = await dbContext.SystemAlerts
                                .Where(a => a.Message.Contains("處理佇列發生嚴重堆積"))
                                .OrderByDescending(a => a.CreatedAt)
                                .FirstOrDefaultAsync(stoppingToken);

                            if (lastAlert == null || (DateTime.UtcNow - lastAlert.CreatedAt).TotalMinutes > 30)
                            {
                                // 1. 動態去 SysStatuses 查出「系統異常」的 ID (確保 Category 也是對的)
                                var alertStatus = await dbContext.SysStatuses
                                    .AsNoTracking()
                                    .FirstOrDefaultAsync(s => s.Category == "ALERT_TYPE" && s.Code == "SYSTEM_ERROR", stoppingToken);

                                if (alertStatus != null)
                                {
                                    var alert = new SystemAlert
                                    {
                                        AlertTypeId = alertStatus.Id,           // 🌟 寫入對應的 26
                                        SourceComponent = "RedisMonitor",       
                                        Message = $"系統警告：AI 處理佇列發生嚴重堆積，目前等待任務數：{totalQueue}。",
                                        CreatedAt = DateTime.UtcNow,
                                        IsResolved = false                      
                                    };
                                    dbContext.SystemAlerts.Add(alert);
                                    await dbContext.SaveChangesAsync(stoppingToken);
                                    _logger.LogWarning($"⚠️ 佇列堆積告警已觸發：目前任務數 {totalQueue}");
                                }
                                else
                                {
                                    _logger.LogWarning($"⚠️ 佇列堆積，但無法寫入告警，因為 SysStatuses 中找不到 Category='ALERT_TYPE' 且 Code='SYSTEM_ERROR' 的紀錄。");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) 
                {
                    _logger.LogError($"監控服務發生異常: {ex.Message}");
                }

                // 每 10 秒更新一次大盤心跳
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        // ==========================================
        // 輔助方法：設定 Redis 訂閱 (事件驅動)
        // ==========================================
        private async Task SetupRedisSubscriptionsAsync(CancellationToken stoppingToken)
        {
            var subscriber = _redis.GetSubscriber();

            // 監聽 AI 處理完成訊號，觸發圓餅圖統計更新
            await subscriber.SubscribeAsync(RedisChannel.Literal("ai_task_completed"), async (channel, message) =>
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var successStatuses = new[] { "OUTPUT", "COMPLETED", "MATCH_VSTACK" };
                var skipStatuses = new[] { "SKIP", "REJECTED", "NOFACE", "GARBAGE", "AMBIGUOUS", "UNCERTAIN" };

                var logs = await dbContext.AiAnalysisLogs
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

            // 監聽系統其他服務丟出的告警訊號
            await subscriber.SubscribeAsync(RedisChannel.Literal("system_alert_new"), async (channel, message) =>
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var latestAlert = await dbContext.SystemAlerts
                    .Include(a => a.AlertType)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync();

                if (latestAlert != null)
                {
                    var alertDto = new
                    {
                        type = latestAlert.AlertType?.Code ?? "UNKNOWN",
                        message = $"[{latestAlert.SourceComponent ?? "System"}] {latestAlert.Message}",
                        timestamp = latestAlert.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                    
                    await _hubContext.Clients.All.SendAsync("NewAlert", JsonSerializer.Serialize(alertDto));
                }
            });
        }

        // ==========================================
        // 輔助方法：硬碟容量監控防護網 (獨立執行緒)
        // ==========================================
        private async Task MonitorSystemCapacityAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🛡️ [系統監控] 硬碟容量防護機制已啟動...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 根據實際 NAS 掛載路徑調整，若無特別掛載則監控當前應用程式所在磁碟
                    var driveInfo = new DriveInfo(Directory.GetCurrentDirectory());
                    
                    double totalGB = driveInfo.TotalSize / (1024.0 * 1024 * 1024);
                    double freeGB = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                    double freePercentage = (freeGB / totalGB) * 100;

                    // 常態廣播給前端大盤
                    var capacityStats = new
                    {
                        TotalGB = Math.Round(totalGB, 2),
                        FreeGB = Math.Round(freeGB, 2),
                        FreePercentage = Math.Round(freePercentage, 2)
                    };
                    
                    await _hubContext.Clients.All.SendAsync("UpdateCapacity", JsonSerializer.Serialize(capacityStats), stoppingToken);

                    // 🚨 危險告警：硬碟空間低於 10% 觸發警告
                    if (freePercentage <= 10.0)
                    {
                        var criticalAlertDto = new
                        {
                            type = "CRITICAL_STORAGE",
                            message = $"[主機監控] ⚠️ 危險！系統硬碟空間僅剩 {capacityStats.FreePercentage}% ({capacityStats.FreeGB} GB)，為避免資料庫與 NAS 鎖死，請盡速清理孤兒檔案！",
                            timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                        };
                        
                        await _hubContext.Clients.All.SendAsync("NewAlert", JsonSerializer.Serialize(criticalAlertDto), stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [系統監控] 讀取硬碟容量失敗: {ex.Message}");
                }

                // ⏳ 每 5 分鐘巡檢一次硬碟
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}