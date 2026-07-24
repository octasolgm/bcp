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
        var maxPage = ResolveMaxPage(segments, maxPageOverride);
        var preferLastMatch = LooksLikeNumberedClause(pointId);
        // Stored extract page hints are often printed/footer pages — ignore when markdown is available.
        var trustedAi = preferLastMatch ? null : SanitizeAiPageHint(aiPageHint, maxPage);
        var monolithic = IsMonolithicMarkdown(segments, maxPage);

        // Numbered clauses: refine page inside large ADE chunks (one marker per ~99 PDF pages).
        if (preferLastMatch && maxPage is > 10)
        {
            var byClause = ResolveNumberedClausePageRefined(segments, pointId, title, maxPage.Value);
            if (byClause is > 0) return byClause;
        }
        else if (preferLastMatch)
        {
            var byClause = FindSectionPageLast(markdown, segments, pointId);
            if (byClause.HasValue) return byClause;
            var byHeading = FindNumberedClauseHeadingPageLast(segments, pointId, title);
            if (byHeading.HasValue) return byHeading;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var byText = AcceptSegmentPage(
                FindInSegments(segments, text.Length > 160 ? text[..160] : text, preferLastMatch),
                segments,
                maxPage,
                monolithic);
            if (byText.HasValue) return byText;
        }

        if (!string.IsNullOrWhiteSpace(title) && title.Trim().Length >= 6)
        {
            var byTitle = AcceptSegmentPage(
                FindInSegments(segments, title.Trim(), preferLastMatch),
                segments,
                maxPage,
                monolithic);
            if (byTitle.HasValue) return byTitle;
        }

        if (!string.IsNullOrWhiteSpace(pointId) && !string.IsNullOrWhiteSpace(title))
        {
            var heading = $"{pointId.Trim()} {title.Trim()}";
            var byHeading = AcceptSegmentPage(
                FindInSegments(segments, heading.Length > 120 ? heading[..120] : heading, preferLastMatch),
                segments,
                maxPage,
                monolithic);
            if (byHeading.HasValue) return byHeading;
        }

        if (!monolithic)
        {
            foreach (var sectionKey in new[] { pointId, section, title }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var bySection = LooksLikeNumberedClause(sectionKey!)
                    ? FindSectionPageLast(markdown, segments, sectionKey!)
                    : FindSectionPage(markdown, segments, sectionKey!);
                if (bySection.HasValue) return bySection;
            }
        }

        if (maxPage is > 10 && !string.IsNullOrWhiteSpace(markdown))
        {
            foreach (var needle in new[] { CombineHeading(pointId, title), title, text }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var estimated = EstimatePageByMarkdownPosition(markdown, needle!, maxPage.Value);
                if (estimated is > 0)
                    return RefinePageGuess(estimated, pointId, maxPage);
            }
        }

        if (monolithic && trustedAi == 1)
            trustedAi = null;

        return RefinePageGuess(trustedAi, pointId, maxPage);
    }

    /// <summary>Spread numbered clauses across PDF length when ADE page data is missing (avoids everything on p. 1).</summary>
    public static int? EstimatePageFromPointNumber(string pointId, int totalPages)
    {
        if (totalPages <= 1) return null;
        var m = MajorSectionRegex().Match(pointId.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var major) || major < 1)
            return null;

        const double assumedMajorSections = 13.0;
        var page = (int)Math.Round(((major - 0.65) / assumedMajorSections) * totalPages);
        return Math.Clamp(page, 1, totalPages);
    }

    public static int? RefinePageGuess(int? page, string pointId, int? totalPages)
    {
        if (totalPages is not > 10 || string.IsNullOrWhiteSpace(pointId))
            return page;
        if (!LooksLikeNumberedClause(pointId) && !MajorSectionRegex().IsMatch(pointId.Trim()))
            return page;

        var estimated = EstimatePageFromPointNumber(pointId, totalPages.Value);
        if (estimated is not > 0) return page;
        if (page is null or 1) return estimated;
        if (page < estimated - 20) return estimated;
        return page;
    }

    private static int? ResolveMaxPage(IReadOnlyList<(int Page, string Text)> segments, int? maxPageOverride)
    {
        if (maxPageOverride is > 0) return maxPageOverride;
        return segments.Count > 0 ? segments.Max(s => s.Page) : null;
    }

    private static bool IsMonolithicMarkdown(IReadOnlyList<(int Page, string Text)> segments, int? maxPage) =>
        segments.Count == 1 && segments[0].Page == 1 && maxPage is > 10;

    /// <summary>Single-marker markdown maps all content to page 1 — ignore unless position estimate fails.</summary>
    private static int? AcceptSegmentPage(
        int? page,
        IReadOnlyList<(int Page, string Text)> segments,
        int? maxPage,
        bool monolithic)
    {
        if (page is not > 0) return null;
        if (monolithic && page == 1) return null;
        return page;
    }

    private static string? CombineHeading(string? pointId, string? title)
    {
        var id = pointId?.Trim();
        var t = title?.Trim();
        if (string.IsNullOrWhiteSpace(id)) return t;
        if (string.IsNullOrWhiteSpace(t)) return id;
        return $"{id} {t}";
    }

    /// <summary>When ADE markdown has no page markers, map match position to viewer page using document length.</summary>
    internal static int? EstimatePageByMarkdownPosition(string markdown, string needle, int totalPages)
    {
        if (totalPages <= 1 || string.IsNullOrWhiteSpace(needle)) return null;
        var hay = NormalizeForMatch(markdown);
        var n = NormalizeForMatch(needle);
        if (n.Length < 8) return null;
        var preferLast = PreferLastMarkdownMatch(needle);
        var idx = preferLast
            ? hay.LastIndexOf(n, StringComparison.Ordinal)
            : hay.IndexOf(n, StringComparison.Ordinal);
        if (idx < 0 && n.Length > 48)
        {
            var prefix = n[..48];
            idx = preferLast
                ? hay.LastIndexOf(prefix, StringComparison.Ordinal)
                : hay.IndexOf(prefix, StringComparison.Ordinal);
        }
        if (idx < 0) return null;
        var ratio = idx / (double)Math.Max(hay.Length, 1);
        return Math.Clamp((int)Math.Round(ratio * totalPages), 1, totalPages);
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

    private static int? FindInSegments(IReadOnlyList<(int Page, string Text)> segments, string quote, bool preferLast = false)
    {
        var needle = NormalizeForMatch(quote);
        if (needle.Length < 12) return null;

        int? MatchInSegment(string text)
        {
            var hay = NormalizeForMatch(text);
            if (hay.Contains(needle, StringComparison.Ordinal)) return 1;
            var prefix = needle.Length > 48 ? needle[..48] : needle;
            return hay.Contains(prefix, StringComparison.Ordinal) ? 1 : null;
        }

        if (preferLast)
        {
            int? last = null;
            foreach (var (page, text) in segments)
            {
                if (MatchInSegment(text) == 1) last = page;
            }

            return last;
        }

        foreach (var (page, text) in segments)
        {
            if (MatchInSegment(text) == 1) return page;
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

    /// <summary>TOC entries often repeat clause numbers on early pages — prefer the last occurrence.</summary>
    private static int? FindSectionPageLast(string markdown, IReadOnlyList<(int Page, string Text)> segments, string section)
    {
        var sectionKey = section.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(sectionKey)) return null;

        var escaped = Regex.Escape(sectionKey).Replace("\\.", "[.]");
        var sectionRegex = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase);

        int? last = null;
        foreach (var (page, text) in segments)
        {
            if (sectionRegex.IsMatch(text)) last = page;
        }

        return last;
    }

    /// <summary>
    /// Resolve numbered clause page inside chunked parse markdown (marker = chunk start, not every PDF page).
    /// </summary>
    private static int? ResolveNumberedClausePageRefined(
        IReadOnlyList<(int Page, string Text)> segments,
        string pointId,
        string? title,
        int maxPage)
    {
        var id = pointId.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(id)) return null;

        var escaped = Regex.Escape(id).Replace("\\.", "[.]");
        var idRegex = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase);

        int? bestPage = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var (page, text) = segments[i];
            var matches = idRegex.Matches(text);
            if (matches.Count == 0) continue;

            Match? chosen = null;
            foreach (Match m in matches)
            {
                if (IsLikelyClauseHeading(text, m, title))
                {
                    chosen = m;
                    break;
                }
            }

            chosen ??= matches[^1];

            var segEnd = SegmentEndPage(segments, i, maxPage);
            var isHeading = IsLikelyClauseHeading(text, chosen, title);
            var refined = PageFromIndexInSegment(page, segEnd, chosen.Index, text.Length, isHeading);
            if (bestPage is null || refined > bestPage)
                bestPage = refined;
        }

        if (bestPage is > 0) return bestPage;

        return FindNumberedClauseHeadingPageLast(segments, pointId, title);
    }

    private static int SegmentEndPage(IReadOnlyList<(int Page, string Text)> segments, int index, int maxPage)
    {
        if (index + 1 < segments.Count)
        {
            var next = segments[index + 1].Page;
            return next > segments[index].Page ? next - 1 : segments[index].Page;
        }

        return maxPage;
    }

    private static int PageFromIndexInSegment(
        int segStart,
        int segEnd,
        int matchIndex,
        int segmentTextLength,
        bool clauseHeading = false)
    {
        var span = Math.Max(1, segEnd - segStart + 1);
        if (segmentTextLength < 6000 || span <= 1)
            return segStart;

        var ratio = matchIndex / (double)Math.Max(segmentTextLength, 1);
        var offset = clauseHeading
            ? (int)Math.Ceiling(ratio * (span - 1))
            : (int)Math.Round(ratio * (span - 1));
        return Math.Clamp(segStart + offset, segStart, segEnd);
    }

    private static bool IsLikelyClauseHeading(string text, Match clauseIdMatch, string? title)
    {
        if (clauseIdMatch.Index > 0)
        {
            var prev = text[clauseIdMatch.Index - 1];
            if (prev != '\n' && prev != '\r' && prev != ' ' && prev != '\t' && prev != '#')
                return false;
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 6)
            return clauseIdMatch.Index < 120 || text.AsSpan(0, clauseIdMatch.Index).LastIndexOf('\n') >= clauseIdMatch.Index - 80;

        var after = text.AsSpan(clauseIdMatch.Index + clauseIdMatch.Length).TrimStart();
        var titlePrefix = title.Trim();
        if (titlePrefix.Length > 24) titlePrefix = titlePrefix[..24];
        return after.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Match clause heading at line start (e.g. "7.4 Identification of Suspicious Transactions").</summary>
    private static int? FindNumberedClauseHeadingPageLast(
        IReadOnlyList<(int Page, string Text)> segments,
        string pointId,
        string? title)
    {
        var id = Regex.Escape(pointId.Trim().TrimEnd('.'));
        var titleTrim = title?.Trim();
        Regex headingRegex;
        if (!string.IsNullOrWhiteSpace(titleTrim) && titleTrim.Length >= 6)
        {
            var titleWords = Regex.Escape(titleTrim.Length > 48 ? titleTrim[..48] : titleTrim);
            headingRegex = new Regex(
                $@"^[\s#>*-]*{id}\s+{titleWords}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
        else
        {
            headingRegex = new Regex(
                $@"^[\s#>*-]*{id}(?:\s|\.)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        int? last = null;
        foreach (var (page, text) in segments)
        {
            if (headingRegex.IsMatch(text)) last = page;
        }

        return last;
    }

    private static bool LooksLikeNumberedClause(string value) =>
        NumberedClauseRegex().IsMatch(value.Trim());

    private static bool PreferLastMarkdownMatch(string needle)
    {
        var t = needle.Trim();
        if (LooksLikeNumberedClause(t)) return true;
        return Regex.IsMatch(t, @"^\d+(\.\d+)+\s+", RegexOptions.CultureInvariant);
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

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex MajorSectionRegex();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex NumberedClauseRegex();
}
