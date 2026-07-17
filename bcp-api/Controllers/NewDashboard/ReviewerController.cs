using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/reviewer")]
public class ReviewerController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    public record ReviewRequest(string? OverallComment, List<PointCommentInput>? PointComments);

    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "checker_approved")
            .OrderByDescending(r => r.SubmittedToReviewerAt)
            .ToListAsync(ct);

        return Ok(new { success = true, data = runs.Select(MapQueueItem) });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "reviewer_approved")
            .OrderByDescending(r => r.ReviewerFinalizedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new { success = true, data = runs.Select(MapQueueItem) });
    }

    [HttpPost("review/{runId:guid}/finalize")]
    public async Task<IActionResult> Finalize(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "checker_approved")
            return BadRequest(new { success = false, message = "Run is not ready for final review." });

        var from = run.Status;
        run.Status = "reviewer_approved";
        run.ReviewerFinalizedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var review = new NdAnalysisReview
        {
            AnalysisRunId = runId,
            ReviewerId = profile!.Id,
            ReviewerRole = "reviewer",
            Action = "finalized",
            OverallComment = body.OverallComment,
        };
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }

    [HttpPost("review/{runId:guid}/pull-back")]
    public async Task<IActionResult> PullBack(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(body.OverallComment))
            return BadRequest(new { success = false, message = "Comment required." });

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "checker_approved")
            return BadRequest(new { success = false, message = "Run is not in reviewer queue." });

        var from = run.Status;
        run.Status = "submitted_for_review";
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var review = new NdAnalysisReview
        {
            AnalysisRunId = runId,
            ReviewerId = profile!.Id,
            ReviewerRole = "reviewer",
            Action = "pulled_back",
            OverallComment = body.OverallComment,
        };
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }

    private static object MapQueueItem(NdAnalysisRun run) => new
    {
        id = run.Id,
        name = run.Name,
        departmentId = run.DepartmentId,
        status = run.Status,
        submittedToReviewerAt = run.SubmittedToReviewerAt,
        createdAt = run.CreatedAt,
    };
}
