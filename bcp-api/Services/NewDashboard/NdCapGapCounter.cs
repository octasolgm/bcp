using System.Text.RegularExpressions;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Counts corrective-action gaps from saved CAP text — same rules as ND run list enrichment.
/// </summary>
public static class NdCapGapCounter
{
    private static readonly Regex CapGapChunkRegex = new(
        @"\(\d+\)\s*Missing:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new(
        @"^\s*(n/a|—|-|none|not applicable|\*+\s*)+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int CountForPoint(NdAnalysisPoint point, int manualEvidenceCount = 0)
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

    public static int CountForPoints(
        IEnumerable<NdAnalysisPoint> points,
        IReadOnlyDictionary<Guid, int>? manualEvidenceByPointId = null)
    {
        return points.Sum(p =>
        {
            var manual = 0;
            if (manualEvidenceByPointId != null && manualEvidenceByPointId.TryGetValue(p.Id, out var c))
                manual = c;
            return CountForPoint(p, manual);
        });
    }

    private static bool IsPlaceholderCap(string plan) =>
        PlaceholderRegex.IsMatch(plan.Replace("*", "").Trim());
}
