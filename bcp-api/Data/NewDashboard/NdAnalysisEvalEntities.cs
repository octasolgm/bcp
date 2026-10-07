using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// A clause eval: a frozen copy of ONE clause's AI result (verdict, gaps, actions, evidence) taken from an
/// analysis, with the prompt versions (text included), retrieval pipeline version and AI model that produced
/// it. Saving the same clause again adds the next version (v1, v2, ...); one version per clause is current,
/// and new analyses are compared clause by clause against the current versions. Independent of the source
/// run: re-running or deleting that run never changes the eval.
/// </summary>
[Table("nd_clause_evals")]
public class NdClauseEval : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Clause number as shown ("3.5").</summary>
    [Column("clause_no")]
    public string ClauseNo { get; set; } = "";

    /// <summary>Identity of the clause across analyses: regulation document id (or name when unknown) plus
    /// the normalized clause number. Versions and "current" are per key.</summary>
    [Column("clause_key")]
    public string ClauseKey { get; set; } = "";

    [Column("clause_title")]
    public string ClauseTitle { get; set; } = "";

    [Column("clause_text")]
    public string ClauseText { get; set; } = "";

    [Column("regulation_document_id")]
    public Guid? RegulationDocumentId { get; set; }

    [Column("regulation_name")]
    public string? RegulationName { get; set; }

    [Column("version_number")]
    public int VersionNumber { get; set; }

    [Column("is_current")]
    public bool IsCurrent { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("source_run_id")]
    public Guid? SourceRunId { get; set; }

    [Column("source_run_name")]
    public string SourceRunName { get; set; } = "";

    [Column("llm_provider")]
    public string? LlmProvider { get; set; }

    [Column("llm_model")]
    public string? LlmModel { get; set; }

    [Column("pipeline_version")]
    public int? PipelineVersion { get; set; }

    [Column("overall_status")]
    public string OverallStatus { get; set; } = "";

    [Column("gap_count")]
    public int GapCount { get; set; }

    /// <summary>JSON array of { promptKey, versionNumber, label, promptText } that produced this clause.</summary>
    [Column("prompt_versions_json")]
    public string PromptVersionsJson { get; set; } = "[]";

    /// <summary>The clause's full result (NdAnalysisEvalService.EvalClause) as JSON.</summary>
    [Column("result_json")]
    public string ResultJson { get; set; } = "{}";

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}
