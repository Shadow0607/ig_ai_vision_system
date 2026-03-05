using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("system_routes")]
public class SystemRoute
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    [Column("route_name")]
    public string RouteName { get; set; } = null!; // 例如 "UserManagement"

    [Required]
    [MaxLength(100)]
    [Column("path")]
    public string Path { get; set; } = null!; // 例如 "/user-management"

    [Required]
    [MaxLength(100)]
    [Column("title")]
    public string Title { get; set; } = null!; // 例如 "帳號權限管理"
    
    [MaxLength(50)]
    [Column("icon")]
    public string Icon { get; set; } = "📌";

    [Column("is_public")]
    public bool IsPublic { get; set; } = false; // 是否不需登入即可查看

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}