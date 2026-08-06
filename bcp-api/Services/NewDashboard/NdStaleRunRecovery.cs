using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Recovers ND analysis runs left in <c>running</c> after API restart or a missed Stop.</summary>
public static class NdStaleRunRecovery
{
    private static readonly TimeSpan StaleNoProgress = TimeSpan.FromMinutes(15);

    public static async Task<int> RecoverOrphanedRunsAsync(
        AppDbContext db,
        NdAnalysisRunCancellationTracker tracker,
        ILogger logger,
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - StaleNoProgress;
        var candidates = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .Where(r => r.Status == "running"
                && r.ProcessedPointsCount == 0
                && r.UpdatedAt < cutoff)
            .OrderBy(r => r.UpdatedAt)
            .Take(25)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var recovered = 0;
        foreach (var run in candidates)
        {
            if (tracker.HasActiveWorker(run.Id)) continue;

            var runningPoints = run.Points.Count(p =>
                p.LandingAiStatus == "running"
                || p.DualVerifyStatus == "running"
                || p.GoogleAiStatus == "running");
            if (!AnalysisActivityHelper.IsStillActive(
                    run.Status,
                    run.ProcessedPointsCount,
                    run.DualVerifyFailedCount,
                    run.TotalPointsCount,
                    run.UpdatedAt,
                    runningPoints))
            {
                await CancelOrphanedRunAsync(db, tracker, run, ct);
                recovered++;
                logger.LogWarning(
                    "Recovered orphaned ND analysis run {RunId} ({Name}) as cancelled (no worker, last update {UpdatedAt})",
                    run.Id,
                    run.Name,
                    run.UpdatedAt);
            }
        }

        if (recovered > 0)
            await db.SaveChangesAsync(ct);

        return recovered;
    }

    /// <summary>Cancel every running/processing run with no in-process worker (safe after API restart).</summary>
    public static async Task<int> CancelAllRunningWithoutWorkerAsync(
        AppDbContext db,
        NdAnalysisRunCancellationTracker tracker,
        ILogger logger,
        CancellationToken ct)
    {
        var runIds = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status == "running" || r.Status == "processing")
            .Select(r => r.Id)
            .ToListAsync(ct);

        var toCancel = runIds.Where(id => !tracker.HasActiveWorker(id)).ToList();
        if (toCancel.Count == 0) return 0;

        var updated = await db.NdAnalysisRuns
            .Where(r => toCancel.Contains(r.Id))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, "failed")
                    .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (updated > 0)
            logger.LogWarning("Marked {Count} stuck analysis run(s) as failed (no in-process worker)", updated);

        return updated;
    }

    public static async Task<bool> TryRecoverRunAsync(
        Guid runId,
        AppDbContext db,
        NdAnalysisRunCancellationTracker tracker,
        ILogger logger,
        CancellationToken ct)
    {
        if (tracker.HasActiveWorker(runId)) return false;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null || run.Status != "running") return false;

        var runningPoints = run.Points.Count(p =>
            p.LandingAiStatus == "running"
            || p.DualVerifyStatus == "running"
            || p.GoogleAiStatus == "running");
        if (AnalysisActivityHelper.IsStillActive(
                run.Status,
                run.ProcessedPointsCount,
                run.DualVerifyFailedCount,
                run.TotalPointsCount,
                run.UpdatedAt,
                runningPoints))
        {
            return false;
        }

        await CancelOrphanedRunAsync(db, tracker, run, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Recovered orphaned ND analysis run {RunId} ({Name}) as cancelled on status poll",
            run.Id,
            run.Name);
        return true;
    }

    private static async Task CancelOrphanedRunAsync(
        AppDbContext db,
        NdAnalysisRunCancellationTracker tracker,
        NdAnalysisRun run,
        CancellationToken ct)
    {
        tracker.RequestStop(run.Id);
        run.Status = "cancelled";
        await NdRegulRunStopHelper.ApplyAsync(db, run, ct);

        foreach (var point in run.Points)
        {
            if (point.LandingAiStatus is "pending" or "running")
            {
                point.LandingAiStatus = "cancelled";
                point.LandingAiError = "Stopped — run was orphaned after server restart";
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (point.DualVerifyStatus is "pending" or "running")
            {
                point.DualVerifyStatus = "cancelled";
                if (point.GoogleAiStatus is "running" or "pending")
                    point.GoogleAiStatus = "cancelled";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        run.ProcessedPointsCount = run.Points.Count(p =>
            p.LandingAiStatus is "compliant" or "partial_compliant" or "non_compliant" or "failed" or "cancelled");
        run.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
