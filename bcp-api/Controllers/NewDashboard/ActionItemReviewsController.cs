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

        if (body.ActionIndex < 1)
            return BadRequest(new { success = false, message = "Invalid action index." });

        var status = body.Status?.Trim().ToLowerInvariant() ?? "";
        if (!ValidActionItemReviewStatuses.Contains(status))
            return BadRequest(new { success = false, message = "Invalid review status." });

        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(body.DueDate)
            && DateOnly.TryParse(body.DueDate.Trim(), out var parsedDue))
        {
            dueDate = parsedDue;
        }

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
        };
        db.NdActionPlanItemReviews.Add(row);
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                row.Id,
                analysisPointId = row.AnalysisPointId,
                actionIndex = row.ActionIndex,
                status = row.Status,
                comment = row.Comment,
                responsibility = row.Responsibility,
                dueDate = row.DueDate.HasValue ? row.DueDate.Value.ToString("yyyy-MM-dd") : null,
                priority = row.Priority,
                row.CreatedAt,
            },
        });
    }

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
        var t = raw?.Trim().ToLowerInvariant() ?? "";
        return t is "medium" or "higher" ? t : null;
    }
}
