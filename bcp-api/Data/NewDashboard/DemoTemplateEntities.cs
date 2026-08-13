using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// Platform-owned demo judgment templates (never cleared by demo workspace reset).
/// Matched to runs by regulation / internal document name hints.
/// </summary>
[Table("demo_analysis_templates")]
public class NdDemoAnalysisTemplate
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable code, e.g. analys1demo.</summary>
    [Column("code")]
    public string Code { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    /// <summary>Match regulation doc name (e.g. CBUAE_EN_3945_VER2).</summary>
    [Column("regulation_name_hint")]
    public string RegulationNameHint { get; set; } = "";

    /// <summary>Match internal doc name (e.g. 290626 / AML Manual).</summary>
    [Column("internal_name_hint")]
    public string InternalNameHint { get; set; } = "";

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<NdDemoAnalysisTemplatePoint> Points { get; set; } = [];
}

[Table("demo_analysis_template_points")]
public class NdDemoAnalysisTemplatePoint
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("template_id")]
    public Guid TemplateId { get; set; }

    [Column("clause_no")]
    public string ClauseNo { get; set; } = "";

    [Column("clause_title")]
    public string? ClauseTitle { get; set; }

    [Column("design_status")]
    public string DesignStatus { get; set; } = "partial";

    [Column("operating_status")]
    public string OperatingStatus { get; set; } = "partial";

    [Column("overall_status")]
    public string OverallStatus { get; set; } = "partial";

    [Column("confidence")]
    public double Confidence { get; set; }

    [Column("interpretation")]
    public string Interpretation { get; set; } = "";

    /// <summary>JSON array of policy extract strings.</summary>
    [Column("policy_extract_json")]
    public string PolicyExtractJson { get; set; } = "[]";

    [Column("document_reference")]
    public string DocumentReference { get; set; } = "";

    [Column("gap_description")]
    public string GapDescription { get; set; } = "";

    [Column("suggested_action")]
    public string SuggestedAction { get; set; } = "";

    [Column("gap_direction")]
    public string GapDirection { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(TemplateId))]
    public NdDemoAnalysisTemplate? Template { get; set; }
}
