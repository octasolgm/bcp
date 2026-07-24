using Reguliq.Api.Services;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Whether an ND analysis run is still executing points (not in checker/reviewer queue).</summary>
public static class NdRunActivityHelper
{
    private static readonly HashSet<string> ReviewWorkflow = new(StringComparer.OrdinalIgnoreCase)
    {
        "submitted_for_review", "checker_approved", "reviewer_approved",
    };

    public static bool IsProcessingRun(
        string status,
        int totalPoints,
        int processedPoints,
        int dualVerifyFailedCount,
        DateTimeOffset? updatedAt)
    {
        var st = (status ?? "").Trim();
        if (string.Equals(st, "deleted", StringComparison.OrdinalIgnoreCase)) return false;
        if (ReviewWorkflow.Contains(st)) return false;

        if (AnalysisActivityHelper.IsStillActive(
                st,
                processedPoints,
                dualVerifyFailedCount,
                totalPoints,
                updatedAt))
            return true;

        return false;
    }
}
