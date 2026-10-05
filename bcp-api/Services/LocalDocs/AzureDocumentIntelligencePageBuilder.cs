using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Azure prebuilt-layout returns accurate <c>pages</c> metadata for Office files, but markdown
/// <c>content</c> often omits <c>&lt;!-- PageBreak --&gt;</c> between pages. Rebuild per-page text
/// from <c>analyzeResult.pages[].lines</c> (and span slices into <c>content</c>) so Word gets the
/// same BCP_PDF_PAGE markers as PDF.
/// </summary>
internal static class AzureDocumentIntelligencePageBuilder
{
    private static readonly string[] TopLevelSpanElementNames =
    [
        "paragraphs",
        "lines",
        "words",
        "tables",
        "figures",
        "sections",
    ];

    internal static readonly Regex MarkdownPageBoundaryPattern = new(
        @"<!--\s*PageBreak\s*-->|<!\-\-\s*PageNumber=""\d+""\s*\-\->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<string>? BuildPerPageContent(JsonElement analyzeResult, string content, int pageCount)
    {
        if (pageCount <= 1 || string.IsNullOrEmpty(content))
            return null;

        var fromPages = BuildFromPagesArray(analyzeResult, content, pageCount);
        if (fromPages is { Count: > 1 })
            return fromPages;

        var rangesByPage = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var elementName in TopLevelSpanElementNames)
        {
            if (!analyzeResult.TryGetProperty(elementName, out var elements) || elements.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var element in elements.EnumerateArray())
                AddSpans(element, rangesByPage);
        }

        if (rangesByPage.Count == 0)
            return null;

        return SliceContentByPageRanges(content, pageCount, rangesByPage);
    }

    internal static IReadOnlyList<string> SplitMarkdownOnPageBoundaries(string markdown)
    {
        var parts = MarkdownPageBoundaryPattern.Split(markdown);
        return parts.Length > 0 ? parts : [markdown];
    }

    private static IReadOnlyList<string>? BuildFromPagesArray(JsonElement analyzeResult, string content, int pageCount)
    {
        if (!analyzeResult.TryGetProperty("pages", out var pagesEl) || pagesEl.ValueKind != JsonValueKind.Array)
            return null;
        if (pagesEl.GetArrayLength() <= 1)
            return null;

        var rangesByPage = new Dictionary<int, List<(int Start, int End)>>();
        foreach (var pageEl in pagesEl.EnumerateArray())
        {
            var pageNum = pageEl.TryGetProperty("pageNumber", out var pn) ? pn.GetInt32() : 0;
            if (pageNum <= 0)
                continue;

            if (pageEl.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                    AddSpans(line, rangesByPage, pageNum);
            }

            if (pageEl.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in words.EnumerateArray())
                    AddSpans(word, rangesByPage, pageNum);
            }
        }

        if (rangesByPage.Count > 0)
        {
            var sliced = SliceContentByPageRanges(content, pageCount, rangesByPage);
            if (sliced is { Count: > 1 } && sliced.Any(p => p.Length > 0))
                return sliced;
        }

        return BuildFromPageLineContent(pagesEl);
    }

    private static IReadOnlyList<string>? BuildFromPageLineContent(JsonElement pagesEl)
    {
        var pages = new List<string>();
        foreach (var pageEl in pagesEl.EnumerateArray().OrderBy(p =>
                     p.TryGetProperty("pageNumber", out var pn) ? pn.GetInt32() : 0))
        {
            var sb = new StringBuilder();
            if (pageEl.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    var text = line.TryGetProperty("content", out var c) ? c.GetString()?.Trim() : null;
                    if (string.IsNullOrEmpty(text))
                        continue;
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(text);
                }
            }
            pages.Add(sb.ToString());
        }

        return pages.Count > 1 && pages.Any(p => p.Length > 0) ? pages : null;
    }

    private static List<string> SliceContentByPageRanges(
        string content,
        int pageCount,
        Dictionary<int, List<(int Start, int End)>> rangesByPage)
    {
        var pages = new List<string>(pageCount);
        for (var page = 1; page <= pageCount; page++)
        {
            if (!rangesByPage.TryGetValue(page, out var ranges) || ranges.Count == 0)
            {
                pages.Add("");
                continue;
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = MergeRanges(ranges);
            var sb = new StringBuilder();
            foreach (var (start, end) in merged)
            {
                if (start < 0 || end > content.Length || start >= end)
                    continue;
                var slice = content[start..end].Trim();
                if (slice.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(slice);
            }
            pages.Add(sb.ToString());
        }
        return pages;
    }

    private static void AddSpans(
        JsonElement element,
        Dictionary<int, List<(int Start, int End)>> rangesByPage,
        int? forcePage = null)
    {
        var page = forcePage ?? 0;
        if (page <= 0)
        {
            if (!element.TryGetProperty("boundingRegions", out var regions) || regions.ValueKind != JsonValueKind.Array)
                return;
            page = regions[0].TryGetProperty("pageNumber", out var pageEl) ? pageEl.GetInt32() : 0;
            if (page <= 0)
                return;
        }

        if (!element.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
            return;

        if (!rangesByPage.TryGetValue(page, out var list))
        {
            list = [];
            rangesByPage[page] = list;
        }

        foreach (var span in spans.EnumerateArray())
        {
            if (!span.TryGetProperty("offset", out var offEl) || !span.TryGetProperty("length", out var lenEl))
                continue;
            var start = offEl.GetInt32();
            var length = lenEl.GetInt32();
            if (length <= 0)
                continue;
            list.Add((start, start + length));
        }
    }

    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> sorted)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var range in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var last = merged[^1];
            if (range.Start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, range.End));
            else
                merged.Add(range);
        }
        return merged;
    }
}
