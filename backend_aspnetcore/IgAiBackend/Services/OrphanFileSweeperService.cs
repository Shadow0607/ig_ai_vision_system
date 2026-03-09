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

namespace IgAiBackend.Services
{
    public class OrphanFileSweeperService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrphanFileSweeperService> _logger;
        private readonly IMinioClient _minioClient;
        private readonly string _bucketName = "ig-ai-assets";

        // ⚠️ 注意：BackgroundService 是 Singleton，不能直接注入 Scoped 的 DbContext
        // 必須注入 IServiceProvider 來動態建立 Scope
        public OrphanFileSweeperService(
            IServiceProvider serviceProvider,
            ILogger<OrphanFileSweeperService> logger,
            IMinioClient minioClient)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _minioClient = minioClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🧹 [自動清理排程] OrphanFileSweeperService 已啟動，守護 NAS 空間中...");

            // 系統啟動後先等待 5 分鐘再執行第一次掃描，避免拖慢系統啟動速度
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOrphanFilesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ [自動清理排程] 發生未預期崩潰，將於下個週期重試。");
                }

                // ⏳ 設定巡邏間隔：每 12 小時執行一次掃描
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
        }

        private async Task SweepOrphanFilesAsync(CancellationToken stoppingToken)
        {
            // 動態建立 Scope 取得 DbContext
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 定義：72 小時前的時間死線
            var deadline = DateTime.UtcNow.AddHours(-72);

            // 尋找目標：狀態為 PENDING(25) 且 來源為 STORY(31) 且 建立時間超過 72 小時 [1-3]
            var orphanAssets = await context.MediaAssets
                .Where(m => m.DownloadStatusId == 25 
                         && m.SourceTypeId == 31 
                         && m.CreatedAt <= deadline)
                .ToListAsync(stoppingToken);

            if (!orphanAssets.Any())
            {
                _logger.LogInformation("🔍 [自動清理排程] 掃描完成，沒有需要清理的孤兒檔案。");
                return;
            }

            _logger.LogWarning($"⚠️ [自動清理排程] 發現 {orphanAssets.Count} 筆逾期未審核的隔離限動，準備執行物理抹除！");

            int successCount = 0;

            foreach (var asset in orphanAssets)
            {
                // 🌟 導入分散式交易，確保「刪除檔案」與「改 DB 狀態」是原子操作
                using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);
                try
                {
                    // 1. 執行物理抹除 (從 MinIO NAS 刪除)
                    // 🛡️ 防呆：絕對確保只有 quarantine/ 目錄下的檔案才能被此排程刪除
                    if (!string.IsNullOrEmpty(asset.FilePath) && asset.FilePath.Contains("quarantine/"))
                    {
                        var objectName = asset.FilePath.Split(new[] { _bucketName + "/" }, StringSplitOptions.None).LastOrDefault();
                        if (!string.IsNullOrEmpty(objectName))
                        {
                            await _minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                                .WithBucket(_bucketName)
                                .WithObject(objectName), stoppingToken);
                        }
                    }

                    // 2. 更新資料庫狀態為 SKIPPED (28) [3]
                    asset.DownloadStatusId = 28; 
                    await context.SaveChangesAsync(stoppingToken);
                    await transaction.CommitAsync(stoppingToken);

                    successCount++;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(stoppingToken);
                    _logger.LogError(ex, $"🗑️ [自動清理排程] 檔案清理失敗 (MediaId: {asset.Id})，已 Rollback");
                }
            }

            _logger.LogInformation($"✅ [自動清理排程] 任務完成，成功釋放 {successCount} 筆隔離區檔案與空間。");
        }
    }
}