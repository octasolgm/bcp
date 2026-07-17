using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/results")]
public class ResultsController(
    AppDbContext db,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    public record UpdateActionPlanRequest(string Content, int? RevertToVersion);

    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> Get(Guid runId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();

        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var reviews = await db.NdAnalysisReviews.AsNoTracking()
            .Where(r => r.AnalysisRunId == runId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);

        var pointIds = run.Points.Select(p => p.Id).ToList();
        var comments = await db.NdAnalysisPointComments.AsNoTracking()
            .Where(c => pointIds.Contains(c.AnalysisPointId))
            .ToListAsync(ct);

        var history = await db.NdAnalysisStatusHistories.AsNoTracking()
            .Where(h => h.AnalysisRunId == runId)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                run = new
                {
                    run.Id,
                    run.Name,
                    run.Status,
                    run.TotalPointsCount,
                    run.ProcessedPointsCount,
                    run.DualVerifyFailedCount,
                    createdByName = creator?.FullName,
                    run.CreatedAt,
                },
                points = run.Points,
                reviews,
                comments,
                statusHistory = history,
            },
        });
    }

    [HttpPut("{runId:guid}/action-plan/{pointId:guid}")]
    public async Task<IActionResult> UpdateActionPlan(
        Guid runId,
        Guid pointId,
        [FromBody] UpdateActionPlanRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);
        if (run.Status is "submitted_for_review" or "checker_approved" or "reviewer_approved")
            return BadRequest(new { success = false, message = "Cannot edit while in review." });

        var point = await db.NdAnalysisPoints.FirstOrDefaultAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
        if (point == null) return NotFound();

        var prev = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == pointId && h.IsCurrent)
            .ToListAsync(ct);
        foreach (var h in prev) h.IsCurrent = false;

        var maxVersion = await db.NdActionPlanHistories
            .Where(h => h.AnalysisPointId == pointId)
            .MaxAsync(h => (int?)h.VersionNumber, ct) ?? 0;

        string changeType;
        int? revertedTo = null;
        if (body.RevertToVersion.HasValue)
        {
            changeType = "maker_reverted_to_version";
            revertedTo = body.RevertToVersion;
        }
        else
        {
            changeType = "maker_edit";
        }

        point.FinalActionPlan = body.Content;
        point.UpdatedAt = DateTimeOffset.UtcNow;

        db.NdActionPlanHistories.Add(new NdActionPlanHistory
        {
            AnalysisPointId = pointId,
            ActionPlanContent = body.Content,
            VersionNumber = maxVersion + 1,
            ChangeType = changeType,
            RevertedToVersion = revertedTo,
            ChangedBy = profile.Id,
            IsCurrent = true,
        });

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpGet("{runId:guid}/action-plan-history/{pointId:guid}")]
    public async Task<IActionResult> ActionPlanHistory(Guid runId, Guid pointId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var history = await db.NdActionPlanHistories.AsNoTracking()
            .Where(h => h.AnalysisPointId == pointId)
            .OrderByDescending(h => h.VersionNumber)
            .ToListAsync(ct);

        var changerIds = history.Where(h => h.ChangedBy.HasValue).Select(h => h.ChangedBy!.Value).Distinct().ToList();
        var changers = await db.NdProfiles.AsNoTracking()
            .Where(p => changerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.FullName, ct);

        return Ok(new
        {
            success = true,
            data = history.Select(h => new
            {
                h.Id,
                h.VersionNumber,
                h.ActionPlanContent,
                h.ChangeType,
                h.RevertedToVersion,
                h.IsCurrent,
                h.CreatedAt,
                changedByName = h.ChangedBy.HasValue && changers.TryGetValue(h.ChangedBy.Value, out var n) ? n : null,
            }),
        });
    }
}
