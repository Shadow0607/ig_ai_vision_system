using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("roles")]
public class Role
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    [Column("code")]
    public string Code { get; set; } = null!; // 例如 "Admin", "Reviewer"

    [Required]
    [MaxLength(50)]
    [Column("name")]
    public string Name { get; set; } = null!; // 例如 "管理員", "覆核員"

    // 導覽屬性：一個角色擁有多個使用者
    public ICollection<User> Users { get; set; } = new List<User>();
    
    // 導覽屬性：一個角色擁有多個路由權限
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}