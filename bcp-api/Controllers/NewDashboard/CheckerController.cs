using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/checker")]
public class CheckerController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    public record ReviewRequest(string? OverallComment, List<PointCommentInput>? PointComments);

    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "submitted_for_review")
            .OrderByDescending(r => r.SubmittedToCheckerAt)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await EnrichRunsAsync(runs, ct) });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        var runs = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "checker_approved" || r.Status == "pulled_back" || r.Status == "reviewer_approved")
            .OrderByDescending(r => r.CheckerReviewedAt ?? r.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await EnrichRunsAsync(runs, ct) });
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
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "checker");
        if (error != null) return error;

        if (string.IsNullOrWhiteSpace(body.OverallComment)
            && (body.PointComments == null || !body.PointComments.Any(c => !string.IsNullOrWhiteSpace(c.Comment))))
            return BadRequest(new { success = false, message = "Comment required to pull back." });

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
            OverallComment = body.OverallComment,
        };
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);

        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }

    private async Task<List<object>> EnrichRunsAsync(List<NdAnalysisRun> runs, CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var run in runs)
        {
            var maker = run.CreatedBy.HasValue
                ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
                : null;
            var points = await db.NdAnalysisPoints.AsNoTracking()
                .Where(p => p.AnalysisRunId == run.Id)
                .ToListAsync(ct);

            result.Add(new
            {
                id = run.Id,
                name = run.Name,
                makerName = maker?.FullName,
                departmentId = run.DepartmentId,
                submittedAt = run.SubmittedToCheckerAt,
                status = run.Status,
                compliant = points.Count(p => p.FinalStatus == "compliant"),
                partial = points.Count(p => p.FinalStatus == "partial_compliant"),
                nonCompliant = points.Count(p => p.FinalStatus == "non_compliant"),
            });
        }
        return result;
    }
}
