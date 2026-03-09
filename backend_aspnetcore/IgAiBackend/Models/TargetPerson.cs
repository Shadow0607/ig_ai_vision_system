using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 

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