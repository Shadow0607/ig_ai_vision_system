using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("system_alerts")]
public class SystemAlert
{
    [Key] public long Id { get; set; }
    
    // ❌ 刪除這行：已經不需要存字串了
    // [Column("alert_type")] public required string AlertType { get; set; } 
    
    [Column("source_component")] public string? SourceComponent { get; set; }
    [Column("message")] public string? Message { get; set; }
    [Column("is_resolved")] public bool IsResolved { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    [Column("alert_type_id")]
    public int AlertTypeId { get; set; }

    [ForeignKey("AlertTypeId")]
    public SysStatus? AlertType { get; set; } // 這是導覽屬性，保留這個就好
}