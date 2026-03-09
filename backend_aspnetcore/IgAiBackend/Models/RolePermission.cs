using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("role_permissions")]
public class RolePermission
{
    [Key]
    public int Id { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    [Column("route_id")]
    public int RouteId { get; set; }
    public SystemRoute SystemRoute { get; set; } = null!;

    [Column("action_id")]
    public int ActionId { get; set; }
    
    [ForeignKey("ActionId")]
    public SysAction Action { get; set; } = null!;
}