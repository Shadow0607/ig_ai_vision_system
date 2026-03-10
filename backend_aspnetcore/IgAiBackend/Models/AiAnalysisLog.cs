using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace IgAiBackend.Models; 
[Table("ai_analysis_logs")]
public class AiAnalysisLog
{
    [Key]
    public long Id { get; set; }

    [Column("media_id")]
    public long MediaId { get; set; }

    [Column("face_detected")]
    public bool FaceDetected { get; set; }

    [Column("confidence_score")]
    public float? ConfidenceScore { get; set; }
    [Column("status_id")]
    public int StatusId { get; set; }

    [ForeignKey("StatusId")]
    public SysStatus? Status { get; set; } // 對應 sys_statuses 表

    [Column("hitl_reviewed_at")]
    public DateTime? HitlReviewedAt { get; set; }

    [Column("processed_at")]
    public DateTime ProcessedAt { get; set; } = DateTime.Now;
    [Column("reviewed_by")]
    public string? ReviewedBy { get; set; }

    [ForeignKey("MediaId")]
    public MediaAsset MediaAsset { get; set; } = null!;
}