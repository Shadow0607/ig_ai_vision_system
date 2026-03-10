using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using IgAiBackend.Data;
using IgAiBackend.Models;
using Microsoft.Extensions.Options;

namespace IgAiBackend.Services 
{
    public class OrphanFileSweeperService : BackgroundService 
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrphanFileSweeperService> _logger;
        private readonly IMinioClient _minioClient;
        private readonly string _bucketName;

        public OrphanFileSweeperService(
            IServiceProvider serviceProvider, 
            ILogger<OrphanFileSweeperService> logger, 
            IMinioClient minioClient,
            IOptions<MinioSettings> minioSettings) 
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _minioClient = minioClient;
            _bucketName = minioSettings.Value.BucketName;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) 
        {
            _logger.LogInformation("🛡️ NAS 死信與孤兒檔案清理排程已啟動 (動態狀態尋址模式)。");

            while (!stoppingToken.IsCancellationRequested) 
            {
                try 
                {
                    // 1. 建立獨立的生命週期 Scope，避免 BackgroundService 導致 Memory Leak
                    using (var scope = _serviceProvider.CreateScope()) 
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        
                        // ========================================================
                        // 🌟 核心優化：動態取得 Status ID，徹底消滅 Magic Numbers
                        // ========================================================
                        var pendingStatus = await dbContext.SysStatuses
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s => s.Category == "DOWNLOAD_STATUS" && s.Code == "PENDING", stoppingToken);

                        var errorStatus = await dbContext.SysStatuses
                            .AsNoTracking()
                            .FirstOrDefaultAsync(s => s.Category == "DOWNLOAD_STATUS" && s.Code == "ERROR", stoppingToken);

                        if (pendingStatus == null || errorStatus == null)
                        {
                            _logger.LogWarning("⚠️ 系統狀態庫 (sys_statuses) 尚未初始化 PENDING 或 ERROR 狀態，本次清理略過。");
                            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                            continue;
                        }

                        // 2. 尋找超過 24 小時仍卡在 PENDING 的無效任務
                        var deadThreshold = DateTime.UtcNow.AddDays(-1);
                        bool hasMoreTasks = true;

                        // 🌟 加入迴圈：只要還有死信任務，就繼續每批 100 筆往下清
                        while (hasMoreTasks && !stoppingToken.IsCancellationRequested)
                        {
                            var deadTasks = await dbContext.MediaAssets
                                .Where(m => m.DownloadStatusId == pendingStatus.Id && m.CreatedAt < deadThreshold)
                                .Take(100) // 每次處理 100 筆，防止記憶體崩潰
                                .ToListAsync(stoppingToken);

                            if (!deadTasks.Any())
                            {
                                hasMoreTasks = false; // 沒資料了，結束迴圈準備去睡 12 小時
                                continue;
                            }

                            foreach (var task in deadTasks) 
                            {
                                _logger.LogWarning($"🧹 偵測到死信任務 (MediaID: {task.Id})，準備清理...");
                                
                                // 3. NAS 實體防呆刪除
                                if (!string.IsNullOrWhiteSpace(task.FilePath)) 
                                {
                                    try
                                    {
                                        var removeArgs = new RemoveObjectArgs()
                                            .WithBucket(_bucketName)
                                            .WithObject(task.FilePath);
                                        await _minioClient.RemoveObjectAsync(removeArgs, stoppingToken);
                                        _logger.LogInformation($"🗑️ 已從 NAS 刪除殘留檔案: {task.FilePath}");
                                    }
                                    catch (Exception minioEx)
                                    {
                                        _logger.LogError($"NAS 刪除失敗 ({task.FilePath}): {minioEx.Message}");
                                    }
                                }

                                // 4. 動態寫入 Error ID，封印任務
                                task.DownloadStatusId = errorStatus.Id; 
                            }

                            // 存檔並紀錄
                            await dbContext.SaveChangesAsync(stoppingToken);
                            _logger.LogInformation($"✅ 本批次已成功清理 {deadTasks.Count} 筆死信任務。");
                        }
                    }
                }
                catch (Exception ex) 
                {
                    _logger.LogError($"清理排程發生嚴重異常: {ex.Message}");
                }

                // 休眠 12 小時 (等待下一次掃描)
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
        }
    }
}