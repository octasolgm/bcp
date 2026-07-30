using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

[Table("regul_forward_findings")]
public class NdRegulForwardFinding
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("analysis_point_id")]
    public Guid? AnalysisPointId { get; set; }

    [Column("clause_no")]
    public string ClauseNo { get; set; } = "";

    [Column("clause_text")]
    public string ClauseText { get; set; } = "";

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("regul_internal_sections")]
public class NdRegulInternalSection
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("section_ref")]
    public string SectionRef { get; set; } = "";

    [Column("section_text")]
    public string SectionText { get; set; } = "";

    [Column("source_doc")]
    public string? SourceDoc { get; set; }

    [Column("source_page")]
    public int? SourcePage { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("regul_reverse_mappings")]
public class NdRegulReverseMapping
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("internal_section_id")]
    public Guid InternalSectionId { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("mapping")]
    public string? Mapping { get; set; }

    [Column("mapped_clause_nos")]
    public string? MappedClauseNos { get; set; }

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("regul_qualitative_assessments")]
public class NdRegulQualitativeAssessment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
