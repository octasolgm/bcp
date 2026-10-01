using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// One "rerun gaps against uploaded evidence" job on a finished analysis. Tracks the same phases a
/// new analysis reports (parsing, retrieval, forward judgment, done) without touching the run's own
/// workflow status, so a report that is with the checker or already finalized stays where it is.
/// </summary>
[Table("gap_evidence_reruns")]
public class NdGapEvidenceRerun : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    /// <summary>report (every open clause) | clause (one clause, optionally one gap)</summary>
    [Column("scope")]
    public string Scope { get; set; } = GapEvidenceRerunScopes.Report;

    /// <summary>queued | running | completed | failed</summary>
    [Column("status")]
    public string Status { get; set; } = GapEvidenceStatuses.Queued;

    /// <summary>queued | parsing | retrieval | forward | done</summary>
    [Column("phase")]
    public string Phase { get; set; } = GapEvidencePhases.Queued;

    [Column("phase_detail")]
    public string? PhaseDetail { get; set; }

    /// <summary>JSON array of { id, name } for every evidence document the job used.</summary>
    [Column("evidence_documents_json")]
    public string EvidenceDocumentsJson { get; set; } = "[]";

    [Column("total_points")]
    public int TotalPoints { get; set; }

    [Column("completed_points")]
    public int CompletedPoints { get; set; }

    [Column("failed_points")]
    public int FailedPoints { get; set; }

    [Column("fulfilled_gaps")]
    public int FulfilledGaps { get; set; }

    [Column("partial_gaps")]
    public int PartialGaps { get; set; }

    [Column("open_gaps")]
    public int OpenGaps { get; set; }

    [Column("resolved_actions")]
    public int ResolvedActions { get; set; }

    [Column("split_actions")]
    public int SplitActions { get; set; }

    [Column("error")]
    public string? Error { get; set; }

    [Column("llm_provider")]
    public string? LlmProvider { get; set; }

    [Column("llm_model")]
    public string? LlmModel { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}

/// <summary>
/// The evidence verdict for one clause inside a rerun. The clause's original judgment and gap list
/// are never rewritten; this row is the record of what the new document covered, what is still
/// missing, which quotes support it, and what happened to each action plan.
/// </summary>
[Table("gap_evidence_reviews")]
public class NdGapEvidenceReview : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("rerun_id")]
    public Guid RerunId { get; set; }

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    /// <summary>Set when only one gap of the clause was re-checked.</summary>
    [Column("gap_index_filter")]
    public int? GapIndexFilter { get; set; }

    /// <summary>queued | running | completed | failed</summary>
    [Column("status")]
    public string Status { get; set; } = GapEvidenceStatuses.Queued;

    [Column("clause_no")]
    public string? ClauseNo { get; set; }

    /// <summary>fulfilled | partially_fulfilled | not_fulfilled</summary>
    [Column("clause_outcome")]
    public string? ClauseOutcome { get; set; }

    [Column("prior_final_status")]
    public string? PriorFinalStatus { get; set; }

    [Column("new_final_status")]
    public string? NewFinalStatus { get; set; }

    [Column("summary")]
    public string? Summary { get; set; }

    /// <summary>JSON array of { id, name } for the evidence documents this clause was checked against.</summary>
    [Column("evidence_documents_json")]
    public string EvidenceDocumentsJson { get; set; } = "[]";

    /// <summary>JSON array: gap index, gap text, outcome, covered, remaining, quotes.</summary>
    [Column("gaps_json")]
    public string GapsJson { get; set; } = "[]";

    /// <summary>JSON array: action plan id, original text, outcome, fulfilled/remaining parts, what was done.</summary>
    [Column("actions_json")]
    public string ActionsJson { get; set; } = "[]";

    /// <summary>The clause's retrieval output (Steps 1-6) from the re-check, for the pipeline panel.</summary>
    [Column("retrieval_json")]
    public string? RetrievalJson { get; set; }

    /// <summary>The New Analysis judgment of the clause re-run with the evidence included.</summary>
    [Column("reanalysis_json")]
    public string? ReanalysisJson { get; set; }

    /// <summary>JSON array of the retrieved evidence sections handed to the model.</summary>
    [Column("context_json")]
    public string ContextJson { get; set; } = "[]";

    [Column("error")]
    public string? Error { get; set; }

    [Column("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Owning workspace (nd_workspaces.id).</summary>
    public Guid? TenantId { get; set; }
}

public static class GapEvidenceRerunScopes
{
    public const string Report = "report";
    public const string Clause = "clause";
}

public static class GapEvidenceStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsActive(string? status) => status is Queued or Running;
}

public static class GapEvidencePhases
{
    public const string Queued = "queued";
    public const string Parsing = "parsing";
    public const string Retrieval = "retrieval";
    public const string Forward = "forward";
    public const string Done = "done";
}

public static class GapEvidenceOutcomes
{
    public const string Fulfilled = "fulfilled";
    public const string Partial = "partially_fulfilled";
    public const string NotFulfilled = "not_fulfilled";

    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (v.Contains("partial")) return Partial;
        if (v.StartsWith("not") || v.StartsWith("un") || v is "no" or "none" or "missing" or "open") return NotFulfilled;
        if (v is "fulfilled" or "fulfilled_fully" or "fully_fulfilled" or "covered" or "fully_covered"
            or "met" or "compliant" or "closed" or "resolved" or "satisfied" or "done" or "yes")
            return Fulfilled;
        return NotFulfilled;
    }
}
