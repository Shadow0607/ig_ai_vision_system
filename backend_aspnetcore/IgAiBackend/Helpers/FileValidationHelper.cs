using Microsoft.AspNetCore.Http;

namespace IgAiBackend.Helpers;

public static class FileValidationHelper
{
    // 🌟 統一定義允許的規格
    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".mp4" };
    private static readonly string[] AllowedMimeTypes = { "image/jpeg", "image/png", "video/mp4" };
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// 統一驗證檔案格式與大小
    /// </summary>
    /// <param name="files">檔案清單</param>
    /// <param name="errorMessage">回傳的錯誤訊息</param>
    /// <returns>是否通過驗證</returns>
    public static bool IsValid(List<IFormFile> files, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (files == null || !files.Any())
        {
            errorMessage = "未選擇任何檔案。";
            return false;
        }

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            // 1. 檢查檔案大小
            if (file.Length > MaxFileSize)
            {
                errorMessage = $"檔案「{file.FileName}」超過 10MB 限制。";
                return false;
            }

            // 2. 檢查副檔名
            var ext = Path.GetExtension(file.FileName).ToLower();
            if (!AllowedExtensions.Contains(ext))
            {
                errorMessage = $"檔案「{file.FileName}」格式不符。僅支援 JPG, PNG, MP4。";
                return false;
            }

            // 3. 檢查 MIME Type (防止偽裝副檔名的惡意檔案)
            if (!AllowedMimeTypes.Contains(file.ContentType))
            {
                errorMessage = $"檔案「{file.FileName}」的內容類型無效。";
                return false;
            }
        }

        return true;
    }
}