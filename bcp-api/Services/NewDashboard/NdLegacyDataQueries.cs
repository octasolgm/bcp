using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Merges legacy stored_documents / analysis tables into ND API responses.</summary>
public static class NdLegacyDataQueries
{
    public static async Task<HashSet<string>> GetExtractCachedHashesAsync(
        AppDbContext db,
        IEnumerable<string?> hashes,
        CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = hashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return set;

        try
        {
            foreach (var batch in list.Chunk(100))
            {
                var batchHashes = batch.ToArray();
                var placeholders = string.Join(", ", batchHashes.Select((_, i) => $"{{{i}}}"));
                var sql =
                    $"SELECT file_hash AS \"Hash\" FROM landing_ai_extract_cache WHERE file_hash IN ({placeholders})";
                var rows = await db.Database
                    .SqlQueryRaw<HashRow>(sql, batchHashes.Cast<object>().ToArray())
                    .ToListAsync(ct);
                foreach (var r in rows)
                    if (!string.IsNullOrWhiteSpace(r.Hash)) set.Add(r.Hash);
            }
        }
        catch
        {
            // cache table may not exist in some environments
        }

        return set;
    }

    private sealed record HashRow(string Hash);

    public static string LegacyRegulationExtractionStatus(
        StoredDocument doc,
        IReadOnlySet<string> cachedHashes) =>
        LegacyRegulationExtractionStatus(doc.PointCount, doc.FileHash, cachedHashes);

    public static string LegacyRegulationExtractionStatus(
        int? pointCount,
        string? fileHash,
        IReadOnlySet<string> cachedHashes)
    {
        if (pointCount is > 0) return "completed";
        if (!string.IsNullOrWhiteSpace(fileHash) && cachedHashes.Contains(fileHash))
            return "completed";
        return "pending";
    }

    public static string MapLegacyAnalysisStatus(string status) => status switch
    {
        "completed" => "completed",
        "failed" => "failed",
        "cancelled" => "failed",
        "running" or "queued" or "processing" => "running",
        _ => "draft",
    };

    public static string MapDualVerifyStatus(string status) => status switch
    {
        "completed" => "completed",
        "failed" => "failed",
        "running" or "queued" => "running",
        _ => "draft",
    };

    public static object MapLegacyAnalysisRun(DocumentAnalysisRun run) => new
    {
        id = run.Id,
        source = "legacy_analysis",
        name = string.IsNullOrWhiteSpace(run.Label)
            ? $"{run.RegulationFileName ?? "Regulation"} × {run.InternalFileName ?? "Compliance"}"
            : run.Label,
        status = MapLegacyAnalysisStatus(run.Status),
        totalPointsCount = run.PointCount,
        processedPointsCount = run.CompletedPoints,
        dualVerifyFailedCount = 0,
        departmentId = (Guid?)null,
        createdBy = (Guid?)null,
        createdAt = run.CreatedAt,
        submittedToCheckerAt = (DateTimeOffset?)null,
        legacySessionId = run.DualVerifySessionId,
        legacyHref = run.DualVerifySessionId.HasValue
            ? $"/nd/analyse-v8?session={run.DualVerifySessionId}"
            : run.ComplianceSessionId.HasValue
                ? $"/nd/gap-analysis?saved=compliance:{run.ComplianceSessionId}"
                : run.Status is "failed" or "cancelled"
                    ? $"/nd/in-progress"
                    : null,
    };

    public static object MapLegacyDualVerifySession(DualVerifySession session) => new
    {
        id = session.Id,
        source = "legacy_dual_verify",
        name = $"{session.GovFileName ?? session.GovDocId} × {session.InternalFileName ?? session.InternalDocId}",
        status = MapDualVerifyStatus(session.Status),
        totalPointsCount = session.TotalPoints,
        processedPointsCount = session.CompletedPoints,
        dualVerifyFailedCount = session.FailedPoints,
        departmentId = (Guid?)null,
        createdBy = (Guid?)null,
        createdAt = new DateTimeOffset(DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc)),
        submittedToCheckerAt = (DateTimeOffset?)null,
        legacySessionId = session.Id,
        legacyHref = $"/nd/analyse-v8?session={session.Id}",
    };

    public static object MapNdRunSummary(
        NdAnalysisRun r,
        string? makerName = null,
        int? compliant = null,
        int? partial = null,
        int? nonCompliant = null,
        int? criticalGaps = null,
        int? mediumGaps = null,
        int? lowGaps = null,
        int runningPoints = 0,
        bool? isActive = null,
        bool createdByIsDemo = false,
        NdRunWorkCounts? work = null) => new
    {
        id = r.Id,
        source = "nd_analysis",
        name = r.Name,
        makerName,
        workflowEngine = r.WorkflowEngine,
        regulPipelinePhase = r.RegulPipelinePhase,
        regulLlmProvider = r.RegulLlmProvider,
        regulLlmModel = r.RegulLlmModel,
        status = r.Status,
        statusBeforeDelete = r.StatusBeforeDelete,
        deletedAt = r.DeletedAt,
        totalPointsCount = r.TotalPointsCount,
        processedPointsCount = r.ProcessedPointsCount,
        dualVerifyFailedCount = r.DualVerifyFailedCount,
        departmentId = r.DepartmentId,
        createdBy = r.CreatedBy,
        createdByIsDemo,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
        submittedToCheckerAt = r.SubmittedToCheckerAt,
        legacySessionId = (Guid?)null,
        legacyHref = (string?)null,
        workflowHolder = NdRunEnrichmentHelper.WorkflowHolderLabel(r.Status),
        compliant = compliant ?? 0,
        partial = partial ?? 0,
        nonCompliant = nonCompliant ?? 0,
        criticalGaps = criticalGaps ?? 0,
        mediumGaps = mediumGaps ?? 0,
        lowGaps = lowGaps ?? 0,
        runningPoints,
        isActive,
        gapCount = work?.Gaps ?? 0,
        resolvedGapCount = work?.ResolvedGaps ?? 0,
        pendingGapCount = work?.PendingGaps ?? 0,
        actionPlanCount = work?.Actions ?? 0,
        resolvedActionPlanCount = work?.ResolvedActions ?? 0,
        pendingActionPlanCount = work?.PendingActions ?? 0,
    };
}

/// <summary>Gap and action tallies for one run, shown on the analysis lists.</summary>
public sealed record NdRunWorkCounts(
    int Gaps,
    int ResolvedGaps,
    int Actions,
    int ResolvedActions)
{
    public int PendingGaps => Math.Max(0, Gaps - ResolvedGaps);
    public int PendingActions => Math.Max(0, Actions - ResolvedActions);
}
