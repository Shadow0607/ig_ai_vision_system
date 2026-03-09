using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 
[Table("media_assets")]
public class MediaAsset
{
    [Key]
    public long Id { get; set; } // BIGINT 對應 long

    [Column("person_id")]
    public int? PersonId { get; set; } // 🟢 修正 2：對齊 SQL 的 DEFAULT NULL [1]

    // 🟢 修正 1：補上缺失的 SystemName，對應資料庫中的 NOT NULL 冗餘欄位 [1]
    [Column("system_name")]
    public required string SystemName { get; set; }

    [Column("account_id")]
    public int? AccountId { get; set; } // Nullable，因為可能無法追蹤來源

    [Column("file_name")]
    public string FileName { get; set; } = string.Empty;

    [Column("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [Column("media_type_id")]
    public int MediaTypeId { get; set; }
    
    [ForeignKey("MediaTypeId")]  // 🌟 補上這個標籤
    public SysStatus? MediaType { get; set; } 

    [Column("source_type_id")]
    public int SourceTypeId { get; set; }
    
    [ForeignKey("SourceTypeId")] // 🌟 補上這個標籤
    public SysStatus? SourceType { get; set; } 

    [Column("download_status_id")]
    public int DownloadStatusId { get; set; }
    
    [ForeignKey("DownloadStatusId")] // 🌟 補上這個標籤
    public SysStatus? DownloadStatus { get; set; }// 🟢 修正 3：對齊 SQL 的 DEFAULT 'UNKNOWN' [2]

    [Column("image_hash")]
    public string? ImageHash { get; set; } 

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [Column("original_username")]
    public string? OriginalUsername { get; set; }

    [Column("original_shortcode")]
    public string? OriginalShortcode { get; set; }

    [Column("source_is_verified")]
    public bool SourceIsVerified { get; set; } // 映射 TINYINT(1)

    // 關聯導航屬性
    [ForeignKey("PersonId")]
    public TargetPerson? Person { get; set; }

    [ForeignKey("AccountId")]
    public SocialAccount? Account { get; set; }
}