using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Scan Landing parse markdown for numbered section headings (e.g. 9.4.1, 8.4).
/// Used to backfill sections/points Landing extract missed.
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

        var list = new List<ScannedSection>();
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var sectionRef = NormalizeRef(m.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(sectionRef)) continue;

            var headingEnd = m.Index + m.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[headingEnd..bodyEnd].Trim();
            var fullText = string.IsNullOrWhiteSpace(body)
                ? m.Value.Trim()
                : $"{m.Value.Trim()}\n{body}".Trim();

            if (fullText.Length < MinSectionChars) continue;

            list.Add(new ScannedSection(sectionRef, fullText, m.Index));
        }

        return list;
    }

    public static string NormalizeRef(string raw) => raw.Trim().TrimEnd('.');

    [GeneratedRegex(
        @"(?m)^[\s#>*-]*(?:(?:AML\s+)?(?:Rule|Section|Article)\s+)?(\d+(?:\.\d+)+)\s*(?:[.:)\-–]\s*|\s+)(.{8,})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeadingRegex();
}
