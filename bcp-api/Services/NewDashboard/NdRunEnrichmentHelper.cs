using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdRunEnrichmentHelper
{
    public static string WorkflowHolderLabel(string status) => status switch
    {
        "submitted_for_review" => "With checker",
        "checker_approved" => "With reviewer",
        "reviewer_approved" => "Review complete",
        "pulled_back" => "With maker (correction)",
        "completed" or "dual_verify_failed" or "landing_ai_complete" => "With maker",
        _ when status is "draft" or "pending" or "running" or "processing" => "With maker",
        _ => "With maker",
    };

    public static async Task<List<object>> EnrichRunsAsync(
        AppDbContext db,
        IReadOnlyList<NdAnalysisRun> runs,
        CancellationToken ct)
    {
        if (runs.Count == 0) return [];

        var runIds = runs.Select(r => r.Id).ToList();
        var makerIds = runs.Where(r => r.CreatedBy.HasValue).Select(r => r.CreatedBy!.Value).Distinct().ToList();

        var makers = makerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => makerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName ?? "", ct);

        var points = await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => runIds.Contains(p.AnalysisRunId))
            .ToListAsync(ct);

        var pointsByRun = points.GroupBy(p => p.AnalysisRunId).ToDictionary(g => g.Key, g => g.ToList());

        // Distinct (point, actionIndex) pairs are computed in memory — EF translates
        // anonymous Distinct().Count() to invalid SQL: count(DISTINCT *).
        var reviewRows = await db.NdActionPlanItemReviews.AsNoTracking()
            .Join(
                db.NdAnalysisPoints.AsNoTracking().Where(p => runIds.Contains(p.AnalysisRunId)),
                r => r.AnalysisPointId,
                p => p.Id,
                (r, p) => new { p.AnalysisRunId, PointId = p.Id, r.ActionIndex, r.Status })
            .Where(x => !string.IsNullOrWhiteSpace(x.Status) && x.ActionIndex >= 1)
            .ToListAsync(ct);

        var reviewedByRun = reviewRows
            .GroupBy(x => x.AnalysisRunId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => (x.PointId, x.ActionIndex)).Distinct().Count());

        var totalReviewsByRun = await db.NdActionPlanItemReviews.AsNoTracking()
            .Join(
                db.NdAnalysisPoints.AsNoTracking().Where(p => runIds.Contains(p.AnalysisRunId)),
                r => r.AnalysisPointId,
                p => p.Id,
                (r, p) => new { p.AnalysisRunId })
            .GroupBy(x => x.AnalysisRunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RunId, x => x.Count, ct);

        var attachmentCountsByPoint = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Join(
                db.NdAnalysisPoints.AsNoTracking().Where(p => runIds.Contains(p.AnalysisRunId)),
                a => a.AnalysisPointId,
                p => p.Id,
                (a, p) => new { p.AnalysisRunId, p.Id })
            .GroupBy(x => new { x.AnalysisRunId, x.Id })
            .Select(g => new { RunId = g.Key.AnalysisRunId, g.Key.Id, Count = g.Count() })
            .ToListAsync(ct);

        var manualEvidenceByRun = attachmentCountsByPoint
            .GroupBy(x => x.RunId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.Id, x => x.Count));

        var result = new List<object>();
        foreach (var run in runs)
        {
            pointsByRun.TryGetValue(run.Id, out var runPoints);
            runPoints ??= [];

            var compliant = runPoints.Count(p =>
                NdRunEnrichmentHelper.EffectiveComplianceStatus(p.FinalStatus, p.LandingAiStatus) == "compliant");
            var partial = runPoints.Count(p =>
                NdRunEnrichmentHelper.EffectiveComplianceStatus(p.FinalStatus, p.LandingAiStatus) == "partial_compliant");
            var nonCompliant = runPoints.Count(p =>
                NdRunEnrichmentHelper.EffectiveComplianceStatus(p.FinalStatus, p.LandingAiStatus) == "non_compliant");

            manualEvidenceByRun.TryGetValue(run.Id, out var byPoint);
            var totalGaps = NdCapGapCounter.CountForPoints(runPoints, byPoint);
            var gapRisk = NdGapRiskCounter.AggregateForPoints(runPoints);

            reviewedByRun.TryGetValue(run.Id, out var reviewedGaps);
            totalReviewsByRun.TryGetValue(run.Id, out var totalReviews);

            result.Add(new
            {
                id = run.Id,
                source = "nd_analysis",
                name = run.Name,
                makerName = run.CreatedBy.HasValue && makers.TryGetValue(run.CreatedBy.Value, out var name) ? name : null,
                departmentId = run.DepartmentId,
                createdBy = run.CreatedBy,
                submittedAt = run.SubmittedToCheckerAt ?? run.SubmittedToReviewerAt,
                submittedToCheckerAt = run.SubmittedToCheckerAt,
                createdAt = run.CreatedAt,
                status = run.Status,
                workflowHolder = WorkflowHolderLabel(run.Status),
                compliant,
                partial,
                nonCompliant,
                totalGaps,
                reviewedGaps,
                totalReviews,
                criticalGaps = gapRisk.Critical,
                mediumGaps = gapRisk.Medium,
                lowGaps = gapRisk.Low,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                legacySessionId = (Guid?)null,
                legacyHref = (string?)null,
            });
        }

        return result;
    }

    private sealed record RunPointStatusAggregate(
        Guid RunId,
        int Compliant,
        int Partial,
        int NonCompliant,
        int Running);

  public sealed record WorkspaceDashboardStats(
        int Compliant,
        int Partial,
        int NonCompliant,
        int CriticalGaps,
        int MediumGaps,
        int LowGaps,
        int TotalRuns,
        DateTimeOffset? LastAnalysisAt);

    private sealed record WorkspaceComplianceTotals(int Compliant, int Partial, int NonCompliant);

    /// <summary>
    /// Fast list rows for nav, analysis-runs table, and dashboard cards.
    /// Uses SQL aggregation (no per-point hydration) so overview loads within Supabase pool limits.
    /// Gap-risk cards use point-severity approximations (non-compliant → critical, partial → medium).
    /// </summary>
    public static async Task<List<object>> MapSummariesLightAsync(
        AppDbContext db,
        IReadOnlyList<NdAnalysisRun> runs,
        CancellationToken ct,
        bool skipGapStats = false,
        IReadOnlySet<Guid>? demoProfileIds = null)
    {
        if (runs.Count == 0) return [];

        var runIds = runs.Select(r => r.Id).ToList();
        var makerIds = runs.Where(r => r.CreatedBy.HasValue).Select(r => r.CreatedBy!.Value).Distinct().ToList();
        var makers = makerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => makerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName ?? "", ct);

        var aggByRun = skipGapStats || runIds.Count == 0
            ? new Dictionary<Guid, RunPointStatusAggregate>()
            : await LoadPointStatusAggregatesAsync(db, runIds, ct);

        var list = new List<object>(runs.Count);
        foreach (var run in runs)
        {
            string? makerName = null;
            if (run.CreatedBy is Guid mid && makers.TryGetValue(mid, out var name))
                makerName = name;

            aggByRun.TryGetValue(run.Id, out var agg);
            var compliant = agg?.Compliant ?? 0;
            var partial = agg?.Partial ?? 0;
            var nonCompliant = agg?.NonCompliant ?? 0;
            var gapRisk = new NdGapRiskCounter.Counts(nonCompliant, partial, 0);
            var runningPoints = agg?.Running ?? 0;
            var isActive = AnalysisActivityHelper.IsStillActive(
                run.Status,
                run.ProcessedPointsCount,
                run.DualVerifyFailedCount,
                run.TotalPointsCount,
                run.UpdatedAt,
                runningPoints);

            var createdByIsDemo = run.CreatedBy is Guid creatorId
                && demoProfileIds != null
                && demoProfileIds.Contains(creatorId);

            list.Add(NdLegacyDataQueries.MapNdRunSummary(
                run,
                makerName,
                compliant,
                partial,
                nonCompliant,
                gapRisk.Critical,
                gapRisk.Medium,
                gapRisk.Low,
                runningPoints,
                isActive,
                createdByIsDemo));
        }

        return list;
    }

    /// <summary>One SQL scan for workspace dashboard cards (cached on WorkspaceController).</summary>
    public static async Task<WorkspaceDashboardStats> LoadWorkspaceDashboardStatsAsync(
        AppDbContext db,
        bool mineOnly,
        Guid? profileId,
        CancellationToken ct,
        NdDemoIsolationContext? demoCtx = null)
    {
        const string deleted = "deleted";
        var runsQ = db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status != deleted);
        if (demoCtx is { Enabled: true })
            runsQ = NdDemoDataFilters.ApplyToAnalysisRuns(runsQ, demoCtx);
        if (mineOnly && profileId.HasValue)
            runsQ = runsQ.Where(r => r.CreatedBy == profileId.Value);

        var runMeta = await runsQ
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), LastAt = g.Max(r => r.CreatedAt) })
            .FirstOrDefaultAsync(ct);

        WorkspaceComplianceTotals totals;
        if (demoCtx is { Enabled: true })
        {
            var runIds = await runsQ.Select(r => r.Id).ToListAsync(ct);
            totals = runIds.Count == 0
                ? new WorkspaceComplianceTotals(0, 0, 0)
                : await ComputeComplianceTotalsForRunsAsync(db, runIds, ct);
        }
        else if (mineOnly && profileId.HasValue)
        {
            totals = await db.Database.SqlQueryRaw<WorkspaceComplianceTotals>(
                """
                SELECT
                  COUNT(*) FILTER (WHERE effective_status = 'compliant')::int AS "Compliant",
                  COUNT(*) FILTER (WHERE effective_status = 'partial_compliant')::int AS "Partial",
                  COUNT(*) FILTER (WHERE effective_status = 'non_compliant')::int AS "NonCompliant"
                FROM (
                  SELECT
                    CASE
                      WHEN p.final_status IN ('compliant','partial_compliant','non_compliant') THEN p.final_status
                      WHEN p.landing_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.landing_ai_status
                      WHEN p.google_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.google_ai_status
                      ELSE NULL
                    END AS effective_status
                  FROM analysis_points p
                  INNER JOIN analysis_runs r ON r.id = p.analysis_run_id
                  WHERE r.status <> 'deleted' AND r.created_by = {0}
                ) t
                """,
                profileId.Value).FirstOrDefaultAsync(ct) ?? new WorkspaceComplianceTotals(0, 0, 0);
        }
        else
        {
            totals = await db.Database.SqlQueryRaw<WorkspaceComplianceTotals>(
                """
                SELECT
                  COUNT(*) FILTER (WHERE effective_status = 'compliant')::int AS "Compliant",
                  COUNT(*) FILTER (WHERE effective_status = 'partial_compliant')::int AS "Partial",
                  COUNT(*) FILTER (WHERE effective_status = 'non_compliant')::int AS "NonCompliant"
                FROM (
                  SELECT
                    CASE
                      WHEN p.final_status IN ('compliant','partial_compliant','non_compliant') THEN p.final_status
                      WHEN p.landing_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.landing_ai_status
                      WHEN p.google_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.google_ai_status
                      ELSE NULL
                    END AS effective_status
                  FROM analysis_points p
                  INNER JOIN analysis_runs r ON r.id = p.analysis_run_id
                  WHERE r.status <> 'deleted'
                ) t
                """).FirstOrDefaultAsync(ct) ?? new WorkspaceComplianceTotals(0, 0, 0);
        }

        var gapRisk = new NdGapRiskCounter.Counts(totals.NonCompliant, totals.Partial, 0);
        return new WorkspaceDashboardStats(
            totals.Compliant,
            totals.Partial,
            totals.NonCompliant,
            gapRisk.Critical,
            gapRisk.Medium,
            gapRisk.Low,
            runMeta?.Total ?? 0,
            runMeta?.LastAt);
    }

    /// <summary>
    /// Same tally as the production SQL above, restricted to a run-id list. Aggregating in
    /// SQL matters here: demo workspaces would otherwise stream every analysis point over
    /// the wire on each dashboard load.
    /// </summary>
    private static async Task<WorkspaceComplianceTotals> ComputeComplianceTotalsForRunsAsync(
        AppDbContext db,
        List<Guid> runIds,
        CancellationToken ct)
    {
        if (runIds.Count == 0) return new WorkspaceComplianceTotals(0, 0, 0);

        return await db.Database.SqlQueryRaw<WorkspaceComplianceTotals>(
            """
            SELECT
              COUNT(*) FILTER (WHERE effective_status = 'compliant')::int AS "Compliant",
              COUNT(*) FILTER (WHERE effective_status = 'partial_compliant')::int AS "Partial",
              COUNT(*) FILTER (WHERE effective_status = 'non_compliant')::int AS "NonCompliant"
            FROM (
              SELECT
                CASE
                  WHEN p.final_status IN ('compliant','partial_compliant','non_compliant') THEN p.final_status
                  WHEN p.landing_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.landing_ai_status
                  WHEN p.google_ai_status IN ('compliant','partial_compliant','non_compliant') THEN p.google_ai_status
                  ELSE NULL
                END AS effective_status
              FROM analysis_points p
              WHERE p.analysis_run_id = ANY({0})
            ) t
            """,
            runIds.ToArray()).FirstOrDefaultAsync(ct) ?? new WorkspaceComplianceTotals(0, 0, 0);
    }

    private static async Task<Dictionary<Guid, RunPointStatusAggregate>> LoadPointStatusAggregatesAsync(
        AppDbContext db,
        IReadOnlyList<Guid> runIds,
        CancellationToken ct)
    {
        if (runIds.Count == 0) return [];

        // Chunk to keep IN lists small and avoid long scans when many runs exist.
        var result = new Dictionary<Guid, RunPointStatusAggregate>();
        foreach (var chunk in runIds.Chunk(10))
        {
            var batch = chunk.ToArray();
            var placeholders = string.Join(", ", batch.Select((_, i) => $"{{{i}}}"));
            var sql = $"""
                SELECT analysis_run_id AS "RunId",
                  COUNT(*) FILTER (WHERE effective_status = 'compliant')::int AS "Compliant",
                  COUNT(*) FILTER (WHERE effective_status = 'partial_compliant')::int AS "Partial",
                  COUNT(*) FILTER (WHERE effective_status = 'non_compliant')::int AS "NonCompliant",
                  COUNT(*) FILTER (WHERE landing_ai_status = 'running')::int AS "Running"
                FROM (
                  SELECT analysis_run_id, landing_ai_status,
                    CASE
                      WHEN final_status IN ('compliant','partial_compliant','non_compliant') THEN final_status
                      WHEN landing_ai_status IN ('compliant','partial_compliant','non_compliant') THEN landing_ai_status
                      WHEN google_ai_status IN ('compliant','partial_compliant','non_compliant') THEN google_ai_status
                      ELSE NULL
                    END AS effective_status
                  FROM analysis_points
                  WHERE analysis_run_id IN ({placeholders})
                ) t
                GROUP BY analysis_run_id
                """;

            var rows = await db.Database
                .SqlQueryRaw<RunPointStatusAggregate>(sql, batch.Cast<object>().ToArray())
                .ToListAsync(ct);

            foreach (var row in rows)
                result[row.RunId] = row;
        }

        return result;
    }

    /// <summary>Prefer dual-verify final status; fall back to Landing / LLM pass for in-progress runs.</summary>
    public static string? EffectiveComplianceStatus(
        string? finalStatus,
        string? landingAiStatus,
        string? googleAiStatus = null)
    {
        if (!string.IsNullOrWhiteSpace(finalStatus)
            && finalStatus is "compliant" or "partial_compliant" or "non_compliant")
            return finalStatus;

        if (!string.IsNullOrWhiteSpace(landingAiStatus)
            && landingAiStatus is "compliant" or "partial_compliant" or "non_compliant")
            return landingAiStatus;

        if (!string.IsNullOrWhiteSpace(googleAiStatus)
            && googleAiStatus is "compliant" or "partial_compliant" or "non_compliant")
            return googleAiStatus;

        return null;
    }
}
