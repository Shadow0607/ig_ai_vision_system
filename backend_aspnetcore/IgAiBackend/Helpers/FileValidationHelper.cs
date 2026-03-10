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
        foreach (var file in files)
        {
            if (file.Length < 8) continue; // 檔案太小，無法判定特徵

            using (var stream = file.OpenReadStream())
            using (var reader = new BinaryReader(stream))
            {
                // 讀取前 8 個 bytes
                byte[] header = reader.ReadBytes(8);

                // 🌟 JPEG 特徵碼: FF D8 FF
                bool isJpg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
                // 🌟 PNG 特徵碼: 89 50 4E 47
                bool isPng = header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
                // 🌟 MP4 特徵碼: 包含 "ftyp" (66 74 79 70) 於 offset 4
                bool isMp4 = header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70;

                if (!isJpg && !isPng && !isMp4)
                {
                    errorMessage = $"檔案「{file.FileName}」內容特徵與副檔名不符，疑似惡意檔案。";
                    return false;
                }
            }
        }
        return true;
    }
}