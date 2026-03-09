using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 

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