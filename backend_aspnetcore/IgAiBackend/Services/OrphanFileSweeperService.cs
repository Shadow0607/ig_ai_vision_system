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
            _logger.LogInformation("🛡️ NAS 檔案清理機啟動 (包含 GARBAGE 物理清理模式)。");

            while (!stoppingToken.IsCancellationRequested) 
            {
                try 
                {
                    using (var scope = _serviceProvider.CreateScope()) 
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        
                        // 🌟 1. 取得需要物理清理的狀態碼 (GARBAGE 與 REJECTED)
                        var garbageStatuses = await dbContext.SysStatuses
                            .Where(s => s.Category == "AI_RECOGNITION" && (s.Code == "GARBAGE" || s.Code == "REJECTED"))
                            .Select(s => s.Id)
                            .ToListAsync(stoppingToken);

                        // 🌟 2. 取得超時 PENDING 狀態
                        var pendingStatusId = (await dbContext.SysStatuses
                            .FirstOrDefaultAsync(s => s.Category == "DOWNLOAD_STATUS" && s.Code == "PENDING", stoppingToken))?.Id;

                        // 🌟 3. 執行物理檔案清理迴圈
                        var cleanupTargets = await dbContext.MediaAssets
                            .Where(m => (garbageStatuses.Contains(m.DownloadStatusId)) || 
                                        (m.DownloadStatusId == pendingStatusId && m.CreatedAt < DateTime.UtcNow.AddDays(-1)))
                            .Take(200) // 批次處理
                            .ToListAsync(stoppingToken);

                        foreach (var target in cleanupTargets) 
                        {
                            if (!string.IsNullOrWhiteSpace(target.FilePath)) 
                            {
                                try {
                                    await _minioClient.RemoveObjectAsync(new RemoveObjectArgs()
                                        .WithBucket(_bucketName).WithObject(target.FilePath), stoppingToken);
                                    _logger.LogInformation($"🗑️ 物理清理成功: {target.FilePath}");
                                    
                                    // 🌟 4. 清理完畢後，將路徑清空防止重複處理，並視需要更新狀態
                                    target.FilePath = string.Empty; 
                                }
                                catch (Exception ex) {
                                    _logger.LogError($"物理清理失敗 ({target.FilePath}): {ex.Message}");
                                }
                            }
                        }
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (Exception ex) {
                    _logger.LogError($"清理排程異常: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromHours(12), stoppingToken); // 定期執行
            }
        }
    }
}