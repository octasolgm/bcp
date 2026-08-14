using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Reguliq.Api.Data.NewDashboard.Entities;

/// <summary>
/// A corrective action plan attached to a single gap (analysis point).
/// A gap can carry many action plans; each action plan can collect review comments.
/// Replaces the older per-action <c>action_plan_item_reviews</c> hybrid, which mixed
/// review verdicts with planning fields.
/// </summary>
[Table("analysis_action_plans")]
public class NdAnalysisActionPlan
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    /// <summary>
    /// 1-based index of the CAP gap this action belongs to. 0 means the action is
    /// attached to the point as a whole (pre-gap-scoping rows).
    /// </summary>
    [Column("gap_index")]
    public int GapIndex { get; set; }

    /// <summary>The action plan text itself.</summary>
    [Column("action_plan")]
    public string ActionPlan { get; set; } = "";

    /// <summary>pending | resolved</summary>
    [Column("status")]
    public string Status { get; set; } = ActionPlanStatuses.Pending;

    /// <summary>low | medium | high — always derived from <see cref="PriorityScore"/>.</summary>
    [Column("priority")]
    public string Priority { get; set; } = ActionPlanPriorities.Medium;

    /// <summary>0–100 risk score the UI slider writes; the tier in <see cref="Priority"/> follows it.</summary>
    [Column("priority_score")]
    public int PriorityScore { get; set; } = ActionPlanPriorities.DefaultScore;

    [Column("target_date")]
    public DateTimeOffset? TargetDate { get; set; }

    /// <summary>department | user</summary>
    [Column("responsibility_type")]
    public string ResponsibilityType { get; set; } = ActionPlanResponsibilityTypes.Department;

    [Column("responsibility_department_id")]
    public Guid? ResponsibilityDepartmentId { get; set; }

    [Column("responsibility_user_id")]
    public Guid? ResponsibilityUserId { get; set; }

    /// <summary>Denormalized label so historical rows survive renames/deletes.</summary>
    [Column("responsibility_label")]
    public string? ResponsibilityLabel { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }

    [Column("resolved_by")]
    public Guid? ResolvedBy { get; set; }

    [Column("created_by")]
    public Guid? CreatedBy { get; set; }

    [Column("updated_by")]
    public Guid? UpdatedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One owner of an action plan. An action can be shared by several departments and/or
/// people, which is what drives each user's inbox. The single
/// <c>responsibility_*</c> columns on the plan stay in sync with the first row here so
/// exports and older screens keep working.
/// </summary>
[Table("analysis_action_plan_assignees")]
public class NdAnalysisActionPlanAssignee
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("action_plan_id")]
    public Guid ActionPlanId { get; set; }

    /// <summary>department | user</summary>
    [Column("assignee_type")]
    public string AssigneeType { get; set; } = ActionPlanResponsibilityTypes.Department;

    [Column("department_id")]
    public Guid? DepartmentId { get; set; }

    [Column("user_id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// Display label. Also the whole value for free-text owners typed into the picker
    /// that match no department or user.
    /// </summary>
    [Column("label")]
    public string Label { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Reviewer/checker note on an action plan. Comment-only by design — the verdict
/// lives on the run workflow, not here.
/// </summary>
[Table("analysis_action_plan_reviews")]
public class NdAnalysisActionPlanReview
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("action_plan_id")]
    public Guid ActionPlanId { get; set; }

    [Column("analysis_point_id")]
    public Guid AnalysisPointId { get; set; }

    [Column("analysis_run_id")]
    public Guid AnalysisRunId { get; set; }

    [Column("comment")]
    public string Comment { get; set; } = "";

    [Column("reviewer_id")]
    public Guid? ReviewerId { get; set; }

    [Column("reviewer_role")]
    public string? ReviewerRole { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Audit trail for target-date changes (re-targeting). Surfaced behind the clock icon.
/// </summary>
[Table("analysis_action_plan_date_history")]
public class NdAnalysisActionPlanDateHistory
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("action_plan_id")]
    public Guid ActionPlanId { get; set; }

    [Column("previous_target_date")]
    public DateTimeOffset? PreviousTargetDate { get; set; }

    [Column("new_target_date")]
    public DateTimeOffset? NewTargetDate { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("changed_by")]
    public Guid? ChangedBy { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ActionPlanStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Resolved, StringComparison.OrdinalIgnoreCase) ? Resolved : Pending;
}

public static class ActionPlanPriorities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    /// <summary>Score bands shared with the web risk standard: 0–33 low, 34–66 medium, 67–100 high.</summary>
    public const int LowMaxScore = 33;
    public const int MediumMaxScore = 66;
    public const int DefaultScore = 50;

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => Low,
        "high" or "higher" or "critical" => High,
        _ => Medium,
    };

    public static int ClampScore(int score) => Math.Min(100, Math.Max(0, score));

    public static string TierFromScore(int score) => ClampScore(score) switch
    {
        <= LowMaxScore => Low,
        <= MediumMaxScore => Medium,
        _ => High,
    };

    /// <summary>Midpoint score for a tier, used to backfill rows saved before the slider existed.</summary>
    public static int ScoreFromTier(string? tier) => Normalize(tier) switch
    {
        Low => 20,
        High => 85,
        _ => DefaultScore,
    };
}

public static class ActionPlanResponsibilityTypes
{
    public const string Department = "department";
    public const string User = "user";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), User, StringComparison.OrdinalIgnoreCase) ? User : Department;
}
