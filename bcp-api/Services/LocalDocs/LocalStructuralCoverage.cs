using System.Text.RegularExpressions;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Compares parsed markdown to the union of structural section texts so we can spot true loss
/// (tables skipped, whole TOC pages dropped) vs mis-split (text lives under another clause).
/// </summary>
public static class LocalStructuralCoverage
{
    public const double LowCoverageThreshold = 0.98;

    private static readonly Regex PageMarkerPattern = new(
        Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"\d+\s*-->",
        RegexOptions.Compiled);

    private static readonly Regex HtmlCommentPattern = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"[a-z0-9]+", RegexOptions.Compiled);

    public sealed record Report(
        double CoverageRatio,
        int ParseTokenCount,
        int CoveredTokenCount,
        string? OrphanSnippet);

    public static Report Compute(string markdown, IReadOnlyList<LocalSection> sections)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new Report(1, 0, 0, null);

        var parseNorm = Normalize(markdown);
        var sectionNorm = Normalize(string.Join("\n", sections.Select(s => s.ClauseText ?? "")));

        var parseTokens = Tokenize(parseNorm);
        if (parseTokens.Count == 0)
            return new Report(1, 0, 0, null);

        var covered = 0;
        foreach (var token in parseTokens)
        {
            if (sectionNorm.Contains(token, StringComparison.Ordinal))
                covered++;
        }

        var ratio = (double)covered / parseTokens.Count;
        var orphan = FindLongestUncoveredSnippet(parseNorm, sectionNorm);

        return new Report(ratio, parseTokens.Count, covered, orphan);
    }

    public static bool IsLowCoverage(double? ratio) =>
        ratio.HasValue && ratio.Value < LowCoverageThreshold;

    private static string Normalize(string text)
    {
        var s = text;
        s = PageMarkerPattern.Replace(s, " ");
        s = HtmlCommentPattern.Replace(s, " ");
        s = HtmlTagPattern.Replace(s, " ");
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static List<string> Tokenize(string normalized)
    {
        var list = new List<string>();
        foreach (Match m in TokenPattern.Matches(normalized))
        {
            var t = m.Value;
            if (t.Length < 3) continue;
            if (t.All(char.IsDigit)) continue;
            list.Add(t);
        }
        return list;
    }

    private static string? FindLongestUncoveredSnippet(string parseNorm, string sectionNorm)
    {
        var words = parseNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return null;

        string? best = null;
        var bestLen = 0;
        var run = new List<string>();

        void FlushRun()
        {
            if (run.Count == 0) return;
            var snippet = string.Join(' ', run);
            if (snippet.Length > bestLen)
            {
                bestLen = snippet.Length;
                best = snippet.Length > 480 ? snippet[..477] + "…" : snippet;
            }
            run.Clear();
        }

        foreach (var w in words)
        {
            if (w.Length < 3)
            {
                FlushRun();
                continue;
            }

            if (sectionNorm.Contains(w, StringComparison.Ordinal))
            {
                FlushRun();
                continue;
            }

            run.Add(w);
        }

        FlushRun();
        return bestLen >= 24 ? best : null;
    }
}
