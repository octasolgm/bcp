using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/checker")]
public class CheckerController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker", "reviewer");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "submitted_for_review")
            .OrderByDescending(r => r.SubmittedToCheckerAt)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await NdRunEnrichmentHelper.EnrichRunsAsync(db, runs, ct) });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "checker_approved" || r.Status == "reviewer_approved")
            .OrderByDescending(r => r.CheckerReviewedAt ?? r.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await NdRunEnrichmentHelper.EnrichRunsAsync(db, runs, ct) });
    }

    [HttpPost("review/{runId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "submitted_for_review")
            return BadRequest(new { success = false, message = "Run is not in review queue." });

        var from = run.Status;
        run.Status = "checker_approved";
        run.CheckerReviewedAt = DateTimeOffset.UtcNow;
        run.SubmittedToReviewerAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var review = new NdAnalysisReview
        {
            AnalysisRunId = runId,
            ReviewerId = profile!.Id,
            ReviewerRole = "checker",
            Action = "approved",
        };
        ApplyReviewMetadata(review, body);
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);

        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await SaveActionItemReviewsAsync(db, review.Id, body.ActionItemReviews, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }

    [HttpPost("review/{runId:guid}/pull-back")]
    public async Task<IActionResult> PullBack(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "submitted_for_review")
            return BadRequest(new { success = false, message = "Run is not in review queue." });

        var from = run.Status;
        run.Status = "pulled_back";
        run.CheckerReviewedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var review = new NdAnalysisReview
        {
            AnalysisRunId = runId,
            ReviewerId = profile!.Id,
            ReviewerRole = "checker",
            Action = "pulled_back",
        };
        ApplyReviewMetadata(review, body);
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);

        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await SaveActionItemReviewsAsync(db, review.Id, body.ActionItemReviews, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }
}
