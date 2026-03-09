using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 
[Table("platforms")]
public class Platform
{
    [Key] public int Id { get; set; }
    [Column("code")] public required string Code { get; set; } // required: C# 11 強制屬性
    [Column("name")] public required string Name { get; set; }
    [Column("base_url")] public string? BaseUrl { get; set; }
}