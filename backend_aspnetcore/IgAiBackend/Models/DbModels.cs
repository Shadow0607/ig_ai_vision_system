using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; // File-scoped namespace (C# 10+)

// [Table 1] 平台
[Table("platforms")]
public class Platform
{
    [Key] public int Id { get; set; }
    [Column("code")] public required string Code { get; set; } // required: C# 11 強制屬性
    [Column("name")] public required string Name { get; set; }
    [Column("base_url")] public string? BaseUrl { get; set; }
}

// [Table 2] 帳號類型
[Table("account_types")]
public class AccountType
{
    [Key] public int Id { get; set; }
    [Column("code")] public required string Code { get; set; }
    [Column("name")] public required string Name { get; set; }
    [Column("weight")] public float Weight { get; set; }
}

// [Table 3] 核心人物
[Table("target_persons")]
public class TargetPerson
{
    [Key] public int Id { get; set; }
    [Column("system_name")] public required string SystemName { get; set; }
    [Column("display_name")] public string? DisplayName { get; set; }
    [Column("threshold")] public double Threshold { get; set; }
    [Column("is_active")] public bool IsActive { get; set; }

    // 關聯: 一個人有多個社群帳號
    public List<SocialAccount> SocialAccounts { get; set; } = new();
}

// [Table 4] 社群帳號
[Table("social_accounts")]
public class SocialAccount
{
    [Key] public int Id { get; set; }
    [Column("person_id")] public int PersonId { get; set; }
    [Column("platform_id")] public int PlatformId { get; set; }
    [Column("account_type_id")] public int AccountTypeId { get; set; }

    [Column("account_identifier")] public required string AccountIdentifier { get; set; }
    [Column("account_name")] public string? AccountName { get; set; }
    [Column("is_monitored")] public bool IsMonitored { get; set; }

    // 導航屬性 (Navigation Properties)
    [JsonIgnore] public TargetPerson? Person { get; set; }
    public Platform? Platform { get; set; }
    public AccountType? AccountType { get; set; }
}
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

    // 關聯導航屬性
    [ForeignKey("PersonId")]
    public TargetPerson? Person { get; set; }

    [ForeignKey("AccountId")]
    public SocialAccount? Account { get; set; }
}

// [Table 6] AI 分析日誌 (對應 ai_analysis_logs)
[Table("ai_analysis_logs")]
public class AiAnalysisLog
{
    [Key]
    public long Id { get; set; }

    [Column("media_id")]
    public long MediaId { get; set; }

    [Column("face_detected")]
    public bool FaceDetected { get; set; }

    [Column("confidence_score")]
    public float? ConfidenceScore { get; set; }

    // 🌟 1. 移除 RecognitionStatus 和 HitlConfirmed
    // 🌟 2. 新增 StatusId 與其導航屬性
    [Column("status_id")]
    public int StatusId { get; set; }

    [ForeignKey("StatusId")]
    public SysStatus? Status { get; set; } // 對應 sys_statuses 表

    [Column("hitl_reviewed_at")]
    public DateTime? HitlReviewedAt { get; set; }

    [Column("processed_at")]
    public DateTime ProcessedAt { get; set; } = DateTime.Now;

    [ForeignKey("MediaId")]
    public MediaAsset MediaAsset { get; set; } = null!;
}

[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    [Column("username")]
    public string Username { get; set; } = null!;

    [Required]
    [MaxLength(255)]
    [Column("password_hash")]
    public string PasswordHash { get; set; } = null!;
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("role_id")]
    public int RoleId { get; set; } // 外鍵

    [ForeignKey("RoleId")]
    public Role Role { get; set; } = null!; // 導覽屬性

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
[Table("sys_statuses")]
public class SysStatus {
    [Key]
    public int Id { get; set; }
    [Column("category")]
    public required string Category { get; set; }
    [Column("code")]
    public required string Code { get; set; }
    [Column("display_name")]
    public required string DisplayName { get; set; }
    [Column("ui_color")]
    public string? UiColor { get; set; }
}