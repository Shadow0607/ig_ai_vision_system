using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 
[Table("account_types")]
public class AccountType
{
    [Key] public int Id { get; set; }
    [Column("code")] public required string Code { get; set; }
    [Column("name")] public required string Name { get; set; }
    [Column("weight")] public float Weight { get; set; }
}