using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/results/{runId:guid}/action-item-reviews")]
public class ActionItemReviewsController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    public record SaveActionItemReviewRequest(
        Guid AnalysisPointId,
        int ActionIndex,
        string Status,
        string? Comment,
        string? Responsibility,
        string? DueDate,
        string? Priority);

    public record UpdateActionItemReviewRequest(
        string Status,
        string? Comment,
        string? Responsibility,
        string? DueDate,
        string? Priority);

    public record ReorderActionItemReviewRequest(string Direction);

    [HttpPost]
    public async Task<IActionResult> Save(
        Guid runId,
        [FromBody] SaveActionItemReviewRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        if (!CanReviewRun(profile!.Role, run.Status))
            return BadRequest(new { success = false, message = "Run is not in an active review stage." });

        var point = await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == body.AnalysisPointId && p.AnalysisRunId == runId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Analysis point not found." });

        if (body.ActionIndex < 0)
            return BadRequest(new { success = false, message = "Invalid action index." });

        var status = body.Status?.Trim().ToLowerInvariant() ?? "";
        if (!ValidActionItemReviewStatuses.Contains(status))
            return BadRequest(new { success = false, message = "Invalid review status." });

        DateTimeOffset? dueDate = ParseOptionalDueDate(body.DueDate);

        var maxOrder = await db.NdActionPlanItemReviews
            .Where(r => r.AnalysisPointId == body.AnalysisPointId && r.ActionIndex == body.ActionIndex)
            .Select(r => (int?)r.SortOrder)
            .MaxAsync(ct) ?? -1;

        var row = new NdActionPlanItemReview
        {
            AnalysisPointId = body.AnalysisPointId,
            AnalysisReviewId = null,
            ActionIndex = body.ActionIndex,
            Status = status,
            Comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim(),
            Responsibility = string.IsNullOrWhiteSpace(body.Responsibility) ? null : body.Responsibility.Trim(),
            DueDate = dueDate,
            Priority = NormalizeReviewPriority(body.Priority),
            ReviewedBy = profile.Id,
            SortOrder = maxOrder + 1,
        };
        db.NdActionPlanItemReviews.Add(row);
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = MapReviewRow(row) });
    }

    [HttpPut("{reviewId:guid}")]
    public async Task<IActionResult> Update(
        Guid runId,
        Guid reviewId,
        [FromBody] UpdateActionItemReviewRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        if (!CanReviewRun(profile!.Role, run.Status))
            return BadRequest(new { success = false, message = "Run is not in an active review stage." });

        var row = await db.NdActionPlanItemReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (row == null) return NotFound(new { success = false, message = "Review not found." });

        var point = await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == row.AnalysisPointId && p.AnalysisRunId == runId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Review not found for this run." });

        var status = body.Status?.Trim().ToLowerInvariant() ?? "";
        if (!ValidActionItemReviewStatuses.Contains(status))
            return BadRequest(new { success = false, message = "Invalid review status." });

        row.Status = status;
        row.Comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        row.Responsibility = string.IsNullOrWhiteSpace(body.Responsibility) ? null : body.Responsibility.Trim();
        row.DueDate = ParseOptionalDueDate(body.DueDate);
        row.Priority = NormalizeReviewPriority(body.Priority);
        row.ReviewedBy = profile.Id;

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = MapReviewRow(row) });
    }

    [HttpPost("{reviewId:guid}/reorder")]
    public async Task<IActionResult> Reorder(
        Guid runId,
        Guid reviewId,
        [FromBody] ReorderActionItemReviewRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        if (!CanReviewRun(profile!.Role, run.Status))
            return BadRequest(new { success = false, message = "Run is not in an active review stage." });

        var row = await db.NdActionPlanItemReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (row == null) return NotFound(new { success = false, message = "Review not found." });

        var point = await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == row.AnalysisPointId && p.AnalysisRunId == runId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Review not found for this run." });

        var direction = body.Direction?.Trim().ToLowerInvariant() ?? "";
        if (direction is not ("up" or "down"))
            return BadRequest(new { success = false, message = "Direction must be up or down." });

        var siblings = await db.NdActionPlanItemReviews
            .Where(r => r.AnalysisPointId == row.AnalysisPointId && r.ActionIndex == row.ActionIndex)
            .OrderByDescending(r => r.SortOrder)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        var index = siblings.FindIndex(r => r.Id == reviewId);
        if (index < 0) return NotFound(new { success = false, message = "Review not found." });

        var swapIndex = direction == "up" ? index - 1 : index + 1;
        if (swapIndex < 0 || swapIndex >= siblings.Count)
            return Ok(new { success = true });

        var other = siblings[swapIndex];
        (row.SortOrder, other.SortOrder) = (other.SortOrder, row.SortOrder);
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true });
    }

    [HttpDelete("{reviewId:guid}")]
    public async Task<IActionResult> Delete(Guid runId, Guid reviewId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        if (!CanReviewRun(profile!.Role, run.Status))
            return BadRequest(new { success = false, message = "Run is not in an active review stage." });

        var row = await db.NdActionPlanItemReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct);
        if (row == null) return NotFound(new { success = false, message = "Review not found." });

        var point = await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == row.AnalysisPointId && p.AnalysisRunId == runId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Review not found for this run." });

        db.NdActionPlanItemReviews.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    private static object MapReviewRow(NdActionPlanItemReview row) => new
    {
        row.Id,
        analysisPointId = row.AnalysisPointId,
        analysisReviewId = row.AnalysisReviewId,
        actionIndex = row.ActionIndex,
        status = row.Status,
        comment = row.Comment,
        responsibility = row.Responsibility,
        dueDate = FormatDueDateResponse(row.DueDate),
        priority = row.Priority,
        sortOrder = row.SortOrder,
        row.CreatedAt,
    };

    private static bool CanReviewRun(string role, string status) =>
        role switch
        {
            "super_admin" => status is "submitted_for_review" or "checker_approved" or "pulled_back",
            "checker" => status is "submitted_for_review" or "pulled_back",
            "reviewer" => status is "checker_approved" or "pulled_back",
            _ => false,
        };

    private static readonly HashSet<string> ValidActionItemReviewStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "approve", "need_modify",
    };

    private static string? NormalizeReviewPriority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (int.TryParse(t, out var score))
            return Math.Clamp(score, 0, 100).ToString();
        var lower = t.ToLowerInvariant();
        return lower switch
        {
            "low" => "25",
            "medium" => "50",
            "higher" or "high" or "critical" => "85",
            _ => null,
        };
    }
}
