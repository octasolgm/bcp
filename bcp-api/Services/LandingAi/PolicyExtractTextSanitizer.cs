using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Strip Landing parse artifacts (anchor tags, page footers, bare UUIDs) so section
/// previews look like extract/seed clause text, not raw markdown.
/// </summary>
public static partial class PolicyExtractTextSanitizer
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var t = AnchorTagRegex().Replace(text, " ");
        t = PageFooterRegex().Replace(t, " ");
        t = UuidOnlyLineRegex().Replace(t, " ");
        t = ExtraBlankLinesRegex().Replace(t, "\n\n");
        return t.Trim();
    }

    [GeneratedRegex(@"<a\s+id\s*=\s*['""][^'""]+['""]\s*(?:/>|>\s*</a>)", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex(@"P\s*a\s*g\s*e\s*\|\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex PageFooterRegex();

    [GeneratedRegex(@"(?im)^\s*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*$")]
    private static partial Regex UuidOnlyLineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();
}
