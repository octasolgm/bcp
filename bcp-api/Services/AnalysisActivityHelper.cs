using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Services;

/// <summary>Shared rules for whether a dual-verify / document analysis run is still in progress.</summary>
public static class AnalysisActivityHelper
{
    private static readonly HashSet<string> Terminal = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed", "failed", "cancelled", "archived", "unavailable",
    };

    private static readonly HashSet<string> ActiveStatus = new(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "processing", "running", "in_progress", "in-progress",
    };

    private static readonly TimeSpan StaleQueuedThreshold = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleRunningThreshold = TimeSpan.FromHours(2);
    private static readonly TimeSpan StaleRunningNoProgressThreshold = TimeSpan.FromMinutes(15);

    public static bool IsStillActive(
        string status,
        int completedPoints,
        int failedPoints,
        int totalPoints,
        DateTimeOffset? updatedAt = null,
        int runningPoints = 0)
    {
        var st = (status ?? "").Trim().ToLowerInvariant();
        if (Terminal.Contains(st)) return false;
        // Draft = run created but analysis not started — not an active worker.
        if (st == "draft") return false;

        var done = completedPoints + failedPoints;
        if (totalPoints > 0 && done >= totalPoints) return false;

        if (st == "queued" && done == 0 && runningPoints == 0 && updatedAt.HasValue)
        {
            if (DateTimeOffset.UtcNow - updatedAt.Value > StaleQueuedThreshold)
                return false;
        }

        if ((st is "processing" or "running") && runningPoints > 0 && done < totalPoints && updatedAt.HasValue)
        {
            if (DateTimeOffset.UtcNow - updatedAt.Value > StaleRunningThreshold)
                return false;
        }

        // Zombie run: status still "running" but no point is active and nothing is progressing
        // (common after API restart or a failed Stop on Regul forward).
        if ((st is "processing" or "running") && runningPoints == 0 && done < totalPoints && updatedAt.HasValue)
        {
            var threshold = done == 0 ? StaleRunningNoProgressThreshold : StaleRunningThreshold;
            if (DateTimeOffset.UtcNow - updatedAt.Value > threshold)
                return false;
        }

        if (ActiveStatus.Contains(st)) return true;
        return totalPoints > 0 && done < totalPoints;
    }

    public static bool IsStillActive(DualVerifySession session) =>
        IsStillActive(
            session.Status,
            session.CompletedPoints,
            session.FailedPoints,
            session.TotalPoints,
            new DateTimeOffset(DateTime.SpecifyKind(session.UpdatedAt, DateTimeKind.Utc)),
            session.RunningPoints);

    public static string NormalizeDisplayStatus(
        string status,
        int completedPoints,
        int failedPoints,
        int totalPoints,
        DateTimeOffset? updatedAt = null,
        int runningPoints = 0)
    {
        if (IsStillActive(status, completedPoints, failedPoints, totalPoints, updatedAt, runningPoints))
            return status;

        var st = (status ?? "").Trim().ToLowerInvariant();
        if (Terminal.Contains(st)) return status;

        var done = completedPoints + failedPoints;
        if (totalPoints > 0 && done >= totalPoints)
            return failedPoints > 0 && completedPoints == 0 ? "failed" : "completed";

        if (st is "queued" or "processing" or "running")
            return "failed";

        return status;
    }
}
