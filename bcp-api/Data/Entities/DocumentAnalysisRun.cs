using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.Entities;

/// <summary>
/// One Analyse / dual-verify run against a compliance document (and optional regulation file).
/// Same compliance PDF can have many runs (different regulations / point sets).
/// </summary>
[Table("document_analysis_runs")]
public class DocumentAnalysisRun
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("workspace_id")]
    public string WorkspaceId { get; set; } = "snb-uae-difc";

    /// <summary>Compliance / internal stored_documents.id</summary>
    [Column("internal_document_id")]
    public Guid? InternalDocumentId { get; set; }

    /// <summary>Regulation stored_documents.id (optional)</summary>
    [Column("regulation_document_id")]
    public Guid? RegulationDocumentId { get; set; }

    /// <summary>Kafka dual-verify session id (nullable when only a compliance report session exists).</summary>
    [Column("dual_verify_session_id")]
    public Guid? DualVerifySessionId { get; set; }

    [Column("compliance_session_id")]
    public Guid? ComplianceSessionId { get; set; }

    [Column("label")]
    public string Label { get; set; } = "";

    [Column("regulation_file_name")]
    public string? RegulationFileName { get; set; }

    [Column("internal_file_name")]
    public string? InternalFileName { get; set; }

    [Column("internal_file_hash")]
    public string? InternalFileHash { get; set; }

    [Column("gov_file_hash")]
    public string? GovFileHash { get; set; }

    [Column("status")]
    public string Status { get; set; } = "queued";

    [Column("point_count")]
    public int PointCount { get; set; }

    [Column("completed_points")]
    public int CompletedPoints { get; set; }

    [Column("granularity")]
    public string Granularity { get; set; } = "leaf";

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
