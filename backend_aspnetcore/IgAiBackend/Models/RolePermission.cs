using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("role_permissions")]
public class RolePermission
{
    [Column("role_id")]
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    [Column("route_id")]
    public int RouteId { get; set; }
    public SystemRoute SystemRoute { get; set; } = null!;

    // 🌟 新增：細顆粒度操作權限
    [Column("can_view")]
    public bool CanView { get; set; } = true;    // 檢視權限 (預設開啟)

    [Column("can_create")]
    public bool CanCreate { get; set; } = false; // 新增權限

    [Column("can_update")]
    public bool CanUpdate { get; set; } = false; // 修改/覆核權限

    [Column("can_delete")]
    public bool CanDelete { get; set; } = false; // 刪除權限
}