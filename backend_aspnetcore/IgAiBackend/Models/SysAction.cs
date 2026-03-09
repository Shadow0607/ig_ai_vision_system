using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IgAiBackend.Models;

[Table("sys_actions")]
public class SysAction
{
    [Key]
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty; // VIEW, CREATE, UPDATE, DELETE, APPROVE
    [Column("display_name")] // 指定資料庫實際的欄位名稱
    public string DisplayName { get; set; } = string.Empty;
}