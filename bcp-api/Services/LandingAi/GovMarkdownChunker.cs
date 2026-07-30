using System.Text;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>Splits parsed regulation markdown into smaller chunks for Landing AI extract.</summary>
public static class GovMarkdownChunker
{
    private static readonly Regex PageMarkerRegex = new(
        Regex.Escape(PolicyPageResolver.PageMarkerPrefix) + @"(\d+)\s*-->",
        RegexOptions.Compiled);

    public const int DefaultPagesPerChunk = 22;
    public const int DefaultPageOverlap = 3;
    public const int DefaultMaxChars = 55_000;

    public static IReadOnlyList<string> SplitForExtract(
        string markdown,
        int pagesPerChunk = DefaultPagesPerChunk,
        int maxChars = DefaultMaxChars,
        int pageOverlap = DefaultPageOverlap)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        if (markdown.Length <= maxChars)
        {
            var pages = PolicyPageResolver.EstimatePageCount(markdown);
            if (pages is null || pages <= pagesPerChunk) return [markdown];
        }

        var byPage = SplitByPageMarkers(markdown);
        if (byPage.Count <= 1)
            return SplitByCharLimit(markdown, maxChars);

        var chunks = new List<string>();
        var start = 0;
        while (start < byPage.Count)
        {
            var end = Math.Min(start + pagesPerChunk, byPage.Count);
            var sb = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                var (page, text) = byPage[i];
                if (sb.Length > 0) sb.Append('\n');
                sb.Append($"<!-- BCP_PDF_PAGE:{page} -->\n{text}");
            }
            chunks.Add(sb.ToString());

            if (end >= byPage.Count) break;
            start = Math.Max(start + 1, end - pageOverlap);
        }

        return chunks.Count > 0 ? chunks : [markdown];
    }

    private static List<(int Page, string Text)> SplitByPageMarkers(string markdown)
    {
        var list = new List<(int Page, string Text)>();
        var matches = PageMarkerRegex.Matches(markdown);
        if (matches.Count == 0) return list;

        for (var i = 0; i < matches.Count; i++)
        {
            if (!int.TryParse(matches[i].Groups[1].Value, out var page)) continue;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            list.Add((page, markdown[start..end]));
        }

        return list;
    }

    private static IReadOnlyList<string> SplitByCharLimit(string markdown, int maxChars)
    {
        if (markdown.Length <= maxChars) return [markdown];
        var chunks = new List<string>();
        var overlap = Math.Min(DefaultMaxChars / 10, 8_000);
        var offset = 0;
        while (offset < markdown.Length)
        {
            var len = Math.Min(maxChars, markdown.Length - offset);
            chunks.Add(markdown.Substring(offset, len));
            if (offset + len >= markdown.Length) break;
            offset += Math.Max(1, len - overlap);
        }
        return chunks;
    }
}
