using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Landing extract sometimes invents sequential ids like <c>1-14</c> instead of official
/// headings (<c>7.4</c>, <c>1. Introduction</c>). Remap those using parse markdown.
/// </summary>
public static partial class PolicyClauseOfficialRefAligner
{
    public static List<PolicyClause> Align(IReadOnlyList<PolicyClause> extracted, string? markdown)
    {
        if (extracted.Count == 0 || string.IsNullOrWhiteSpace(markdown))
            return extracted.ToList();

        var scanned = MarkdownSectionScanner.Scan(markdown);
        if (scanned.Count == 0)
            return extracted.ToList();

        var result = new List<PolicyClause>(extracted.Count);
        foreach (var clause in extracted)
        {
            if (!LooksInvented(clause.ClauseNo))
            {
                result.Add(clause);
                continue;
            }

            var matched = FindOfficialRef(scanned, clause.ClauseText);
            result.Add(matched is null ? clause : clause with { ClauseNo = matched });
        }

        return result;
    }

    public static List<PolicyClause> AlignThenMerge(IReadOnlyList<PolicyClause> extracted, string? markdown)
    {
        var aligned = Align(extracted, markdown);
        return PolicyClauseMarkdownRecovery.MergeMissing(aligned, markdown);
    }

    /// <summary>
    /// Replace short extract/seed blurbs with the full heading-to-heading body from parse markdown.
    /// </summary>
    public static List<PolicyClause> ExpandTextsFromMarkdown(
        IReadOnlyList<PolicyClause> clauses,
        string? markdown)
    {
        if (clauses.Count == 0 || string.IsNullOrWhiteSpace(markdown))
            return clauses.ToList();

        var scanned = MarkdownSectionScanner.Scan(markdown);
        if (scanned.Count == 0)
            return clauses.ToList();

        var byRef = scanned
            .GroupBy(s => MarkdownSectionScanner.NormalizeRef(s.SectionRef), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().SectionText, StringComparer.OrdinalIgnoreCase);

        return clauses.Select(clause =>
        {
            var key = MarkdownSectionScanner.NormalizeRef(clause.ClauseNo);
            if (!byRef.TryGetValue(key, out var full))
                return clause;
            var clean = PolicyExtractTextSanitizer.Clean(full);
            if (clean.Length <= clause.ClauseText.Trim().Length)
                return clause;
            if (clean.Contains("<a id", StringComparison.OrdinalIgnoreCase))
                return clause;
            return clause with { ClauseText = clean };
        }).ToList();
    }

    public static bool LooksInvented(string clauseNo)
    {
        var t = clauseNo.Trim();
        return InventedNumericDashRegex().IsMatch(t);
    }

    private static string? FindOfficialRef(
        IReadOnlyList<MarkdownSectionScanner.ScannedSection> scanned,
        string clauseText)
    {
        var needle = Normalize(clauseText);
        if (needle.Length < 24) return null;
        if (needle.Length > 48) needle = needle[..48];

        string? last = null;
        foreach (var section in scanned)
        {
            var hay = Normalize(section.SectionText);
            if (hay.Contains(needle, StringComparison.Ordinal))
                last = section.SectionRef;
        }

        return last;
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(value.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"^\d+\.?\-\d+$")]
    private static partial Regex InventedNumericDashRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
