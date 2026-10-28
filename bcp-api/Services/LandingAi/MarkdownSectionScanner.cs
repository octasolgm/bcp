using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Scan Landing parse markdown for numbered section headings (e.g. 1. Introduction, 7.4, 9.4.1).
/// Used to backfill sections/points Landing extract missed and to remap invented ids like 1-14.
/// </summary>
public static partial class MarkdownSectionScanner
{
    private const int MinSectionChars = 40;

    public sealed record ScannedSection(string SectionRef, string SectionText, int StartIndex);

    public static IReadOnlyList<ScannedSection> Scan(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var matches = SectionHeadingRegex().Matches(markdown);
        if (matches.Count == 0) return [];

        var valid = new List<Match>();
        foreach (Match m in matches)
        {
            var sectionRef = HeadingRef(m);
            if (string.IsNullOrWhiteSpace(sectionRef)) continue;
            var title = m.Groups["title"].Value.Trim();
            if (!IsPlausibleHeading(sectionRef, title)) continue;
            if (IsTocHeadingLine(title)) continue;
            valid.Add(m);
        }

        var list = new List<ScannedSection>();
        for (var i = 0; i < valid.Count; i++)
        {
            var m = valid[i];
            var sectionRef = HeadingRef(m);
            var headingEnd = m.Index + m.Length;
            var bodyEnd = i + 1 < valid.Count ? valid[i + 1].Index : markdown.Length;
            var body = PolicyExtractTextSanitizer.Clean(markdown[headingEnd..bodyEnd]);
            var heading = PolicyExtractTextSanitizer.Clean(m.Value);
            var fullText = string.IsNullOrWhiteSpace(body)
                ? heading
                : $"{heading}\n{body}".Trim();

            if (fullText.Length < MinSectionChars) continue;

            list.Add(new ScannedSection(sectionRef, fullText, m.Index));
        }

        return list;
    }

    public static string NormalizeRef(string raw) => raw.Trim().TrimEnd('.');

    private static string HeadingRef(Match m)
    {
        var hierarchical = m.Groups["hier"].Value;
        if (!string.IsNullOrWhiteSpace(hierarchical))
            return NormalizeRef(hierarchical);
        return NormalizeRef(m.Groups["major"].Value);
    }

    internal static bool IsPlausibleHeading(string sectionRef, string title)
    {
        var t = title.Trim().TrimEnd(':', '.', ';');
        if (t.Length < 3) return false;
        if (t.Contains('<', StringComparison.Ordinal) && t.Contains("id=", StringComparison.OrdinalIgnoreCase))
            return false;
        if (UuidInTitleRegex().IsMatch(t)) return false;
        if (CitationRuleRegex().IsMatch(t)) return false;
        if (t.Contains('|', StringComparison.Ordinal) && t.Contains("page", StringComparison.OrdinalIgnoreCase))
            return false;

        if (LooksLikeSentence(t)) return false;

        var hierarchical = sectionRef.Contains('.', StringComparison.Ordinal);
        if (hierarchical)
            return t[0] is >= 'A' and <= 'Z';

        if (t.Contains('(', StringComparison.Ordinal) && !t.Contains("KYC", StringComparison.OrdinalIgnoreCase)
            && !t.Contains("PEP", StringComparison.OrdinalIgnoreCase)
            && !t.Contains("AML", StringComparison.OrdinalIgnoreCase))
            return false;

        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 1 or > 10) return false;

        return words[0][0] is >= 'A' and <= 'Z';
    }

    internal static bool IsTocHeadingLine(string title)
    {
        var t = title.Trim();
        return TocDotsRegex().IsMatch(t) || TocTrailingPageRegex().IsMatch(t);
    }

    private static bool LooksLikeSentence(string title)
    {
        if (SentenceLeadRegex().IsMatch(title)) return true;
        return SentenceVerbRegex().IsMatch(title);
    }

    [GeneratedRegex(
        @"(?m)^[\s#>*-]*(?:(?:AML\s+)?(?:Rule|Section|Article)\s+)?(?:(?<hier>\d+\.\d+(?:\.\d+)*)|(?<major>\d+)\.)\s+(?<title>[A-Z][^\n]{2,120})$")]
    private static partial Regex SectionHeadingRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidInTitleRegex();

    [GeneratedRegex(@"\b(?:AML\s+)?rule\s+\d", RegexOptions.IgnoreCase)]
    private static partial Regex CitationRuleRegex();

    [GeneratedRegex(@"\.{3,}\s*\d+\s*$")]
    private static partial Regex TocDotsRegex();

    [GeneratedRegex(@"\s+\.{2,}\s*\d+\s*$")]
    private static partial Regex TocTrailingPageRegex();

    [GeneratedRegex(@"^(Any|The|A|An|In|If|When|This|Each|All|There)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceLeadRegex();

    [GeneratedRegex(@"\b(shall|must|should not|knows that|means a)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceVerbRegex();
}
