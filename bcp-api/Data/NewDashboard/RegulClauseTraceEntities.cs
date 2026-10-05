using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// Audit trail of one clause's trip through the Regul pipeline: what Step 7 built as the judgment
/// context, every Step 8 LLM call (request and raw response, per attempt), and what post-processing
/// changed before the result was saved. Lets an analyst verify exactly what the AI saw and said.
/// </summary>
[Table("regul_clause_traces")]
public class NdRegulClauseTrace : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("finding_id")]
    public Guid? FindingId { get; set; }

    [Column("clause_no")]
    public string ClauseNo { get; set; } = "";

    /// <summary>context | llm_call | postprocess | evidence_check</summary>
    [Column("step")]
    public string Step { get; set; } = "";

    /// <summary>Which entry point ran the pipeline: analysis | rerun_all | clause_rerun | gap_evidence</summary>
    [Column("source")]
    public string Source { get; set; } = RegulClauseTraceSources.Analysis;

    [Column("attempt")]
    public int Attempt { get; set; }

    [Column("provider")]
    public string? Provider { get; set; }

    [Column("model")]
    public string? Model { get; set; }

    [Column("system_prompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>Step 7: the exact context block sent to the LLM.</summary>
    [Column("context_text")]
    public string? ContextText { get; set; }

    /// <summary>Step 7: JSON array of { label, chars } for each chunk in the context.</summary>
    [Column("chunks_json")]
    public string? ChunksJson { get; set; }

    [Column("query_text")]
    public string? QueryText { get; set; }

    [Column("response_text")]
    public string? ResponseText { get; set; }

    /// <summary>postprocess: the final judgment JSON that was saved.</summary>
    [Column("result_json")]
    public string? ResultJson { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("error")]
    public string? Error { get; set; }

    [Column("chars_sent")]
    public int? CharsSent { get; set; }

    [Column("duration_ms")]
    public int? DurationMs { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}

public static class RegulClauseTraceSteps
{
    public const string Context = "context";
    public const string LlmCall = "llm_call";
    public const string PostProcess = "postprocess";
    public const string EvidenceCheck = "evidence_check";
}

public static class RegulClauseTraceSources
{
    public const string Analysis = "analysis";
    public const string RerunAll = "rerun_all";
    public const string ClauseRerun = "clause_rerun";
    public const string GapEvidence = "gap_evidence";
}
