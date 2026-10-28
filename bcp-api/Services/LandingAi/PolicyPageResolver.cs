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
            var located = ResolveQuoteLocation(markdown, quote);
            var byQuote = FindInSegments(segments, quote, preferLast: true);
            if (byQuote.HasValue && (located.Page is null || byQuote > located.Page))
                return byQuote;
            if (located.Page is > 0) return located.Page;
            if (byQuote.HasValue) return byQuote;
        }

        if (!string.IsNullOrWhiteSpace(section))
        {
            var bySection = FindSectionPageLast(markdown, segments, section);
            if (bySection.HasValue) return bySection;
        }

        return aiPage is > 0 ? aiPage : null;
    }

    /// <summary>
    /// Locate the PDF page and nearest numbered section heading for a verbatim policy quote in parse markdown.
    /// Used to ground Regul judgment document_reference (avoids wrong section_ref from retrieval chunks).
    /// </summary>
    public static (int? Page, string? Section) ResolveQuoteLocation(string? markdown, string quote)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(quote))
            return (null, null);

        var rawIdx = FindLastQuoteIndex(markdown, quote);
        if (rawIdx < 0) return (null, null);

        var segments = SplitByPageMarkers(markdown);
        var maxPage = ResolveMaxPage(segments, null);
        var page = FindPageForMarkdownIndex(markdown, segments, rawIdx, maxPage);
        var section = FindSectionHeadingBeforeIndex(markdown, rawIdx);
        if (page is null or 1 && section is not null)
        {
            var bySection = FindSectionPageLast(markdown, segments, section);
            if (bySection is > 0) page = bySection;
        }

        return (page, section);
    }

    /// <summary>Pre-split markdown once for batch page resolution (e.g. 800+ policy sections).</summary>
    public readonly record struct PolicyPageResolveContext(
        string Markdown,
        IReadOnlyList<(int Page, string Text)> Segments,
        int? MaxPage,
        string NormalizedHay);

    public static PolicyPageResolveContext CreateResolveContext(string markdown, int? maxPageOverride = null)
    {
        var segments = SplitByPageMarkers(markdown);
        return new PolicyPageResolveContext(
            markdown,
            segments,
            ResolveMaxPage(segments, maxPageOverride),
            NormalizeForMatch(markdown));
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

        var ctx = CreateResolveContext(markdown, maxPageOverride);
        return ResolveGovPointPage(ctx, pointId, section, title, text, aiPageHint);
    }

    /// <summary>Resolve page using a pre-built context (avoids re-splitting markdown per section).</summary>
    public static int? ResolveGovPointPage(
        PolicyPageResolveContext ctx,
        string pointId,
        string? section,
        string? title,
        string text,
        int? aiPageHint)
    {
        var markdown = ctx.Markdown;
        var segments = ctx.Segments;
        var maxPage = ctx.MaxPage;
        var preferLastMatch = LooksLikeNumberedClause(pointId);
        // Stored extract page hints are often printed/footer pages — ignore when markdown is available.
        var trustedAi = preferLastMatch ? null : SanitizeAiPageHint(aiPageHint, maxPage);
        var monolithic = IsMonolithicMarkdown(segments, maxPage);
        var distinctiveText = text?.Trim();
        var hasDistinctiveText = distinctiveText is { Length: >= 48 };
        var sparseMarkers = segments.Count > 1;
        var singleChunkCoversDoc = segments.Count == 1
            && maxPage is > 10
            && segments[0].Text.Length > 10_000;

        // Numbered clauses: refine page inside large ADE chunks (one marker per ~99 PDF pages).
        if (preferLastMatch && maxPage is > 10)
        {
            var byClause = ResolveNumberedClausePageRefined(
                segments,
                pointId,
                title,
                maxPage.Value,
                hasDistinctiveText ? distinctiveText : null);
            if (byClause is > 0) return byClause;
        }
        else if (preferLastMatch)
        {
            var byClause = FindSectionPageLast(markdown, segments, pointId);
            if (byClause.HasValue) return byClause;
            var byHeading = FindNumberedClauseHeadingPageLast(segments, pointId, title);
            if (byHeading.HasValue) return byHeading;
        }

        // Multi-chunk markdown: body text pinpoints page better than a TOC clause repeat in an earlier chunk.
        if (hasDistinctiveText && sparseMarkers && !singleChunkCoversDoc)
        {
            var textNeedle = distinctiveText!.Length > 200 ? distinctiveText[..200] : distinctiveText;
            var byText = AcceptSegmentPage(
                FindInSegments(segments, textNeedle, preferLast: true, maxPage),
                segments,
                maxPage,
                monolithic);
            if (byText.HasValue) return byText;
        }

        if (!string.IsNullOrWhiteSpace(text) && !hasDistinctiveText)
        {
            var byText = AcceptSegmentPage(
                FindInSegments(segments, text.Length > 160 ? text[..160] : text, preferLastMatch, maxPage),
                segments,
                maxPage,
                monolithic);
            if (byText.HasValue) return byText;
        }
        else if (hasDistinctiveText)
        {
            // Shorter prefix when OCR/extract truncates wording slightly.
            var prefix = distinctiveText!.Length > 80 ? distinctiveText[..80] : distinctiveText;
            var byPrefix = AcceptSegmentPage(
                FindInSegments(segments, prefix, preferLast: true, maxPage),
                segments,
                maxPage,
                monolithic);
            if (byPrefix.HasValue) return byPrefix;
        }

        if (!string.IsNullOrWhiteSpace(title) && title.Trim().Length >= 6)
        {
            var byTitle = AcceptSegmentPage(
                FindInSegments(segments, title.Trim(), preferLastMatch, maxPage),
                segments,
                maxPage,
                monolithic);
            if (byTitle.HasValue) return byTitle;
        }

        if (!string.IsNullOrWhiteSpace(pointId) && !string.IsNullOrWhiteSpace(title))
        {
            var heading = $"{pointId.Trim()} {title.Trim()}";
            var byHeading = AcceptSegmentPage(
                FindInSegments(segments, heading.Length > 120 ? heading[..120] : heading, preferLastMatch, maxPage),
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

    /// <summary>Split parsed internal-doc markdown into PDF viewer page segments (BCP markers, ---, or monolithic).</summary>
    public static IReadOnlyList<(int Page, string Text)> SplitMarkdownIntoPageSegments(string markdown) =>
        SplitByPageMarkers(markdown);

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
            if (!split.TryGetProperty("markdown", out var md) || md.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;
            var markdown = md.GetString();
            if (string.IsNullOrWhiteSpace(markdown)) continue;

            var pages = ReadSplitPages(split);
            if (pages.Count == 0) continue;

            if (pages.Count == 1)
            {
                sb.AppendLine($"{PageMarkerPrefix}{pages[0]} -->");
                sb.AppendLine(markdown);
            }
            else
            {
                AppendProportionalPageMarkers(sb, markdown, pages);
            }

            sb.AppendLine();
            wrote = true;
        }

        return wrote ? sb.ToString().Trim() : null;
    }

    private static void AppendProportionalPageMarkers(StringBuilder sb, string markdown, IReadOnlyList<int> pages)
    {
        var partLen = Math.Max(1, markdown.Length / pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            sb.AppendLine($"{PageMarkerPrefix}{pages[i]} -->");
            var start = i * partLen;
            var len = i == pages.Count - 1 ? markdown.Length - start : partLen;
            sb.AppendLine(markdown.Substring(start, len));
        }
    }

    private static List<int> ReadSplitPages(System.Text.Json.JsonElement split)
    {
        var pages = new List<int>();
        if (split.TryGetProperty("pages", out var pagesProp) && pagesProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var p in pagesProp.EnumerateArray())
            {
                if (p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetInt32(out var n))
                    pages.Add(n);
            }
        }

        if (pages.Count > 0)
            return ToOneIndexedPages(pages);

        var single = ReadSplitPageFromIdentifier(split);
        if (single > 0) pages.Add(single);

        return pages;
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
        var pages = ReadSplitPages(split);
        return pages.Count > 0 ? pages[0] : ReadSplitPageFromIdentifier(split);
    }

    private static int ReadSplitPageFromIdentifier(System.Text.Json.JsonElement split)
    {
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
            return page <= 0 ? page + 1 : page;
        return 0;
    }

    /// <summary>
    /// ADE parse v1 <c>pages</c> are 0-based when 0 is present. A mixed/1-based list is left as viewer pages.
    /// </summary>
    private static List<int> ToOneIndexedPages(List<int> pages)
    {
        if (pages.Count == 0) return pages;
        if (pages.Min() > 0)
            return pages;

        return pages.Select(p => p + 1).Where(p => p > 0).ToList();
    }

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

    private static int FindLastQuoteIndex(string markdown, string quote)
    {
        var trimmed = quote.Trim();
        if (trimmed.Length < 8) return -1;

        // Prefer verbatim match in raw markdown (normalized index mapping drifts across segments).
        var rawIdx = markdown.LastIndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        if (rawIdx >= 0) return rawIdx;

        var needle = NormalizeForMatch(quote);
        if (needle.Length < 8) return -1;

        var hay = NormalizeForMatch(markdown);
        var idx = hay.LastIndexOf(needle, StringComparison.Ordinal);
        if (idx < 0 && needle.Length >= 40)
            idx = hay.LastIndexOf(needle[..40], StringComparison.Ordinal);
        if (idx < 0) return -1;

        var ratio = idx / (double)Math.Max(hay.Length, 1);
        return Math.Clamp((int)Math.Round(ratio * markdown.Length), 0, Math.Max(0, markdown.Length - 1));
    }

    private static int? FindPageForMarkdownIndex(
        string markdown,
        IReadOnlyList<(int Page, string Text)> segments,
        int rawIndex,
        int? maxPage = null)
    {
        if (segments.Count == 0) return null;
        maxPage ??= ResolveMaxPage(segments, null);
        var offset = 0;
        for (var segIndex = 0; segIndex < segments.Count; segIndex++)
        {
            var (page, text) = segments[segIndex];
            var segmentStart = markdown.IndexOf(text, offset, StringComparison.Ordinal);
            if (segmentStart < 0) continue;
            var segmentEnd = segmentStart + text.Length;
            if (rawIndex >= segmentStart && rawIndex <= segmentEnd)
            {
                if (maxPage is > 10 && text.Length >= 6000)
                {
                    var segEnd = SegmentEndPage(segments, segIndex, maxPage.Value);
                    var indexInSegment = rawIndex - segmentStart;
                    return PageFromIndexInSegment(page, segEnd, indexInSegment, text.Length);
                }

                return page;
            }

            offset = segmentEnd;
        }

        if (maxPage is > 10)
        {
            var estimated = EstimatePageByMarkdownPosition(markdown, markdown[Math.Min(rawIndex, markdown.Length - 1)..Math.Min(rawIndex + 80, markdown.Length)], maxPage.Value);
            if (estimated is > 0) return estimated;
        }

        return segments.LastOrDefault(s => markdown.IndexOf(s.Text, StringComparison.Ordinal) <= rawIndex).Page;
    }

    internal static string? FindSectionHeadingBeforeIndex(string markdown, int quoteIndex)
    {
        if (quoteIndex <= 0) return null;
        var before = markdown[..quoteIndex];
        var window = before.Length > 5000 ? before[^5000..] : before;
        string? last = null;
        foreach (Match m in SectionHeadingBeforeQuoteRegex().Matches(window))
            last = m.Groups[1].Value.Trim();
        return SanitizeSectionLabel(last);
    }

    private static int? FindInSegments(
        IReadOnlyList<(int Page, string Text)> segments,
        string quote,
        bool preferLast = false,
        int? maxPage = null)
    {
        var needle = NormalizeForMatch(quote);
        if (needle.Length < 12) return null;
        maxPage ??= ResolveMaxPage(segments, null);

        int? MatchInSegment(int segIndex, int page, string text)
        {
            var hay = NormalizeForMatch(text);
            var idx = preferLast ? hay.LastIndexOf(needle, StringComparison.Ordinal) : hay.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0 && needle.Length > 48)
            {
                var prefix = needle[..48];
                idx = preferLast
                    ? hay.LastIndexOf(prefix, StringComparison.Ordinal)
                    : hay.IndexOf(prefix, StringComparison.Ordinal);
            }

            if (idx < 0) return null;

            if (maxPage is > 10)
            {
                var segEnd = SegmentEndPage(segments, segIndex, maxPage.Value);
                return PageFromIndexInSegment(page, segEnd, idx, text.Length);
            }

            return page;
        }

        if (preferLast)
        {
            int? last = null;
            for (var i = 0; i < segments.Count; i++)
            {
                var (page, text) = segments[i];
                var matched = MatchInSegment(i, page, text);
                if (matched.HasValue) last = matched;
            }

            return last;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var (page, text) = segments[i];
            var matched = MatchInSegment(i, page, text);
            if (matched.HasValue) return matched;
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
    /// Body text wins over later running headers (those look like headings and used to shift refs +1).
    /// </summary>
    private static int? ResolveNumberedClausePageRefined(
        IReadOnlyList<(int Page, string Text)> segments,
        string pointId,
        string? title,
        int maxPage,
        string? sectionText = null)
    {
        var id = pointId.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(id)) return null;

        var escaped = Regex.Escape(id).Replace("\\.", "[.]");
        var idRegex = new Regex($@"\b{escaped}\b", RegexOptions.IgnoreCase);
        var (anchorSegment, anchorIndex) = FindSectionTextAnchor(segments, sectionText);
        if (anchorSegment >= 0)
        {
            var (page, text) = segments[anchorSegment];
            var segEnd = SegmentEndPage(segments, anchorSegment, maxPage);
            Match? headingBeforeBody = null;
            foreach (Match m in idRegex.Matches(text))
            {
                if (IsTocLine(text, m) || !IsLikelyClauseHeading(text, m, title))
                    continue;
                if (anchorIndex >= 0 && m.Index > anchorIndex + 120)
                    continue;
                headingBeforeBody = m;
            }

            var idx = headingBeforeBody?.Index ?? Math.Max(anchorIndex, 0);
            return PageFromIndexInSegment(page, segEnd, idx, text.Length);
        }
        int? firstHeadingPage = null;
        int? lastMentionPage = null;

        for (var i = 0; i < segments.Count; i++)
        {
            var (page, text) = segments[i];
            var matches = idRegex.Matches(text);
            if (matches.Count == 0) continue;

            Match? chosenHeading = null;
            foreach (Match m in matches)
            {
                if (IsTocLine(text, m) || !IsLikelyClauseHeading(text, m, title))
                    continue;
                chosenHeading = m;
                break;
            }

            var segEnd = SegmentEndPage(segments, i, maxPage);
            if (chosenHeading is not null)
            {
                var refined = PageFromIndexInSegment(page, segEnd, chosenHeading.Index, text.Length);
                if (firstHeadingPage is null)
                    firstHeadingPage = refined;
                continue;
            }

            lastMentionPage = PageFromIndexInSegment(page, segEnd, matches[^1].Index, text.Length);
        }

        if (firstHeadingPage is > 0) return firstHeadingPage;
        if (lastMentionPage is > 0) return lastMentionPage;

        return FindNumberedClauseHeadingPageLast(segments, pointId, title);
    }

    private static (int SegmentIndex, int Index) FindSectionTextAnchor(
        IReadOnlyList<(int Page, string Text)> segments,
        string? sectionText)
    {
        if (string.IsNullOrWhiteSpace(sectionText) || sectionText.Trim().Length < 40)
            return (-1, -1);

        var needle = NormalizeForMatch(sectionText.Length > 120 ? sectionText[..120] : sectionText);
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            var hay = NormalizeForMatch(segments[i].Text);
            var idx = hay.LastIndexOf(needle, StringComparison.Ordinal);
            if (idx < 0 && needle.Length > 48)
                idx = hay.LastIndexOf(needle[..48], StringComparison.Ordinal);
            if (idx >= 0) return (i, idx);
        }

        return (-1, -1);
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
        int segmentTextLength)
    {
        var span = Math.Max(1, segEnd - segStart + 1);
        if (segmentTextLength < 6000 || span <= 1)
            return segStart;

        var ratio = matchIndex / (double)Math.Max(segmentTextLength, 1);
        // Round (not Ceiling): Ceiling systematically landed one viewer page late.
        var offset = (int)Math.Round(ratio * (span - 1), MidpointRounding.AwayFromZero);
        return Math.Clamp(segStart + offset, segStart, segEnd);
    }

    /// <summary>TOC lines like "6.2 Title .......... 52" must not beat the real heading.</summary>
    private static bool IsTocLine(string text, Match clauseIdMatch)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, clauseIdMatch.Index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = text.IndexOf('\n', clauseIdMatch.Index);
        var line = (lineEnd < 0 ? text[lineStart..] : text[lineStart..lineEnd]).Trim();
        return TocLeaderRegex().IsMatch(line);
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

        int? first = null;
        foreach (var (page, text) in segments)
        {
            foreach (Match m in headingRegex.Matches(text))
            {
                if (IsTocLine(text, m)) continue;
                first ??= page;
                break;
            }

            if (first is > 0) break;
        }

        return first;
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
        var tightSection = TightSectionRegex().Match(trimmed);
        if (tightSection.Success)
            section = tightSection.Groups[1].Value.Trim();
        else
        {
            var sectionMatch = SectionRegex().Match(trimmed);
            if (sectionMatch.Success) section = sectionMatch.Groups[1].Value.Trim().TrimEnd(':');
        }

        section = SanitizeSectionLabel(section);

        int? aiPage = null;
        var pageMatch = PageCitationRegex().Match(trimmed);
        if (pageMatch.Success && int.TryParse(pageMatch.Groups[1].Value, out var p) && p > 0)
            aiPage = p;

        return (quote, section, aiPage);
    }

    /// <summary>
    /// Strip UUID / regulation-title leaks and trailing ")." junk from AI Section labels
    /// so quote/section page lookup uses a real policy section id (e.g. 7.2).
    /// </summary>
    internal static string? SanitizeSectionLabel(string? section)
    {
        if (string.IsNullOrWhiteSpace(section)) return null;
        var s = section.Trim().TrimEnd(')', '.', ',', ':', ';', ']').Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;

        if (UuidRegex().IsMatch(s))
        {
            var withoutUuid = UuidRegex().Replace(s, " ");
            withoutUuid = WhitespaceRegex().Replace(withoutUuid, " ").Trim();
            var numbered = NumberedSectionPrefixRegex().Match(withoutUuid);
            if (!numbered.Success) numbered = NumberedSectionAnywhereRegex().Match(s);
            return numbered.Success ? numbered.Groups[1].Value : null;
        }

        var prefix = NumberedSectionPrefixRegex().Match(s);
        if (prefix.Success)
        {
            var rest = s[prefix.Length..].Trim();
            if (string.IsNullOrEmpty(rest) || rest.Length > 24) return prefix.Groups[1].Value;
            if (char.IsUpper(rest[0]) && rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
                return prefix.Groups[1].Value;
            return prefix.Groups[1].Value;
        }

        return s.Length > 48 ? s[..45].TrimEnd() + "…" : s;
    }

    private static string NormalizeForMatch(string text) =>
        WhitespaceRegex().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"Page\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PageCitationRegex();

    [GeneratedRegex(@"Section\s+([^:'""]+?)(?=\s*:\s*['""]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"Section\s+(\d+(?:\.\d+)*|[A-Za-z][\w./-]{0,40})(?=\s*[,:).]|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex TightSectionRegex();

    [GeneratedRegex(@"['""]([^'""]+)['""]")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"page_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SplitPageIdRegex();

    [GeneratedRegex(@"\.{3,}|…|\s{2,}\d{1,4}\s*$")]
    private static partial Regex TocLeaderRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex MajorSectionRegex();

    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*(?:-[a-z](?:\d+)?)?|\d+-\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedClauseRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^(\d+(?:\.\d+)*)\b")]
    private static partial Regex NumberedSectionPrefixRegex();

    [GeneratedRegex(@"\b(\d+(?:\.\d+)*)\b")]
    private static partial Regex NumberedSectionAnywhereRegex();

    [GeneratedRegex(
        @"(?m)^[\s#>*-]*(?:(?:Rule|Section)\s+)?(\d+(?:\.\d+)*(?:-[a-z]\d*)?|\d+(?:-\d+)+)\s+\S",
        RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeadingBeforeQuoteRegex();
}
