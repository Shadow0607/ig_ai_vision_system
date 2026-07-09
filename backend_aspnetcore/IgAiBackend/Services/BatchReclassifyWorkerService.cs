using System.Text.Json;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace IgAiBackend.Services
{
    public class BatchReclassifyWorkerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BatchReclassifyWorkerService> _logger;
        private readonly IConnectionMultiplexer _redis;
        // 🗑️ 移除了 IMinioClient 與 MinioSettings，讓 Worker 職責更單純

        public BatchReclassifyWorkerService(
            IServiceProvider serviceProvider,
            ILogger<BatchReclassifyWorkerService> logger,
            IConnectionMultiplexer redis)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _redis = redis;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 批次分類背景處理機 (Batch Reclassify Worker) 已啟動...");
            var db = _redis.GetDatabase();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 從 Redis 佇列取出任務 (非阻塞讀取，若空則休眠 2 秒)
                    var redisResult = await db.ListRightPopAsync("ig_batch_reclassify_queue");
                    if (redisResult.IsNullOrEmpty)
                    {
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }

                    var payload = JsonSerializer.Deserialize<BatchTaskPayload>(redisResult.ToString());
                    if (payload == null || !payload.LogIds.Any()) continue;

                    _logger.LogInformation($"📥 收到批次任務，共 {payload.LogIds.Count} 筆檔案準備搬移...");

                    // 🌟 建立獨立 Scope 解析 Scoped 服務 (BackgroundService 必備寫法)
                    using var scope = _serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    
                    // 🌟 透過 Scope 解析我們剛寫好的 S3 共用服務
                    var s3Service = scope.ServiceProvider.GetRequiredService<IS3MediaStorageService>();

                    var logs = await dbContext.AiAnalysisLogs
                        .Include(l => l.MediaAsset)
                        .Where(l => payload.LogIds.Contains(l.Id))
                        .ToListAsync(stoppingToken);

                    bool isOutput = payload.TargetStatusCode == "OUTPUT";
                    string targetFolder = isOutput ? "pos" : (payload.TargetStatusCode == "REJECTED" ? "GARBAGE" : payload.TargetStatusCode);
                    var affectedProfiles = new HashSet<string>();

                    // 🌟 改用 foreach 循序處理，避免 EF Core 的並發更新例外 (Thread-Safety)
                    foreach (var log in logs)
                    {
                        try
                        {
                            string systemName = log.MediaAsset.SystemName;
                            string sourceKey = log.MediaAsset.FilePath;

                            // 🛡️ 呼叫共用 S3 服務，若失敗會拋出 Exception，直接跳到 catch
                            string newPath = await s3Service.MoveMediaWithThumbnailAsync(
                                sourceKey, 
                                systemName, 
                                targetFolder, 
                                syncToOutput: isOutput
                            );

                            // 🛡️ 只有 S3 搬移成功，才會執行到這裡更新資料庫
                            log.MediaAsset.FilePath = newPath;
                            log.StatusId = payload.NewStatusId;
                            log.ReviewedBy = payload.ReviewedBy;
                            log.ConfidenceScore = isOutput ? 1.0f : 0.0f;

                            affectedProfiles.Add(systemName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[Worker Move Error] LogID: {log.Id}, S3 搬移失敗已跳過更新: {ex.Message}");
                        }
                    }

                    // 一次性將所有成功搬移的實體狀態儲存進資料庫
                    await dbContext.SaveChangesAsync(stoppingToken);

                    // 觸發 AI 重建
                    foreach (var profile in affectedProfiles)
                    {
                        var aiPayload = new { type = "BUILD_FEATURE_BANK", profile = profile, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                        await db.ListLeftPushAsync("ig_processing_queue_high", JsonSerializer.Serialize(aiPayload));
                    }

                    _logger.LogInformation($"✅ 批次任務處理完成！已通知 AI 重建 {affectedProfiles.Count} 個人物特徵庫。");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ 背景工作異常: {ex.Message}");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        // 定義反序列化用的 Payload 模型
        private class BatchTaskPayload
        {
            public List<long> LogIds { get; set; } = new();
            public int NewStatusId { get; set; }
            public string TargetStatusCode { get; set; } = "";
            public string ReviewedBy { get; set; } = "";
        }
    }
}