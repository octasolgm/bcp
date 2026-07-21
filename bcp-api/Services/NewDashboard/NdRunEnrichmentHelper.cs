using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

public static class NdRunEnrichmentHelper
{
    private static readonly Regex CapGapChunkRegex = new(
        @"\(\d+\)\s*Missing:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(
        @"^\s*(n/a|—|-|none|not applicable|\*+\s*)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            .Where(x => !string.IsNullOrWhiteSpace(x.Status))
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

            var compliant = runPoints.Count(p => p.FinalStatus == "compliant");
            var partial = runPoints.Count(p => p.FinalStatus == "partial_compliant");
            var nonCompliant = runPoints.Count(p => p.FinalStatus == "non_compliant");
            var totalGaps = runPoints.Sum(p =>
            {
                manualEvidenceByRun.TryGetValue(run.Id, out var byPoint);
                var manual = byPoint != null && byPoint.TryGetValue(p.Id, out var c) ? c : 0;
                return CountCapGapsForPoint(p, manual);
            });
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
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                legacySessionId = (Guid?)null,
                legacyHref = (string?)null,
            });
        }

        return result;
    }

    private static int CountCapGapsForPoint(NdAnalysisPoint point, int manualEvidenceCount = 0)
    {
        if (string.Equals(point.FinalStatus, "compliant", StringComparison.OrdinalIgnoreCase))
            return manualEvidenceCount > 0 ? manualEvidenceCount : 0;

        var plan = point.FinalActionPlan?.Trim();
        if (string.IsNullOrEmpty(plan)) plan = point.OriginalAiActionPlan?.Trim();
        if (string.IsNullOrEmpty(plan) || IsPlaceholderCap(plan)) return manualEvidenceCount;

        var count = CapGapChunkRegex.Matches(plan).Count;
        if (count == 0 && !IsPlaceholderCap(plan)) count = 1;
        return Math.Max(count, manualEvidenceCount);
    }

    private static bool IsPlaceholderCap(string plan) =>
        PlaceholderRegex.IsMatch(plan.Replace("*", "").Trim());
}
