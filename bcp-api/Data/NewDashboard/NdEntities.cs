using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Data.NewDashboard.Entities;

[Table("profiles")]
public class NdProfile
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("full_name")]
    public string FullName { get; set; } = "";

    [Column("role")]
    public string Role { get; set; } = "maker";

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [ForeignKey(nameof(DepartmentId))]
    public NdDepartment? Department { get; set; }
}

[Table("departments")]
public class NdDepartment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("regulation_documents")]
public class NdRegulationDocument
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("stored_document_id")]
    public Guid? StoredDocumentId { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("file_path")]
    public string FilePath { get; set; } = "";

    [Column("file_url")]
    public string? FileUrl { get; set; }

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("extraction_status")]
    public string ExtractionStatus { get; set; } = "pending";

    [Column("extraction_progress_label")]
    public string? ExtractionProgressLabel { get; set; }

    [Column("extraction_progress_pct")]
    public int? ExtractionProgressPct { get; set; }

    /// <summary>Last fully parsed PDF chunk index (0-based), for resume after pause.</summary>
    [Column("extraction_parse_chunk_completed")]
    public int? ExtractionParseChunkCompleted { get; set; }

    [Column("extraction_result")]
    public string? ExtractionResult { get; set; }

    [Column("extraction_markdown")]
    public string? ExtractionMarkdown { get; set; }

    [Column("extracted_at")]
    public DateTimeOffset? ExtractedAt { get; set; }

    [Column("extracted_by")]
    public Guid? ExtractedBy { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("is_manual")]
    public bool IsManual { get; set; }

    /// <summary>1 = visible, -1 = hidden (soft-deleted, not removed from DB).</summary>
    [Column("status")]
    public int Status { get; set; } = 1;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<NdRegulationPoint> Points { get; set; } = [];
}

[Table("regulation_points")]
public class NdRegulationPoint
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("regulation_document_id")]
    public Guid RegulationDocumentId { get; set; }

    [Column("point_number")]
    public string PointNumber { get; set; } = "";

    [Column("point_title")]
    public string? PointTitle { get; set; }

    [Column("point_content")]
    public string PointContent { get; set; } = "";

    [Column("page_reference")]
    public string? PageReference { get; set; }

    [Column("is_introduction_point")]
    public bool IsIntroductionPoint { get; set; }

    [Column("is_annex_point")]
    public bool IsAnnexPoint { get; set; }

    /// <summary>1 = active, -1 = soft-deleted (kept for audit/recovery).</summary>
    [Column("status")]
    public int Status { get; set; } = NdRegulationPointStatus.Active;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("nd_internal_document_sections")]
public class NdInternalDocumentSection
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("stored_document_id")]
    public Guid StoredDocumentId { get; set; }

    [Column("section_ref")]
    public string SectionRef { get; set; } = "";

    [Column("section_text")]
    public string SectionText { get; set; } = "";

    [Column("source_page")]
    public int? SourcePage { get; set; }

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("libraries")]
public class NdLibrary
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<NdLibraryPoint> LibraryPoints { get; set; } = [];
}

[Table("library_points")]
public class NdLibraryPoint
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("library_id")]
    public Guid LibraryId { get; set; }

    [Column("regulation_point_id")]
    public Guid RegulationPointId { get; set; }

    [Column("regulation_document_id")]
    public Guid RegulationDocumentId { get; set; }

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("point_snapshot")]
    public string? PointSnapshot { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("analysis_runs")]
public class NdAnalysisRun
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    /// <summary>Landing AI + dual-verify prompt revision for this run (v1|v2|v3). Null = ND default (v2).</summary>
    [Column("compare_prompt_version")]
    public string? ComparePromptVersion { get; set; }

    /// <summary>bcp_landing (V8) or regul_pipeline (Regul workflow V3).</summary>
    [Column("workflow_engine")]
    public string WorkflowEngine { get; set; } = "bcp_landing";

    [Column("enable_qualitative")]
    public bool EnableQualitative { get; set; }

    [Column("regul_llm_provider")]
    public string? RegulLlmProvider { get; set; }

    [Column("regul_llm_model")]
    public string? RegulLlmModel { get; set; }

    /// <summary>forward | reverse | qualitative | done</summary>
    [Column("regul_pipeline_phase")]
    public string? RegulPipelinePhase { get; set; }

    [Column("regul_pipeline_error")]
    public string? RegulPipelineError { get; set; }

    /// <summary>Regul workflow: maker confirmed clause list before Run analysis (Regul.ai extraction_review gate).</summary>
    [Column("regul_clauses_confirmed_at")]
    public DateTimeOffset? RegulClausesConfirmedAt { get; set; }

    [Column("library_id")]
    public Guid? LibraryId { get; set; }

    [Column("selected_points_snapshot")]
    public string SelectedPointsSnapshot { get; set; } = "[]";

    [Column("selected_internal_doc_ids")]
    public string SelectedInternalDocIds { get; set; } = "[]";

    [Column("selected_regulation_doc_ids")]
    public string SelectedRegulationDocIds { get; set; } = "[]";

    [Column("status")]
    public string Status { get; set; } = "draft";

    [Column("status_before_delete")]
    public string? StatusBeforeDelete { get; set; }

    [Column("deleted_at")]
    public DateTimeOffset? DeletedAt { get; set; }

    [Column("total_points_count")]
    public int TotalPointsCount { get; set; }

    [Column("processed_points_count")]
    public int ProcessedPointsCount { get; set; }

    [Column("landing_ai_completed_count")]
    public int LandingAiCompletedCount { get; set; }

    [Column("dual_verify_completed_count")]
    public int DualVerifyCompletedCount { get; set; }

    [Column("dual_verify_failed_count")]
    public int DualVerifyFailedCount { get; set; }

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("dual_verify_session_id")]
    public Guid? DualVerifySessionId { get; set; }

    [Column("compliance_session_id")]
    public Guid? ComplianceSessionId { get; set; }

    [Column("submitted_to_checker_at")]
    public DateTimeOffset? SubmittedToCheckerAt { get; set; }

    [Column("checker_reviewed_at")]
    public DateTimeOffset? CheckerReviewedAt { get; set; }

    [Column("submitted_to_reviewer_at")]
    public DateTimeOffset? SubmittedToReviewerAt { get; set; }

    [Column("reviewer_finalized_at")]
    public DateTimeOffset? ReviewerFinalizedAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<NdAnalysisPoint> Points { get; set; } = [];
}

[Table("analysis_points")]
public class NdAnalysisPoint
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("regulation_point_id")]
    public Guid? RegulationPointId { get; set; }

    [Column("point_snapshot")]
    public string PointSnapshot { get; set; } = "{}";

    [Column("landing_ai_status")]
    public string LandingAiStatus { get; set; } = "pending";

    [Column("landing_ai_result")]
    public string? LandingAiResult { get; set; }

    [Column("landing_ai_action_plan")]
    public string? LandingAiActionPlan { get; set; }

    [Column("landing_ai_run_at")]
    public DateTimeOffset? LandingAiRunAt { get; set; }

    [Column("landing_ai_error")]
    public string? LandingAiError { get; set; }

    [Column("google_ai_status")]
    public string GoogleAiStatus { get; set; } = "pending";

    [Column("google_ai_result")]
    public string? GoogleAiResult { get; set; }

    [Column("google_ai_run_at")]
    public DateTimeOffset? GoogleAiRunAt { get; set; }

    [Column("google_ai_error")]
    public string? GoogleAiError { get; set; }

    [Column("dual_verify_status")]
    public string DualVerifyStatus { get; set; } = "pending";

    [Column("dual_verify_run_at")]
    public DateTimeOffset? DualVerifyRunAt { get; set; }

    [Column("final_status")]
    public string? FinalStatus { get; set; }

    [Column("final_action_plan")]
    public string? FinalActionPlan { get; set; }

    [Column("original_ai_action_plan")]
    public string? OriginalAiActionPlan { get; set; }

    [Column("landing_ai_rerun_count")]
    public int LandingAiRerunCount { get; set; }

    [Column("dual_verify_rerun_count")]
    public int DualVerifyRerunCount { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("analysis_point_attachments")]
public class NdAnalysisPointAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    [Column("stored_document_id")]
    public Guid StoredDocumentId { get; set; }

    [Column("file_name")]
    public string FileName { get; set; } = "";

    [Column("action_index")]
    public int? ActionIndex { get; set; }

    [Column("uploaded_by")]
    public Guid? UploadedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("action_plan_history")]
public class NdActionPlanHistory
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    [Column("action_plan_content")]
    public string ActionPlanContent { get; set; } = "";

    [Column("version_number")]
    public int VersionNumber { get; set; }

    [Column("change_type")]
    public string ChangeType { get; set; } = "ai_original";

    [Column("reverted_to_version")]
    public int? RevertedToVersion { get; set; }

    [Column("changed_by")]
    public Guid? ChangedBy { get; set; }

    [Column("is_current")]
    public bool IsCurrent { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("analysis_reviews")]
public class NdAnalysisReview
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("reviewer_id")]
    public Guid? ReviewerId { get; set; }

    [Column("reviewer_role")]
    public string ReviewerRole { get; set; } = "checker";

    [Column("action")]
    public string Action { get; set; } = "submitted";

    [Column("overall_comment")]
    public string? OverallComment { get; set; }

    [Column("review_status")]
    public string? ReviewStatus { get; set; }

    [Column("priority")]
    public int? Priority { get; set; }

    [Column("responsibility")]
    public string? Responsibility { get; set; }

    [Column("due_date")]
    public DateTimeOffset? DueDate { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("analysis_point_comments")]
public class NdAnalysisPointComment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    [Column("analysis_review_id")]
    public Guid? AnalysisReviewId { get; set; }

    [Column("comment")]
    public string Comment { get; set; } = "";

    [Column("commented_by")]
    public Guid? CommentedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("action_plan_item_reviews")]
public class NdActionPlanItemReview
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    [Column("analysis_review_id")]
    public Guid? AnalysisReviewId { get; set; }

    [Column("action_index")]
    public int ActionIndex { get; set; }

    /// <summary>approve | need_modify | uix</summary>
    [Column("status")]
    public string Status { get; set; } = "";

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("responsibility")]
    public string? Responsibility { get; set; }

    [Column("due_date")]
    public DateTimeOffset? DueDate { get; set; }

    /// <summary>medium | higher</summary>
    [Column("priority")]
    public string? Priority { get; set; }

    [Column("reviewed_by")]
    public Guid? ReviewedBy { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Soft-delete marker for legacy analyses (document_analysis_runs / dual_verify_sessions)
/// shown in the ND runs list. Legacy tables stay untouched; a row here hides the run.
/// </summary>
[Table("hidden_legacy_runs")]
public class NdHiddenLegacyRun
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>legacy_analysis | legacy_dual_verify</summary>
    [Column("source")]
    public string Source { get; set; } = "";

    [Column("legacy_id")]
    public Guid LegacyId { get; set; }

    [Column("deleted_by")]
    public Guid? DeletedBy { get; set; }

    [Column("deleted_at")]
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("analysis_status_history")]
public class NdAnalysisStatusHistory
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("from_status")]
    public string? FromStatus { get; set; }

    [Column("to_status")]
    public string ToStatus { get; set; } = "";

    [Column("changed_by")]
    public Guid? ChangedBy { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[Table("nd_system_settings")]
public class NdSystemSetting
{
    [Key]
    [Column("key")]
    public string Key { get; set; } = "";

    [Column("value_json")]
    public string ValueJson { get; set; } = "{}";

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_by")]
    public Guid? UpdatedBy { get; set; }
}
