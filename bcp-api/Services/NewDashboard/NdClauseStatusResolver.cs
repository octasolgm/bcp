using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Compliance verdicts a clause can carry.</summary>
public static class ClauseStatuses
{
    public const string Compliant = "compliant";
    public const string Partial = "partial_compliant";
    public const string NonCompliant = "non_compliant";

    public static bool IsValid(string? value) =>
        value is Compliant or Partial or NonCompliant;

    public static string? Normalize(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return v switch
        {
            "compliant" => Compliant,
            "partial" or "partial_compliant" or "partially_compliant" => Partial,
            "non_compliant" or "noncompliant" or "not_compliant" => NonCompliant,
            _ => null,
        };
    }
}

/// <summary>
/// Keeps a clause's compliance status in step with the actions raised against it:
/// a clause whose actions are all resolved reads as compliant, and reopening any of
/// them puts the AI verdict back. A status a person set by hand is never touched.
/// </summary>
public static class NdClauseStatusResolver
{
    /// <summary>
    /// Recomputes the clause status for each point. Call after any change to action plan
    /// status. Returns the number of points whose status actually moved.
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

        var plans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => ids.Contains(p.AnalysisPointId))
            .Select(p => new { p.AnalysisPointId, p.Status })
            .ToListAsync(ct);

        var changed = 0;
        foreach (var point in points)
        {
            // A verdict someone chose by hand outranks both the pipeline and this rule.
            if (point.FinalStatusSource == "manual") continue;

            var mine = plans.Where(p => p.AnalysisPointId == point.Id).ToList();
            var allResolved = mine.Count > 0 && mine.All(p => p.Status == ActionPlanStatuses.Resolved);

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

    /// <summary>Records a status a user picked, so the automatic rule stops managing it.</summary>
    public static void ApplyManual(NdAnalysisPoint point, string status)
    {
        point.AiFinalStatus ??= point.FinalStatus;
        point.FinalStatus = status;
        point.FinalStatusSource = "manual";
    }

    /// <summary>Hands the clause back to the pipeline verdict and the automatic rule.</summary>
    public static void ClearManual(NdAnalysisPoint point)
    {
        if (point.FinalStatusSource != "manual") return;
        point.FinalStatus = point.AiFinalStatus ?? point.FinalStatus;
        point.FinalStatusSource = null;
    }
}
