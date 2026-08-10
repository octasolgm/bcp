using System.Text.RegularExpressions;
using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Dashboard Critical / Medium / Low gap cards — same bands as the web risk-priority-score helper
/// (0–33 Low · 34–66 Medium · 67–100 Critical).
/// </summary>
public static class NdGapRiskCounter
{
    public readonly record struct Counts(int Critical, int Medium, int Low)
    {
        public int Total => Critical + Medium + Low;

        public static Counts Empty => new(0, 0, 0);

        public Counts Add(Counts other) =>
            new(Critical + other.Critical, Medium + other.Medium, Low + other.Low);
    }

    private static readonly Regex CapGapChunkRegex = new(
        @"\(\d+\)\s*Missing:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PriorityInChunkRegex = new(
        @"\.\s*Priority:\s*(low|medium|higher|high|critical)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Counts AggregateForPoints(
        IEnumerable<NdAnalysisPoint> points,
        IReadOnlyDictionary<(Guid PointId, int ActionIndex), string?>? reviewPriorityByAction = null)
    {
        var critical = 0;
        var medium = 0;
        var low = 0;

        foreach (var point in points)
        {
            var status = NdRunEnrichmentHelper.EffectiveComplianceStatus(
                point.FinalStatus, point.LandingAiStatus, point.GoogleAiStatus);
            // Skip queued / pending — do not invent Critical/Medium gaps before scoring.
            if (status is null or "compliant") continue;

            var plan = ResolveCapSource(point);
            var chunks = ExtractGapChunks(plan);
            if (chunks.Count > 0 && !IsWeakCorrectivePlan(plan))
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var actionIndex = i + 1;
                    string? fromReview = null;
                    reviewPriorityByAction?.TryGetValue((point.Id, actionIndex), out fromReview);
                    var priorityRaw = !string.IsNullOrWhiteSpace(fromReview)
                        ? fromReview
                        : ExtractPriorityLabel(chunks[i]);
                    Add(ScoreFromRaw(priorityRaw), ref critical, ref medium, ref low);
                }
                continue;
            }

            // No usable CAP rows — still count the finding by compliance severity.
            var fallback = status == "non_compliant" ? 85 : 50;
            Add(fallback, ref critical, ref medium, ref low);
        }

        return new Counts(critical, medium, low);
    }

    /// <summary>Light projection row (avoids loading AI result blobs).</summary>
    public static Counts AggregateForCapRows(
        IEnumerable<CapRow> rows,
        IReadOnlyDictionary<(Guid PointId, int ActionIndex), string?>? reviewPriorityByAction = null)
    {
        // Reuse entity-shaped aggregation via thin wrappers.
        var points = rows.Select(r => new NdAnalysisPoint
        {
            Id = r.Id,
            FinalStatus = r.FinalStatus,
            LandingAiStatus = r.LandingAiStatus,
            FinalActionPlan = r.FinalActionPlan,
            OriginalAiActionPlan = r.OriginalAiActionPlan,
            LandingAiActionPlan = r.LandingAiActionPlan,
        });
        return AggregateForPoints(points, reviewPriorityByAction);
    }

    public readonly record struct CapRow(
        Guid Id,
        string? FinalStatus,
        string LandingAiStatus,
        string? FinalActionPlan,
        string? OriginalAiActionPlan,
        string? LandingAiActionPlan);

    private static string ResolveCapSource(NdAnalysisPoint point)
    {
        var plan = point.FinalActionPlan?.Trim();
        if (string.IsNullOrEmpty(plan)) plan = point.OriginalAiActionPlan?.Trim();
        if (string.IsNullOrEmpty(plan)) plan = point.LandingAiActionPlan?.Trim();
        return plan ?? "";
    }

    private static List<string> ExtractGapChunks(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return [];
        var matches = CapGapChunkRegex.Matches(plan);
        if (matches.Count == 0)
        {
            if (IsWeakCorrectivePlan(plan) || IsPlaceholderCap(plan)) return [];
            return [plan.Trim()];
        }

        var chunks = new List<string>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : plan.Length;
            chunks.Add(plan[start..end].Trim());
        }
        return chunks;
    }

    private static string? ExtractPriorityLabel(string chunk)
    {
        var m = PriorityInChunkRegex.Match(chunk);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsPlaceholderCap(string plan)
    {
        var t = plan.Replace("*", "", StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(t)
            || t is "n/a" or "N/A" or "—" or "-" or "none" or "None" or "not applicable";
    }

    private static bool IsWeakCorrectivePlan(string plan)
    {
        if (IsPlaceholderCap(plan)) return true;
        if (plan.Contains("Re-run comparison", StringComparison.OrdinalIgnoreCase)) return true;
        if (plan.Contains("verify internal document", StringComparison.OrdinalIgnoreCase)
            && !plan.Contains("Missing:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(plan, @"Missing:\s*MISSING\b", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static int ScoreFromRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 50;
        if (int.TryParse(raw.Trim(), out var n)) return Math.Clamp(n, 0, 100);
        var t = raw.Trim().ToLowerInvariant();
        return t switch
        {
            "low" => 25,
            "medium" => 50,
            "higher" or "high" or "critical" => 85,
            _ => 50,
        };
    }

    private static void Add(int score, ref int critical, ref int medium, ref int low)
    {
        var s = Math.Clamp(score, 0, 100);
        if (s <= 33) low++;
        else if (s <= 66) medium++;
        else critical++;
    }
}
