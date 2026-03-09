using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; // File-scoped namespace (C# 10+)


// [Table 2] 帳號類型


// [Table 3] 核心人物


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

