using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Rolls state up the clause → gap → action chain.
///
/// A gap is resolved once every action raised against it is resolved; reopening one
/// action reopens only that gap. A clause reads as compliant once every one of its
/// gaps is resolved. A verdict a person set by hand is never overwritten.
/// </summary>
public static class NdGapStatusResolver
{
    /// <summary>
    /// Recomputes gap status from action plans, then clause status from gap status.
    /// Call after anything that changes an action plan or a gap. Saves if anything moved.
    /// </summary>
    public static async Task<int> RecomputeAsync(
        AppDbContext db,
        IEnumerable<Guid> pointIds,
        CancellationToken ct)
    {
        var ids = pointIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        var points = await db.NdAnalysisPoints.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (points.Count == 0) return 0;

        var gaps = await db.NdAnalysisGaps
            .Where(g => ids.Contains(g.AnalysisPointId))
            .ToListAsync(ct);

        var plans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => ids.Contains(p.AnalysisPointId))
            .Select(p => new { p.AnalysisPointId, p.GapIndex, p.Status })
            .ToListAsync(ct);

        var changed = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var gap in gaps)
        {
            // Actions saved before gap scoping carry index 0; they belong to the first gap.
            var mine = plans
                .Where(p => p.AnalysisPointId == gap.AnalysisPointId
                    && (p.GapIndex == gap.GapIndex || (p.GapIndex == 0 && gap.GapIndex == 1)))
                .ToList();

            // With no actions there is nothing to derive from — leave whatever a person set.
            if (mine.Count == 0) continue;

            var resolved = mine.All(p => p.Status == ActionPlanStatuses.Resolved);
            var next = resolved ? GapStatuses.Resolved : GapStatuses.Pending;
            if (gap.Status == next) continue;

            gap.Status = next;
            gap.ResolvedAt = resolved ? now : null;
            gap.UpdatedAt = now;
            changed++;
        }

        foreach (var point in points)
        {
            if (point.FinalStatusSource == "manual") continue;

            var mine = gaps.Where(g => g.AnalysisPointId == point.Id).ToList();
            var allResolved = mine.Count > 0 && mine.All(g => g.Status == GapStatuses.Resolved);

            if (allResolved && point.FinalStatus != ClauseStatuses.Compliant)
            {
                point.AiFinalStatus ??= point.FinalStatus;
                point.FinalStatus = ClauseStatuses.Compliant;
                point.FinalStatusSource = "auto";
                changed++;
            }
            else if (!allResolved && point.FinalStatusSource == "auto")
            {
                point.FinalStatus = point.AiFinalStatus;
                point.FinalStatusSource = null;
                changed++;
            }
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// Makes sure a row exists for every gap the report is showing, so gap state survives
    /// even though the gap text itself is parsed out of the clause's CAP blob.
    /// <paramref name="risksByIndex"/> seeds risk on first sight only — it never overwrites
    /// a risk someone has since edited.
    /// </summary>
    public static async Task<int> EnsureRosterAsync(
        AppDbContext db,
        Guid runId,
        Guid pointId,
        IReadOnlyDictionary<int, string> risksByIndex,
        CancellationToken ct)
    {
        if (risksByIndex.Count == 0) return 0;

        var existing = await db.NdAnalysisGaps
            .Where(g => g.AnalysisPointId == pointId)
            .Select(g => g.GapIndex)
            .ToListAsync(ct);
        var have = existing.ToHashSet();

        var added = 0;
        foreach (var (index, risk) in risksByIndex)
        {
            if (index <= 0 || have.Contains(index)) continue;

            var tier = ActionPlanPriorities.Normalize(risk);
            db.NdAnalysisGaps.Add(new NdAnalysisGap
            {
                AnalysisRunId = runId,
                AnalysisPointId = pointId,
                GapIndex = index,
                Risk = tier,
                RiskScore = ActionPlanPriorities.ScoreFromTier(tier),
                Status = GapStatuses.Pending,
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Whether a gap may be marked resolved by hand. A gap that still has open actions
    /// cannot be closed — those actions have to be dealt with first.
    /// </summary>
    public static async Task<bool> CanResolveByHandAsync(
        AppDbContext db,
        Guid pointId,
        int gapIndex,
        CancellationToken ct)
    {
        var open = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => p.AnalysisPointId == pointId
                && (p.GapIndex == gapIndex || (p.GapIndex == 0 && gapIndex == 1))
                && p.Status != ActionPlanStatuses.Resolved)
            .CountAsync(ct);
        return open == 0;
    }
}
