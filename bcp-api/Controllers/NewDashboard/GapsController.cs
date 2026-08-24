using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// State for the gaps raised against a clause. The gap text stays in the clause's CAP
/// blob; these rows carry risk and resolve, so that closing one gap leaves the other
/// gaps on the same clause untouched.
/// </summary>
[ApiController]
[Route("nd/results/{runId:guid}/gaps")]
public class GapsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory,
    NdDashboardCacheService dashboardCache) : NdControllerBase
{
    private static readonly string[] AllRoles = ["super_admin", "maker", "checker", "reviewer"];

    /// <summary>One gap the report is showing, with the risk the AI gave it.</summary>
    public record GapRosterItem(Guid AnalysisPointId, int GapIndex, string? Risk);

    public record SyncGapsRequest(List<GapRosterItem>? Items);

    public record UpdateGapRequest(string? Risk, int? RiskScore, string? Status);

    [HttpGet]
    public async Task<IActionResult> List(Guid runId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var accessError = await EnsureRunVisibleAsync(runId, ct);
        if (accessError != null) return accessError;

        var rows = await db.NdAnalysisGaps.AsNoTracking()
            .Where(g => g.AnalysisRunId == runId)
            .OrderBy(g => g.GapIndex)
            .ToListAsync(ct);

        var names = await LoadProfileNamesAsync(db, rows.Select(r => r.ResolvedBy), ct);
        return Ok(new { success = true, data = rows.Select(r => Map(r, names)) });
    }

    /// <summary>
    /// Registers the gaps a report is showing. Existing rows keep whatever risk and
    /// status they already carry — this only fills in gaps seen for the first time.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(Guid runId, [FromBody] SyncGapsRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var accessError = await EnsureRunVisibleAsync(runId, ct);
        if (accessError != null) return accessError;

        var items = body.Items ?? [];
        if (items.Count == 0) return Ok(new { success = true, data = new { added = 0 } });

        var validPoints = (await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => p.Id)
            .ToListAsync(ct)).ToHashSet();

        var added = 0;
        foreach (var group in items.Where(i => validPoints.Contains(i.AnalysisPointId)).GroupBy(i => i.AnalysisPointId))
        {
            var risks = group
                .Where(i => i.GapIndex > 0)
                .GroupBy(i => i.GapIndex)
                .ToDictionary(g => g.Key, g => g.First().Risk ?? ActionPlanPriorities.Medium);
            added += await NdGapStatusResolver.EnsureRosterAsync(db, runId, group.Key, risks, ct);
        }

        // A first sync can reveal gaps whose actions are already resolved.
        await NdGapStatusResolver.RecomputeAsync(db, items.Select(i => i.AnalysisPointId), ct);
        if (added > 0) dashboardCache.Invalidate();
        return Ok(new { success = true, data = new { added } });
    }

    [HttpPut("{pointId:guid}/{gapIndex:int}")]
    public async Task<IActionResult> Update(
        Guid runId,
        Guid pointId,
        int gapIndex,
        [FromBody] UpdateGapRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, AllRoles);
        if (error != null) return error;

        var accessError = await EnsureRunVisibleAsync(runId, ct);
        if (accessError != null) return accessError;

        var row = await db.NdAnalysisGaps
            .FirstOrDefaultAsync(g => g.AnalysisRunId == runId && g.AnalysisPointId == pointId && g.GapIndex == gapIndex, ct);
        if (row == null)
        {
            var pointExists = await db.NdAnalysisPoints.AsNoTracking()
                .AnyAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
            if (!pointExists) return NotFound(new { success = false, message = "Gap point not found for this run." });

            row = new NdAnalysisGap { AnalysisRunId = runId, AnalysisPointId = pointId, GapIndex = gapIndex };
            db.NdAnalysisGaps.Add(row);
        }

        if (body.RiskScore is int score)
        {
            row.RiskScore = ActionPlanPriorities.ClampScore(score);
            row.Risk = ActionPlanPriorities.TierFromScore(row.RiskScore);
        }
        else if (!string.IsNullOrWhiteSpace(body.Risk))
        {
            row.Risk = ActionPlanPriorities.Normalize(body.Risk);
            row.RiskScore = ActionPlanPriorities.ScoreFromTier(row.Risk);
        }

        if (!string.IsNullOrWhiteSpace(body.Status))
        {
            var next = GapStatuses.Normalize(body.Status);
            if (next == GapStatuses.Resolved && !await NdGapStatusResolver.CanResolveByHandAsync(db, pointId, gapIndex, ct))
            {
                return BadRequest(new
                {
                    success = false,
                    code = "actions_pending",
                    message = "Resolve every action on this gap before marking the gap resolved.",
                });
            }

            row.Status = next;
            row.ResolvedAt = next == GapStatuses.Resolved ? DateTimeOffset.UtcNow : null;
            row.ResolvedBy = next == GapStatuses.Resolved ? profile!.Id : null;
        }

        row.UpdatedBy = profile!.Id;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // A gap risk change cascades into the actions that inherit it.
        if (body.RiskScore.HasValue || !string.IsNullOrWhiteSpace(body.Risk))
            await ApplyRiskToActionsAsync(row, ct);

        await NdGapStatusResolver.RecomputeAsync(db, [pointId], ct);
        dashboardCache.Invalidate();

        var names = await LoadProfileNamesAsync(db, [row.ResolvedBy], ct);
        return Ok(new { success = true, data = Map(row, names) });
    }

    /// <summary>Actions inherit their parent gap's risk, so a risk edit rewrites them too.</summary>
    private async Task ApplyRiskToActionsAsync(NdAnalysisGap gap, CancellationToken ct)
    {
        var plans = await db.NdAnalysisActionPlans
            .Where(p => p.AnalysisPointId == gap.AnalysisPointId
                && (p.GapIndex == gap.GapIndex || (p.GapIndex == 0 && gap.GapIndex == 1)))
            .ToListAsync(ct);
        if (plans.Count == 0) return;

        foreach (var plan in plans)
        {
            plan.PriorityScore = gap.RiskScore;
            plan.Priority = gap.Risk;
            plan.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<IActionResult?> EnsureRunVisibleAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Run not found." });

        var user = ValidateJwt(jwt);
        if (user == null) return Unauthorized(new { success = false, message = "Unauthorized" });

        var ctx = await demoDirectory.ResolveContextAsync(user, ct);
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, ctx))
            return NotFound(new { success = false, message = "Run not found." });

        return null;
    }

    private static object Map(NdAnalysisGap g, IReadOnlyDictionary<Guid, string> names) => new
    {
        id = g.Id,
        analysisRunId = g.AnalysisRunId,
        analysisPointId = g.AnalysisPointId,
        gapIndex = g.GapIndex,
        risk = g.Risk,
        riskScore = g.RiskScore,
        status = g.Status,
        resolvedAt = g.ResolvedAt,
        resolvedBy = g.ResolvedBy,
        resolvedByName = ProfileName(names, g.ResolvedBy),
        updatedAt = g.UpdatedAt,
    };
}
