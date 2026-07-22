using System.Text;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>
/// Resolves 1-based PDF viewer page numbers from parsed internal-doc markdown.
/// Landing AI often cites printed/footer page numbers; we locate the quote or section in markdown instead.
/// </summary>
public static partial class PolicyPageResolver
{
    public const string PageMarkerPrefix = "<!-- BCP_PDF_PAGE:";

    public static int? Resolve(string? markdown, string? outputResponse)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(outputResponse))
            return null;

        var (quote, section, aiPage) = ParseCitation(outputResponse);
        var segments = SplitByPageMarkers(markdown);

        if (!string.IsNullOrWhiteSpace(quote))
        {
            var byQuote = FindInSegments(segments, quote);
            if (byQuote.HasValue) return byQuote;
        }

        if (!string.IsNullOrWhiteSpace(section))
        {
            var bySection = FindSectionPage(markdown, segments, section);
            if (bySection.HasValue) return bySection;
        }

        return aiPage is > 0 ? aiPage : null;
    }

    /// <summary>Resolve PDF viewer page for a gov/regulation requirement point.</summary>
    public static int? ResolveGovPointPage(
        string? markdown,
        string pointId,
        string? section,
        string? title,
        string text,
        int? aiPageHint,
        int? maxPageOverride = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return SanitizeAiPageHint(aiPageHint, maxPageOverride);

        var segments = SplitByPageMarkers(markdown);
        var maxPage = maxPageOverride ?? (segments.Count > 0 ? segments.Max(s => s.Page) : null);
        var trustedAi = SanitizeAiPageHint(aiPageHint, maxPage);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var byText = FindInSegments(segments, text.Length > 160 ? text[..160] : text);
            if (byText.HasValue) return byText;
        }

        foreach (var sectionKey in new[] { pointId, section, title }.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var bySection = FindSectionPage(markdown, segments, sectionKey!);
            if (bySection.HasValue) return bySection;
        }

        return trustedAi;
    }

    public static int? EstimatePageCount(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var segments = SplitByPageMarkers(markdown);
        return segments.Count > 0 ? segments.Max(s => s.Page) : null;
    }

    private static int? SanitizeAiPageHint(int? aiPageHint, int? maxPage)
    {
        if (aiPageHint is not > 0) return null;
        if (maxPage is > 0 && aiPageHint > maxPage) return null;
        return aiPageHint;
    }

    public static string RewriteCitationPage(string output, int page)
    {
        if (string.IsNullOrWhiteSpace(output) || page <= 0) return output;
        return PageCitationRegex().Replace(output, $"Page {page}");
    }

    /// <summary>Inject page markers when rebuilding markdown from Landing AI parse JSON.</summary>
    public static string InjectPageMarkersFromParseJson(string rawJson, string fallbackMarkdown)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("splits", out var splits) && splits.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var fromSplits = BuildFromSplits(splits);
                if (!string.IsNullOrWhiteSpace(fromSplits)) return fromSplits;
            }

            if (root.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var fromChunks = BuildFromChunks(chunks);
                if (!string.IsNullOrWhiteSpace(fromChunks)) return fromChunks;
            }
        }
        catch
        {
            // fall through
        }

        return fallbackMarkdown;
    }

    private static string? BuildFromSplits(System.Text.Json.JsonElement splits)
    {
        var sb = new StringBuilder();
        var wrote = false;
        foreach (var split in splits.EnumerateArray())
        {
            var page = ReadSplitPage(split);
            if (page <= 0) continue;

            sb.AppendLine($"{PageMarkerPrefix}{page} -->");
            if (split.TryGetProperty("markdown", out var md) && md.ValueKind == System.Text.Json.JsonValueKind.String)
                sb.AppendLine(md.GetString());
            sb.AppendLine();
            wrote = true;
        }

        return wrote ? sb.ToString().Trim() : null;
    }

    private static string? BuildFromChunks(System.Text.Json.JsonElement chunks)
    {
        var sb = new StringBuilder();
        var lastPage = 0;
        var wrote = false;

        foreach (var chunk in chunks.EnumerateArray())
        {
            if (!chunk.TryGetProperty("markdown", out var mdProp) || mdProp.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;
            var md = mdProp.GetString();
            if (string.IsNullOrWhiteSpace(md)) continue;

            var page = ReadChunkPage(chunk);
            if (page > 0 && page != lastPage)
            {
                sb.AppendLine($"{PageMarkerPrefix}{page} -->");
                lastPage = page;
                wrote = true;
            }

            sb.AppendLine(md);
            sb.AppendLine();
        }

        return wrote ? sb.ToString().Trim() : null;
    }

    private static int ReadSplitPage(System.Text.Json.JsonElement split)
    {
        if (split.TryGetProperty("pages", out var pages) && pages.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var p in pages.EnumerateArray())
            {
                if (p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetInt32(out var n))
                    return ZeroToOneIndexed(n);
            }
        }

        if (split.TryGetProperty("identifier", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var id = idProp.GetString() ?? "";
            var m = SplitPageIdRegex().Match(id);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var zeroIdx))
                return zeroIdx + 1;
        }

        return 0;
    }

    private static int ReadChunkPage(System.Text.Json.JsonElement chunk)
    {
        if (!chunk.TryGetProperty("grounding", out var grounding)) return 0;
        if (grounding.TryGetProperty("page", out var pageProp) && pageProp.TryGetInt32(out var page))
            return ZeroToOneIndexed(page);
        return 0;
    }

    private static int ZeroToOneIndexed(int page) => page <= 0 ? page + 1 : page;

    private static List<(int Page, string Text)> SplitByPageMarkers(string markdown)
    {
        var segments = new List<(int Page, string Text)>();
        var pattern = Regex.Escape(PageMarkerPrefix) + @"(\d+)\s*-->";
        var matches = Regex.Matches(markdown, pattern);
        if (matches.Count > 0)
        {
            for (var i = 0; i < matches.Count; i++)
            {
                if (!int.TryParse(matches[i].Groups[1].Value, out var page) || page <= 0) continue;
                var start = matches[i].Index + matches[i].Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
                var text = markdown[start..end];
                segments.Add((page, text));
            }

            return segments;
        }

        // Landing AI markdown often separates pages with horizontal rules.
        var parts = markdown.Split("\n---\n", StringSplitOptions.None);
        if (parts.Length > 1)
        {
            for (var i = 0; i < parts.Length; i++)
                segments.Add((i + 1, parts[i]));
            return segments;
        }

        segments.Add((1, markdown));
        return segments;
    }

    private static int? FindInSegments(IReadOnlyList<(int Page, string Text)> segments, string quote)
    {
        var needle = NormalizeForMatch(quote);
        if (needle.Length < 12) return null;

        foreach (var (page, text) in segments)
        {
            if (NormalizeForMatch(text).Contains(needle, StringComparison.Ordinal))
                return page;
        }

        // Shorter prefix match for truncated quotes
        var prefix = needle.Length > 48 ? needle[..48] : needle;
        foreach (var (page, text) in segments)
        {
            if (NormalizeForMatch(text).Contains(prefix, StringComparison.Ordinal))
                return page;
        }

        return null;
    }

    private static int? FindSectionPage(string markdown, IReadOnlyList<(int Page, string Text)> segments, string section)
    {
        var sectionKey = section.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(sectionKey)) return null;

        var escaped = Regex.Escape(sectionKey).Replace("\\.", "[.]");
        var sectionRegex = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase);

        foreach (var (page, text) in segments)
        {
            if (sectionRegex.IsMatch(text)) return page;
        }

        return null;
    }

    private static (string? Quote, string? Section, int? AiPage) ParseCitation(string output)
    {
        var trimmed = output.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return (null, null, null);

        string? quote = null;
        var quoteMatch = QuoteRegex().Match(trimmed);
        if (quoteMatch.Success) quote = quoteMatch.Groups[1].Value.Trim();

        string? section = null;
        var sectionMatch = SectionRegex().Match(trimmed);
        if (sectionMatch.Success) section = sectionMatch.Groups[1].Value.Trim().TrimEnd(':');

        int? aiPage = null;
        var pageMatch = PageCitationRegex().Match(trimmed);
        if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out var p) && p > 0)
            aiPage = p;

        return (quote, section, aiPage);
    }

    private static string NormalizeForMatch(string text) =>
        WhitespaceRegex().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"Page\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageCitationRegex();

    [GeneratedRegex(@"Section\s+([^:'""]+?)(?=\s*:\s*['""]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"['""]([^'""]+)['""]")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"page_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPageIdRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
