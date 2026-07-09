using Minio;
using Minio.DataModel.Args;
using Microsoft.Extensions.Options;
using IgAiBackend.Models; // 替換為你的 MinioSettings 所在命名空間

namespace IgAiBackend.Services;

public interface IS3MediaStorageService
{
    /// <summary>
    /// 統一處理 S3 檔案搬移、縮圖聯動、與 OUTPUT 同步。
    /// 成功則回傳新的 S3 Key 路徑；失敗會直接拋出 Exception 阻斷流程。
    /// </summary>
    Task<string> MoveMediaWithThumbnailAsync(string sourceKey, string systemName, string targetFolder, bool syncToOutput);
}

public class S3MediaStorageService : IS3MediaStorageService
{
    private readonly IMinioClient _minioClient;
    private readonly string _bucketName;
    private readonly ILogger<S3MediaStorageService> _logger;

    public S3MediaStorageService(IMinioClient minioClient, IOptions<MinioSettings> minioSettings, ILogger<S3MediaStorageService> logger)
    {
        _minioClient = minioClient;
        _bucketName = minioSettings.Value.BucketName;
        _logger = logger;
    }

    public async Task<string> MoveMediaWithThumbnailAsync(string sourceKey, string systemName, string targetFolder, bool syncToOutput)
    {
        string fileName = sourceKey.Split('/').Last();
        string targetKey = $"{systemName}/{targetFolder}/{fileName}";

        // 如果路徑根本沒變，直接回傳成功
        if (sourceKey == targetKey) return targetKey;

        // 🛡️ 1. 搬移主檔 (若此處失敗，會直接拋出 Exception，外層 Controller 就會中斷，保證不更新資料庫)
        await S3CopyAndRemoveAsync(sourceKey, targetKey);

        // 🛡️ 2. 視需求同步至 OUTPUT
        if (syncToOutput)
        {
            await S3CopyOnlyAsync(targetKey, $"{systemName}/OUTPUT/{fileName}");
        }

        // 🛡️ 3. 影片縮圖聯動處理
        if (sourceKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            string thumbSrc = sourceKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);
            string thumbDest = targetKey.Replace(".mp4", ".jpg", StringComparison.OrdinalIgnoreCase);

            try
            {
                await S3CopyAndRemoveAsync(thumbSrc, thumbDest);
                if (syncToOutput)
                {
                    await S3CopyOnlyAsync(thumbDest, $"{systemName}/OUTPUT/{Path.GetFileName(thumbDest)}");
                }
            }
            catch (Exception ex)
            {
                // 💡 縮圖遺失通常不算是致命錯誤 (影片主檔已搬運成功)，所以這裡只印 Log，不阻斷流程。
                _logger.LogWarning($"⚠️ 影片縮圖搬移失敗 [{thumbSrc}]: {ex.Message}");
            }
        }

        return targetKey; // 全部成功，回傳新的目標路徑
    }

    // ==========================================
    // 底層 MinIO 操作 (絕對不寫 try-catch，讓錯誤往外丟)
    // ==========================================
    private async Task S3CopyAndRemoveAsync(string src, string dest)
    {
        var cpArgs = new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src));
        await _minioClient.CopyObjectAsync(cpArgs);
        await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(src));
    }

    private async Task S3CopyOnlyAsync(string src, string dest)
    {
        var cpArgs = new CopyObjectArgs().WithBucket(_bucketName).WithObject(dest)
            .WithCopyObjectSource(new CopySourceObjectArgs().WithBucket(_bucketName).WithObject(src));
        await _minioClient.CopyObjectAsync(cpArgs);
    }
}