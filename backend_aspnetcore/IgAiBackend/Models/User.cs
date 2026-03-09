using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 
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