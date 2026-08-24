using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Corrective action plans attached to a gap. All four roles may create and edit
/// plans; checker/reviewer additionally leave comment-only reviews on them.
/// </summary>
[ApiController]
[Route("nd/results/{runId:guid}/action-plans")]
public class ActionPlansController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    private static readonly string[] AllRoles = ["super_admin", "maker", "checker", "reviewer"];
    private static readonly string[] ReviewRoles = ["super_admin", "checker", "reviewer"];

    /// <summary>
    /// One owner picked in the responsibility field. <paramref name="Label"/> alone is a
    /// free-text owner that matches no department or user.
    /// </summary>
    public record AssigneeInput(string? Type, Guid? DepartmentId, Guid? UserId, string? Label);

    public record SaveActionPlanRequest(
        Guid AnalysisPointId,
        int? GapIndex,
        string? ActionPlan,
        string? Status,
        string? Priority,
        int? PriorityScore,
        string? TargetDate,
        string? ResponsibilityType,
        Guid? ResponsibilityDepartmentId,
        Guid? ResponsibilityUserId,
        List<AssigneeInput>? Assignees,
        string? Comment);

    public record UpdateActionPlanRequest(
        int? GapIndex,
        string? ActionPlan,
        string? Status,
        string? Priority,
        int? PriorityScore,
        string? TargetDate,
        string? TargetDateChangeReason,
        string? ResponsibilityType,
        Guid? ResponsibilityDepartmentId,
        Guid? ResponsibilityUserId,
        List<AssigneeInput>? Assignees,
        string? Comment);

    public record ReorderRequest(string Direction);

    public record SaveReviewRequest(
        string Comment,
        string? AssigneeType,
        Guid? AssigneeDepartmentId,
        Guid? AssigneeUserId,
        string? AssigneeLabel);

    // ---------------------------------------------------------------- list

    [HttpGet]
    public async Task<IActionResult> List(Guid runId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        var accessError = await EnsureRunVisibleAsync(run, ct);
        if (accessError != null) return accessError;

        var plans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .OrderBy(p => p.GapIndex).ThenBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var planIds = plans.Select(p => p.Id).ToList();

        var reviews = planIds.Count == 0
            ? []
            : await db.NdAnalysisActionPlanReviews.AsNoTracking()
                .Where(r => planIds.Contains(r.ActionPlanId))
                .OrderBy(r => r.CreatedAt)
                .ToListAsync(ct);

        var names = await LoadProfileNamesAsync(
            db,
            plans.SelectMany(p => new[] { p.CreatedBy, p.UpdatedBy, p.ResolvedBy, p.ResponsibilityUserId })
                .Concat(reviews.Select(r => r.ReviewerId)),
            ct);

        var departmentNames = await LoadDepartmentNamesAsync(plans, ct);
        var assignees = await LoadAssigneesAsync(planIds, ct);

        var reviewsByPlan = reviews
            .GroupBy(r => r.ActionPlanId)
            .ToDictionary(g => g.Key, g => g.Select(r => MapReview(r, names)).ToList());

        return Ok(new
        {
            success = true,
            data = plans.Select(p => MapPlan(
                p,
                names,
                departmentNames,
                reviewsByPlan.TryGetValue(p.Id, out var rs) ? rs : [],
                assignees.TryGetValue(p.Id, out var asg) ? asg : [])),
        });
    }

    // -------------------------------------------------------------- create

    [HttpPost]
    public async Task<IActionResult> Create(Guid runId, [FromBody] SaveActionPlanRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        var accessError = await EnsureRunVisibleAsync(run, ct);
        if (accessError != null) return accessError;

        var point = await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == body.AnalysisPointId && p.AnalysisRunId == runId, ct);
        if (point == null) return NotFound(new { success = false, message = "Gap point not found for this run." });

        if (string.IsNullOrWhiteSpace(body.ActionPlan))
            return BadRequest(new { success = false, message = "Action plan text is required." });

        var gapIndex = Math.Max(0, body.GapIndex ?? 0);

        var maxOrder = await db.NdAnalysisActionPlans
            .Where(p => p.AnalysisPointId == body.AnalysisPointId && p.GapIndex == gapIndex)
            .Select(p => (int?)p.SortOrder)
            .MaxAsync(ct) ?? -1;

        var score = ResolveScore(body.PriorityScore, body.Priority, ActionPlanPriorities.DefaultScore);

        var row = new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = body.AnalysisPointId,
            GapIndex = gapIndex,
            ActionPlan = body.ActionPlan.Trim(),
            Status = ActionPlanStatuses.Normalize(body.Status),
            PriorityScore = score,
            Priority = ActionPlanPriorities.TierFromScore(score),
            TargetDate = ParseOptionalDueDate(body.TargetDate),
            Comment = Trimmed(body.Comment),
            SortOrder = maxOrder + 1,
            CreatedBy = profile!.Id,
            UpdatedBy = profile.Id,
        };
        if (row.Status == ActionPlanStatuses.Resolved)
        {
            row.ResolvedAt = DateTimeOffset.UtcNow;
            row.ResolvedBy = profile.Id;
        }

        db.NdAnalysisActionPlans.Add(row);
        // Child rows carry plain FK columns with no navigation, so EF cannot order the
        // inserts. Persist the plan first or the child inserts trip the FK constraint.
        await db.SaveChangesAsync(ct);

        await SyncAssigneesAsync(
            row, body.Assignees, body.ResponsibilityType, body.ResponsibilityDepartmentId, body.ResponsibilityUserId, ct);
        await db.SaveChangesAsync(ct);

        if (row.TargetDate.HasValue)
        {
            db.NdAnalysisActionPlanDateHistories.Add(new NdAnalysisActionPlanDateHistory
            {
                ActionPlanId = row.Id,
                PreviousTargetDate = null,
                NewTargetDate = row.TargetDate,
                Reason = "Initial target date",
                ChangedBy = profile.Id,
            });
            await db.SaveChangesAsync(ct);
        }

        await NdClauseStatusResolver.RecomputeAsync(db, [row.AnalysisPointId], ct);
        return Ok(new { success = true, data = await MapSinglePlanAsync(row, ct) });
    }

    // ---------------------------------------------------------------- seed

    public record SeedActionPlanItem(
        Guid AnalysisPointId,
        int GapIndex,
        string ActionPlan,
        string? Priority,
        string? TargetDate,
        string? OwnerLabel);

    public record SeedActionPlansRequest(List<SeedActionPlanItem>? Items);

    /// <summary>
    /// Writes a first-draft action for each gap the caller found that doesn't already
    /// have one. Skipping only the gaps already covered — rather than refusing outright
    /// once the run owns any action — means a gap added after the first seeding pass
    /// (e.g. gap-numbering fixes landing later) still gets a draft, while never
    /// duplicating or overwriting what a maker has since edited.
    /// </summary>
    [HttpPost("seed")]
    public async Task<IActionResult> Seed(Guid runId, [FromBody] SeedActionPlansRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        var accessError = await EnsureRunVisibleAsync(run, ct);
        if (accessError != null) return accessError;

        var items = body.Items ?? [];
        if (items.Count == 0)
            return Ok(new { success = true, data = new { seeded = 0, skipped = false } });

        var coveredGaps = (await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => new { p.AnalysisPointId, p.GapIndex })
            .ToListAsync(ct))
            .Select(p => (p.AnalysisPointId, p.GapIndex))
            .ToHashSet();
        items = items.Where(i => !coveredGaps.Contains((i.AnalysisPointId, Math.Max(0, i.GapIndex)))).ToList();
        if (items.Count == 0)
            return Ok(new { success = true, data = new { seeded = 0, skipped = true } });

        var validPointIds = await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var validPoints = validPointIds.ToHashSet();

        var departments = await db.NdDepartments.AsNoTracking()
            .Where(d => d.IsActive)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(ct);

        var rows = new List<NdAnalysisActionPlan>();
        var ownerLabels = new List<string?>();
        var orderByGap = new Dictionary<(Guid, int), int>();

        foreach (var item in items)
        {
            if (!validPoints.Contains(item.AnalysisPointId)) continue;
            if (string.IsNullOrWhiteSpace(item.ActionPlan)) continue;

            var gapIndex = Math.Max(0, item.GapIndex);
            var key = (item.AnalysisPointId, gapIndex);
            var order = orderByGap.TryGetValue(key, out var last) ? last + 1 : 0;
            orderByGap[key] = order;

            var score = ResolveScore(null, item.Priority, ActionPlanPriorities.DefaultScore);
            rows.Add(new NdAnalysisActionPlan
            {
                AnalysisRunId = runId,
                AnalysisPointId = item.AnalysisPointId,
                GapIndex = gapIndex,
                ActionPlan = item.ActionPlan.Trim(),
                Status = ActionPlanStatuses.Pending,
                PriorityScore = score,
                Priority = ActionPlanPriorities.TierFromScore(score),
                TargetDate = ParseOptionalDueDate(item.TargetDate),
                SortOrder = order,
                CreatedBy = profile!.Id,
                UpdatedBy = profile.Id,
            });
            ownerLabels.Add(item.OwnerLabel);
        }

        if (rows.Count == 0)
            return Ok(new { success = true, data = new { seeded = 0, skipped = false } });

        db.NdAnalysisActionPlans.AddRange(rows);
        await db.SaveChangesAsync(ct);

        // Owners come back as department names from the seed catalog; only attach the
        // ones that match a real department so the picker stays consistent.
        for (var i = 0; i < rows.Count; i++)
        {
            var label = ownerLabels[i]?.Trim();
            if (string.IsNullOrWhiteSpace(label)) continue;

            var dept = departments.FirstOrDefault(d =>
                string.Equals(d.Name, label, StringComparison.OrdinalIgnoreCase));
            if (dept == null) continue;

            rows[i].ResponsibilityType = "department";
            rows[i].ResponsibilityDepartmentId = dept.Id;
            rows[i].ResponsibilityLabel = dept.Name;
            db.NdAnalysisActionPlanAssignees.Add(new NdAnalysisActionPlanAssignee
            {
                ActionPlanId = rows[i].Id,
                AssigneeType = "department",
                DepartmentId = dept.Id,
                Label = dept.Name,
                SortOrder = 0,
            });
        }
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = new { seeded = rows.Count, skipped = false } });
    }

    // -------------------------------------------------------------- update

    [HttpPut("{planId:guid}")]
    public async Task<IActionResult> Update(
        Guid runId,
        Guid planId,
        [FromBody] UpdateActionPlanRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        var accessError = await EnsureRunVisibleAsync(run, ct);
        if (accessError != null) return accessError;

        var row = await db.NdAnalysisActionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (row == null) return NotFound(new { success = false, message = "Action plan not found." });

        if (!string.IsNullOrWhiteSpace(body.ActionPlan))
            row.ActionPlan = body.ActionPlan.Trim();

        var newStatus = ActionPlanStatuses.Normalize(body.Status ?? row.Status);
        var statusChanged = newStatus != row.Status;
        if (statusChanged)
        {
            db.NdAnalysisActionPlanStatusHistories.Add(new NdAnalysisActionPlanStatusHistory
            {
                ActionPlanId = row.Id,
                PreviousStatus = row.Status,
                NewStatus = newStatus,
                ChangedBy = profile!.Id,
            });
            row.Status = newStatus;
            row.ResolvedAt = newStatus == ActionPlanStatuses.Resolved ? DateTimeOffset.UtcNow : null;
            row.ResolvedBy = newStatus == ActionPlanStatuses.Resolved ? profile.Id : null;
        }

        if (body.GapIndex is int gi) row.GapIndex = Math.Max(0, gi);

        row.PriorityScore = ResolveScore(body.PriorityScore, body.Priority, row.PriorityScore);
        row.Priority = ActionPlanPriorities.TierFromScore(row.PriorityScore);
        row.Comment = Trimmed(body.Comment) ?? row.Comment;

        var newTarget = ParseOptionalDueDate(body.TargetDate);
        var targetChanged = body.TargetDate != null && newTarget != row.TargetDate;
        if (targetChanged)
        {
            db.NdAnalysisActionPlanDateHistories.Add(new NdAnalysisActionPlanDateHistory
            {
                ActionPlanId = row.Id,
                PreviousTargetDate = row.TargetDate,
                NewTargetDate = newTarget,
                Reason = Trimmed(body.TargetDateChangeReason),
                ChangedBy = profile!.Id,
            });
            row.TargetDate = newTarget;
        }

        if (body.Assignees != null
            || body.ResponsibilityType != null
            || body.ResponsibilityDepartmentId != null
            || body.ResponsibilityUserId != null)
        {
            await SyncAssigneesAsync(
                row,
                body.Assignees,
                body.ResponsibilityType ?? row.ResponsibilityType,
                body.ResponsibilityDepartmentId,
                body.ResponsibilityUserId,
                ct);
        }

        row.UpdatedBy = profile!.Id;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        if (statusChanged) await NdClauseStatusResolver.RecomputeAsync(db, [row.AnalysisPointId], ct);
        return Ok(new { success = true, data = await MapSinglePlanAsync(row, ct) });
    }

    [HttpPost("{planId:guid}/reorder")]
    public async Task<IActionResult> Reorder(Guid runId, Guid planId, [FromBody] ReorderRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var direction = body.Direction?.Trim().ToLowerInvariant() ?? "";
        if (direction is not ("up" or "down"))
            return BadRequest(new { success = false, message = "Direction must be up or down." });

        var row = await db.NdAnalysisActionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (row == null) return NotFound(new { success = false, message = "Action plan not found." });

        var siblings = await db.NdAnalysisActionPlans
            .Where(p => p.AnalysisPointId == row.AnalysisPointId && p.GapIndex == row.GapIndex)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);

        var index = siblings.FindIndex(p => p.Id == planId);
        var swapIndex = direction == "up" ? index - 1 : index + 1;
        if (index < 0 || swapIndex < 0 || swapIndex >= siblings.Count)
            return Ok(new { success = true });

        var other = siblings[swapIndex];
        (row.SortOrder, other.SortOrder) = (other.SortOrder, row.SortOrder);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{planId:guid}")]
    public async Task<IActionResult> Delete(Guid runId, Guid planId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var row = await db.NdAnalysisActionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (row == null) return NotFound(new { success = false, message = "Action plan not found." });

        var pointId = row.AnalysisPointId;
        db.NdAnalysisActionPlans.Remove(row);
        await db.SaveChangesAsync(ct);
        await NdClauseStatusResolver.RecomputeAsync(db, [pointId], ct);
        return Ok(new { success = true });
    }

    // ------------------------------------------------------ date history

    [HttpGet("{planId:guid}/date-history")]
    public async Task<IActionResult> DateHistory(Guid runId, Guid planId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var exists = await db.NdAnalysisActionPlans.AsNoTracking()
            .AnyAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (!exists) return NotFound(new { success = false, message = "Action plan not found." });

        var rows = await db.NdAnalysisActionPlanDateHistories.AsNoTracking()
            .Where(h => h.ActionPlanId == planId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.ChangedBy), ct);

        return Ok(new
        {
            success = true,
            data = rows.Select(h => new
            {
                id = h.Id,
                previousTargetDate = FormatDueDateResponse(h.PreviousTargetDate),
                newTargetDate = FormatDueDateResponse(h.NewTargetDate),
                reason = h.Reason,
                changedBy = h.ChangedBy,
                changedByName = ProfileName(names, h.ChangedBy),
                createdAt = h.CreatedAt,
            }),
        });
    }

    /// <summary>Who moved this action between pending and resolved, newest first.</summary>
    [HttpGet("{planId:guid}/status-history")]
    public async Task<IActionResult> StatusHistory(Guid runId, Guid planId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var plan = await db.NdAnalysisActionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (plan == null) return NotFound(new { success = false, message = "Action plan not found." });

        var rows = await db.NdAnalysisActionPlanStatusHistories.AsNoTracking()
            .Where(h => h.ActionPlanId == planId)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);

        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.ChangedBy).Append(plan.CreatedBy), ct);

        return Ok(new
        {
            success = true,
            data = rows.Select(h => new
            {
                id = h.Id,
                previousStatus = h.PreviousStatus,
                newStatus = h.NewStatus,
                changedBy = h.ChangedBy,
                changedByName = ProfileName(names, h.ChangedBy),
                createdAt = h.CreatedAt,
            }),
            createdByName = ProfileName(names, plan.CreatedBy),
            createdAt = plan.CreatedAt,
        });
    }

    // ----------------------------------------------------------- reviews

    [HttpPost("{planId:guid}/reviews")]
    public async Task<IActionResult> AddReview(Guid runId, Guid planId, [FromBody] SaveReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, ReviewRoles);
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(body.Comment))
            return BadRequest(new { success = false, message = "Review comment is required." });

        var plan = await db.NdAnalysisActionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.AnalysisRunId == runId, ct);
        if (plan == null) return NotFound(new { success = false, message = "Action plan not found." });

        var row = new NdAnalysisActionPlanReview
        {
            ActionPlanId = planId,
            AnalysisPointId = plan.AnalysisPointId,
            AnalysisRunId = runId,
            Comment = body.Comment.Trim(),
            ReviewerId = profile!.Id,
            ReviewerRole = profile.Role,
        };
        await ApplyReviewAssigneeAsync(row, body, ct);
        db.NdAnalysisActionPlanReviews.Add(row);
        await db.SaveChangesAsync(ct);

        var names = await LoadProfileNamesAsync(db, [row.ReviewerId], ct);
        return Ok(new { success = true, data = MapReview(row, names) });
    }

    [HttpPut("{planId:guid}/reviews/{reviewId:guid}")]
    public async Task<IActionResult> UpdateReview(
        Guid runId,
        Guid planId,
        Guid reviewId,
        [FromBody] SaveReviewRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, ReviewRoles);
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(body.Comment))
            return BadRequest(new { success = false, message = "Review comment is required." });

        var row = await db.NdAnalysisActionPlanReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.ActionPlanId == planId && r.AnalysisRunId == runId, ct);
        if (row == null) return NotFound(new { success = false, message = "Review not found." });

        if (row.ReviewerId != profile!.Id && !string.Equals(profile.Role, "super_admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { success = false, message = "You can only edit your own review." });

        row.Comment = body.Comment.Trim();
        await ApplyReviewAssigneeAsync(row, body, ct);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var names = await LoadProfileNamesAsync(db, [row.ReviewerId], ct);
        return Ok(new { success = true, data = MapReview(row, names) });
    }

    [HttpDelete("{planId:guid}/reviews/{reviewId:guid}")]
    public async Task<IActionResult> DeleteReview(Guid runId, Guid planId, Guid reviewId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, ReviewRoles);
        if (error != null) return error;

        var row = await db.NdAnalysisActionPlanReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.ActionPlanId == planId && r.AnalysisRunId == runId, ct);
        if (row == null) return NotFound(new { success = false, message = "Review not found." });

        if (row.ReviewerId != profile!.Id && !string.Equals(profile.Role, "super_admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { success = false, message = "You can only delete your own review." });

        db.NdAnalysisActionPlanReviews.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    // ----------------------------------------------------------- helpers

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The slider sends a 0–100 score. Older clients (and rows written before the slider
    /// existed) only carry a tier, so fall back to the tier midpoint.
    /// </summary>
    private static int ResolveScore(int? score, string? tier, int fallback)
    {
        if (score is int s) return ActionPlanPriorities.ClampScore(s);
        if (!string.IsNullOrWhiteSpace(tier)) return ActionPlanPriorities.ScoreFromTier(tier);
        return ActionPlanPriorities.ClampScore(fallback);
    }

    /// <summary>Rows stored before <c>priority_score</c> existed default to 50 — trust their tier instead.</summary>
    private static int EffectiveScore(NdAnalysisActionPlan p) =>
        ActionPlanPriorities.TierFromScore(p.PriorityScore) == ActionPlanPriorities.Normalize(p.Priority)
            ? ActionPlanPriorities.ClampScore(p.PriorityScore)
            : ActionPlanPriorities.ScoreFromTier(p.Priority);

    private async Task<IActionResult?> EnsureRunVisibleAsync(Data.NewDashboard.Entities.NdAnalysisRun run, CancellationToken ct)
    {
        var user = ValidateJwt(jwt);
        if (user == null) return Unauthorized(new { success = false, message = "Unauthorized" });

        var ctx = await demoDirectory.ResolveContextAsync(user, ct);
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, ctx))
            return NotFound(new { success = false, message = "Run not found." });

        return null;
    }

    /// <summary>
    /// Rewrites the owner rows for a plan. Older clients send only the single
    /// <c>responsibility*</c> fields, so those are folded into a one-entry list. The
    /// plan's own responsibility columns keep mirroring the first owner because exports
    /// and the compact gap views still read them.
    /// </summary>
    private async Task SyncAssigneesAsync(
        NdAnalysisActionPlan row,
        List<AssigneeInput>? inputs,
        string? legacyType,
        Guid? legacyDepartmentId,
        Guid? legacyUserId,
        CancellationToken ct)
    {
        var list = inputs ?? [];
        if (list.Count == 0 && (legacyDepartmentId.HasValue || legacyUserId.HasValue))
            list = [new AssigneeInput(legacyType, legacyDepartmentId, legacyUserId, null)];

        var resolved = new List<NdAnalysisActionPlanAssignee>();
        foreach (var input in list)
        {
            var type = ActionPlanResponsibilityTypes.Normalize(input.Type);
            var label = Trimmed(input.Label);

            if (type == ActionPlanResponsibilityTypes.User && input.UserId is Guid uid)
            {
                label ??= await db.NdProfiles.AsNoTracking()
                    .Where(p => p.Id == uid).Select(p => p.FullName).FirstOrDefaultAsync(ct);
                resolved.Add(new NdAnalysisActionPlanAssignee
                {
                    ActionPlanId = row.Id,
                    AssigneeType = type,
                    UserId = uid,
                    Label = label ?? "",
                });
            }
            else if (type == ActionPlanResponsibilityTypes.Department && input.DepartmentId is Guid did)
            {
                label ??= await db.NdDepartments.AsNoTracking()
                    .Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync(ct);
                resolved.Add(new NdAnalysisActionPlanAssignee
                {
                    ActionPlanId = row.Id,
                    AssigneeType = type,
                    DepartmentId = did,
                    Label = label ?? "",
                });
            }
            else if (!string.IsNullOrWhiteSpace(label))
            {
                resolved.Add(new NdAnalysisActionPlanAssignee
                {
                    ActionPlanId = row.Id,
                    AssigneeType = type,
                    Label = label,
                });
            }
        }

        for (var i = 0; i < resolved.Count; i++) resolved[i].SortOrder = i;

        var existing = await db.NdAnalysisActionPlanAssignees
            .Where(a => a.ActionPlanId == row.Id)
            .ToListAsync(ct);
        if (existing.Count > 0) db.NdAnalysisActionPlanAssignees.RemoveRange(existing);
        if (resolved.Count > 0) db.NdAnalysisActionPlanAssignees.AddRange(resolved);

        var primary = resolved.FirstOrDefault();
        row.ResponsibilityType = primary?.AssigneeType ?? ActionPlanResponsibilityTypes.Department;
        row.ResponsibilityDepartmentId = primary?.DepartmentId;
        row.ResponsibilityUserId = primary?.UserId;
        row.ResponsibilityLabel = resolved.Count switch
        {
            0 => null,
            1 => primary!.Label,
            _ => $"{primary!.Label} +{resolved.Count - 1}",
        };
    }

    private async Task<Dictionary<Guid, List<NdAnalysisActionPlanAssignee>>> LoadAssigneesAsync(
        IReadOnlyCollection<Guid> planIds,
        CancellationToken ct)
    {
        if (planIds.Count == 0) return [];
        var rows = await db.NdAnalysisActionPlanAssignees.AsNoTracking()
            .Where(a => planIds.Contains(a.ActionPlanId))
            .OrderBy(a => a.SortOrder)
            .ToListAsync(ct);
        return rows.GroupBy(a => a.ActionPlanId).ToDictionary(g => g.Key, g => g.ToList());
    }

    private async Task<Dictionary<Guid, string>> LoadDepartmentNamesAsync(
        List<NdAnalysisActionPlan> plans,
        CancellationToken ct)
    {
        var ids = plans.Where(p => p.ResponsibilityDepartmentId.HasValue)
            .Select(p => p.ResponsibilityDepartmentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.NdDepartments.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
    }

    private async Task<object> MapSinglePlanAsync(NdAnalysisActionPlan row, CancellationToken ct)
    {
        var names = await LoadProfileNamesAsync(db, [row.CreatedBy, row.UpdatedBy, row.ResolvedBy, row.ResponsibilityUserId], ct);
        var departmentNames = await LoadDepartmentNamesAsync([row], ct);
        var reviews = await db.NdAnalysisActionPlanReviews.AsNoTracking()
            .Where(r => r.ActionPlanId == row.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var reviewerNames = await LoadProfileNamesAsync(db, reviews.Select(r => r.ReviewerId), ct);
        var assignees = await LoadAssigneesAsync([row.Id], ct);
        return MapPlan(
            row,
            names,
            departmentNames,
            reviews.Select(r => MapReview(r, reviewerNames)).ToList(),
            assignees.TryGetValue(row.Id, out var asg) ? asg : []);
    }

    public static object MapAssignee(NdAnalysisActionPlanAssignee a) => new
    {
        id = a.Id,
        type = a.AssigneeType,
        departmentId = a.DepartmentId,
        userId = a.UserId,
        label = a.Label,
    };

    private static object MapPlan(
        NdAnalysisActionPlan p,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> departmentNames,
        List<object> reviews,
        List<NdAnalysisActionPlanAssignee> assignees) => new
    {
        id = p.Id,
        analysisRunId = p.AnalysisRunId,
        analysisPointId = p.AnalysisPointId,
        gapIndex = p.GapIndex,
        actionPlan = p.ActionPlan,
        status = p.Status,
        priority = p.Priority,
        priorityScore = EffectiveScore(p),
        targetDate = FormatDueDateResponse(p.TargetDate),
        responsibilityType = p.ResponsibilityType,
        responsibilityDepartmentId = p.ResponsibilityDepartmentId,
        responsibilityUserId = p.ResponsibilityUserId,
        responsibilityName = ResolveResponsibilityName(p, names, departmentNames),
        assignees = assignees.Select(MapAssignee),
        comment = p.Comment,
        sortOrder = p.SortOrder,
        resolvedAt = FormatDueDateResponse(p.ResolvedAt),
        resolvedBy = p.ResolvedBy,
        resolvedByName = ProfileName(names, p.ResolvedBy),
        createdBy = p.CreatedBy,
        createdByName = ProfileName(names, p.CreatedBy),
        updatedByName = ProfileName(names, p.UpdatedBy),
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
        reviews,
        reviewCount = reviews.Count,
    };

    private static string? ResolveResponsibilityName(
        NdAnalysisActionPlan p,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlyDictionary<Guid, string> departmentNames)
    {
        if (p.ResponsibilityType == ActionPlanResponsibilityTypes.User)
            return ProfileName(names, p.ResponsibilityUserId) ?? p.ResponsibilityLabel;
        if (p.ResponsibilityDepartmentId is Guid did && departmentNames.TryGetValue(did, out var dept))
            return dept;
        return p.ResponsibilityLabel;
    }

    private static object MapReview(NdAnalysisActionPlanReview r, IReadOnlyDictionary<Guid, string> names) => new
    {
        id = r.Id,
        actionPlanId = r.ActionPlanId,
        analysisPointId = r.AnalysisPointId,
        comment = r.Comment,
        reviewerId = r.ReviewerId,
        reviewerName = ProfileName(names, r.ReviewerId),
        reviewerRole = r.ReviewerRole,
        assigneeType = r.AssigneeType,
        assigneeDepartmentId = r.AssigneeDepartmentId,
        assigneeUserId = r.AssigneeUserId,
        assigneeLabel = r.AssigneeLabel,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
    };

    /// <summary>
    /// Copies the routing a caller chose onto the review, resolving the display label
    /// from the real department or profile so the two sides always read the same name.
    /// </summary>
    private async Task ApplyReviewAssigneeAsync(
        NdAnalysisActionPlanReview row,
        SaveReviewRequest body,
        CancellationToken ct)
    {
        var type = (body.AssigneeType ?? "").Trim().ToLowerInvariant();
        if (type is not ("department" or "user"))
        {
            row.AssigneeType = null;
            row.AssigneeDepartmentId = null;
            row.AssigneeUserId = null;
            row.AssigneeLabel = null;
            return;
        }

        string? label = body.AssigneeLabel?.Trim();
        if (type == "department" && body.AssigneeDepartmentId is Guid deptId)
        {
            label = await db.NdDepartments.AsNoTracking()
                .Where(d => d.Id == deptId).Select(d => d.Name).FirstOrDefaultAsync(ct) ?? label;
            row.AssigneeDepartmentId = deptId;
            row.AssigneeUserId = null;
        }
        else if (type == "user" && body.AssigneeUserId is Guid userId)
        {
            label = await db.NdProfiles.AsNoTracking()
                .Where(p => p.Id == userId).Select(p => p.FullName).FirstOrDefaultAsync(ct) ?? label;
            row.AssigneeUserId = userId;
            row.AssigneeDepartmentId = null;
        }
        else
        {
            // A free-typed name with no matching record still records the intent.
            row.AssigneeDepartmentId = null;
            row.AssigneeUserId = null;
        }

        row.AssigneeType = type;
        row.AssigneeLabel = string.IsNullOrWhiteSpace(label) ? null : label;
    }
}
