using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/reviewer")]
public class ReviewerController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory,
    NdCorrectedDocumentService correctedDocuments,
    NdActionPlanEmbedResolver embedResolver) : NdControllerBase
{
    [HttpGet("queue")]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var runsQ = db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "checker_approved");
        if (demoCtx.Enabled)
            runsQ = NdDemoDataFilters.ApplyToAnalysisRuns(runsQ, demoCtx);
        var runs = await runsQ
            .OrderByDescending(r => r.SubmittedToReviewerAt)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await NdRunEnrichmentHelper.EnrichRunsAsync(db, runs, ct) });
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var runsQ = db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "reviewer_approved");
        if (demoCtx.Enabled)
            runsQ = NdDemoDataFilters.ApplyToAnalysisRuns(runsQ, demoCtx);
        var runs = await runsQ
            .OrderByDescending(r => r.ReviewerFinalizedAt)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new { success = true, data = await NdRunEnrichmentHelper.EnrichRunsAsync(db, runs, ct) });
    }

    [HttpPost("review/{runId:guid}/finalize")]
    public async Task<IActionResult> Finalize(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireRoleAtLeastAsync(db, jwt, ct, "reviewer");
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
        };
        ApplyReviewMetadata(review, body);
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await SaveActionItemReviewsAsync(db, review.Id, body.ActionItemReviews, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);

        var corrected = await correctedDocuments.GenerateForRunAsync(runId, profile.Id, ct);
        var embed = await embedResolver.DescribeForRunAsync(runId, ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                correctedDocuments = corrected.Select(c => new
                {
                    documentId = c.DocumentId,
                    title = c.Title,
                    version = c.VersionNumber,
                }),
                embed = MapEmbedDiagnostics(embed),
            },
        });
    }

    /// <summary>
    /// Re-runs just the corrected-document generation for a run that's already finalized —
    /// for re-testing the embed step (e.g. after a fix to the embedder) without re-running the
    /// AI analysis itself. Always adds a new version; never touches or deletes an existing one.
    /// </summary>
    [HttpPost("review/{runId:guid}/regenerate-corrected-documents")]
    public async Task<IActionResult> RegenerateCorrectedDocuments(Guid runId, CancellationToken ct)
    {
        var (profile, error) = await RequireRoleAtLeastAsync(db, jwt, ct, "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "reviewer_approved")
            return BadRequest(new { success = false, message = "Run is not finalized yet." });

        var corrected = await correctedDocuments.GenerateForRunAsync(runId, profile!.Id, ct, force: true);
        var embed = await embedResolver.DescribeForRunAsync(runId, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                correctedDocuments = corrected.Select(c => new
                {
                    documentId = c.DocumentId,
                    title = c.Title,
                    version = c.VersionNumber,
                }),
                embed = MapEmbedDiagnostics(embed),
            },
        });
    }

    private static object MapEmbedDiagnostics(NdActionPlanEmbedResolver.EmbedDiagnostics embed) => new
    {
        resolvedActionPlans = embed.ResolvedActionPlanCount,
        embedTargets = embed.EmbedTargetCount,
        documents = embed.Documents.Select(d => new
        {
            documentId = d.DocumentId,
            title = d.Title,
            targetCount = d.TargetCount,
        }),
    };

    [HttpPost("review/{runId:guid}/pull-back")]
    public async Task<IActionResult> PullBack(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireRoleAtLeastAsync(db, jwt, ct, "reviewer");
        if (error != null) return error;

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
        };
        ApplyReviewMetadata(review, body);
        db.NdAnalysisReviews.Add(review);
        await db.SaveChangesAsync(ct);
        await SavePointCommentsAsync(db, review.Id, body.PointComments, profile.Id, ct);
        await SaveActionItemReviewsAsync(db, review.Id, body.ActionItemReviews, profile.Id, ct);
        await RecordStatusChangeAsync(db, runId, from, run.Status, profile.Id, body.OverallComment, ct);
        return Ok(new { success = true });
    }

    [HttpPost("review/{runId:guid}/pull-back-to-maker")]
    public async Task<IActionResult> PullBackToMaker(Guid runId, [FromBody] ReviewRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireRoleAtLeastAsync(db, jwt, ct, "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (run.Status != "checker_approved")
            return BadRequest(new { success = false, message = "Run is not in reviewer queue." });

        var from = run.Status;
        run.Status = "pulled_back";
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var review = new NdAnalysisReview
        {
            AnalysisRunId = runId,
            ReviewerId = profile!.Id,
            ReviewerRole = "reviewer",
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
