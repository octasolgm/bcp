using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Services.NewDashboard.CorrectedDocs;

/// <summary>
/// Turns a run's resolved action plans into concrete embed targets: which internal document, and where
/// in it, each action plan's note belongs. The embed unit is the action plan, not the gap — a gap with
/// two action plans where only one is resolved still gets that one embedded; it does not wait for every
/// action plan on the gap to be resolved (that "gap closed" rollup is a separate concept, owned by
/// <c>NdGapStatusResolver</c>, and this class does not need or check it).
///
/// Two resolution paths, best first:
///  - Hybrid (V5) clauses store a per-clause retrieval preview (RetrievalJson) with the exact
///    document id and page the judgment's evidence came from — used directly, no guessing.
///  - Everything else (V3/V4, or a V5 clause judged before retrieval was recorded) falls back to
///    matching the judgment's own document_reference text against the run's attached document
///    titles, then locating the cited quote in that document's parsed markdown the same way the
///    live gap-analysis page grounds a citation (PolicyPageResolver).
/// When nothing else names a document, resolved actions for that clause embed into the run's first
/// selected internal document so finalize still produces a marked-up copy rather than a byte-identical
/// placeholder.
/// </summary>
public class NdActionPlanEmbedResolver(
    AppDbContext db,
    LandingAiCacheRepository parseCache,
    ILogger<NdActionPlanEmbedResolver> logger)
{
    public sealed record EmbedDiagnostics(
        int ResolvedActionPlanCount,
        int EmbedTargetCount,
        IReadOnlyList<EmbedDocSummary> Documents);

    public sealed record EmbedDocSummary(Guid DocumentId, string? Title, int TargetCount);

    public async Task<EmbedDiagnostics> DescribeForRunAsync(Guid runId, CancellationToken ct)
    {
        var resolvedCount = await db.NdAnalysisActionPlans.AsNoTracking()
            .CountAsync(p => p.AnalysisRunId == runId && p.Status == ActionPlanStatuses.Resolved, ct);
        var jobs = await ResolveForRunAsync(runId, ct);
        var titles = await db.StoredDocuments.AsNoTracking()
            .Where(d => jobs.Select(j => j.StoredDocumentId).Contains(d.Id))
            .Select(d => new { d.Id, d.Title })
            .ToListAsync(ct);
        var titleById = titles.ToDictionary(t => t.Id, t => t.Title);
        return new EmbedDiagnostics(
            resolvedCount,
            jobs.Sum(j => j.Targets.Count),
            jobs.Select(j => new EmbedDocSummary(
                j.StoredDocumentId,
                titleById.GetValueOrDefault(j.StoredDocumentId),
                j.Targets.Count)).ToList());
    }

    public async Task<IReadOnlyList<NdActionPlanEmbedJob>> ResolveForRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return [];

        var docIds = ParseGuidList(run.SelectedInternalDocIds);
        if (docIds.Count == 0) return [];

        // Every resolved action plan in the run, regardless of whether its gap has other, still-pending
        // action plans — each one is embedded on its own.
        var resolvedPlans = await db.NdAnalysisActionPlans.AsNoTracking()
            .Where(p => p.AnalysisRunId == runId && p.Status == ActionPlanStatuses.Resolved)
            .ToListAsync(ct);
        if (resolvedPlans.Count == 0) return [];

        var pointIds = resolvedPlans.Select(p => p.AnalysisPointId).Distinct().ToList();

        // All gaps on those points (not just resolved ones) — needed for gap text/context even when the
        // gap itself is still open because a sibling action plan hasn't been resolved yet.
        var gaps = await db.NdAnalysisGaps.AsNoTracking()
            .Where(g => g.AnalysisRunId == runId && pointIds.Contains(g.AnalysisPointId))
            .ToListAsync(ct);

        var points = await db.NdAnalysisPoints.AsNoTracking()
            .Where(p => pointIds.Contains(p.Id))
            .ToListAsync(ct);
        var pointById = points.ToDictionary(p => p.Id);

        var findings = await db.NdRegulForwardFindings.AsNoTracking()
            .Where(f => f.AnalysisRunId == runId && f.AnalysisPointId != null && pointIds.Contains(f.AnalysisPointId!.Value))
            .ToListAsync(ct);
        var findingByPointId = findings
            .Where(f => f.AnalysisPointId.HasValue)
            .ToDictionary(f => f.AnalysisPointId!.Value);

        var docTitles = await db.StoredDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new AttachedDocName(d.Id, d.Title, d.OriginalFileName))
            .ToListAsync(ct);

        var resolverIds = resolvedPlans.Select(p => p.ResolvedBy).Concat(gaps.Select(g => g.ResolvedBy))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var namesById = resolverIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.NdProfiles.AsNoTracking()
                .Where(p => resolverIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.FullName ?? "", ct);

        var gapByKey = gaps.ToDictionary(g => (g.AnalysisPointId, g.GapIndex));
        var targets = new List<NdActionPlanEmbedTarget>();

        // A clause with several matched sections in RetrievalJson can point at more than one attached
        // document (e.g. the manual for the main obligation, a procedures doc for one sub-point) — this
        // groups its resolved actions by whichever attached document each of its top matches names,
        // instead of collapsing the whole clause onto a single "best" match.
        var docsByPoint = new Dictionary<Guid, Dictionary<Guid, int?>>();

        foreach (var plan in resolvedPlans)
        {
            if (!pointById.TryGetValue(plan.AnalysisPointId, out var point)) continue;
            if (!gapByKey.TryGetValue((plan.AnalysisPointId, plan.GapIndex), out var gap)
                && !gapByKey.TryGetValue((plan.AnalysisPointId, 0), out gap))
            {
                gap = gaps.FirstOrDefault(g => g.AnalysisPointId == plan.AnalysisPointId);
            }

            var finding = ResolveFindingForPoint(point, findingByPointId, findings);
            var (clauseNo, clauseTitle) = ParseClause(point.PointSnapshot);
            if (string.IsNullOrWhiteSpace(clauseNo))
                clauseNo = finding?.ClauseNo?.Trim().TrimStart('§') ?? "";
            if (string.IsNullOrWhiteSpace(clauseNo)) continue;

            if (!docsByPoint.TryGetValue(point.Id, out var pointDocs))
            {
                pointDocs = await ResolveDocsForPointAsync(
                    point, finding, docIds, docTitles, ct);
                docsByPoint[point.Id] = pointDocs;
            }
            var planDocs = await NarrowToDocumentNamedInActionAsync(pointDocs, plan.ActionPlan ?? "", docTitles, ct);
            if (planDocs.Count == 0) continue;

            var gapIndexForText = gap?.GapIndex ?? plan.GapIndex;
            var gapText = ExtractGapText(point, gapIndexForText);
            if (string.IsNullOrWhiteSpace(gapText))
                gapText = plan.ActionPlan ?? "Gap addressed";
            var judgmentContext = ParseJudgmentContext(finding);
            var resolvedByName = plan.ResolvedBy.HasValue && namesById.TryGetValue(plan.ResolvedBy.Value, out var n)
                ? n
                : gap?.ResolvedBy is Guid gapResolver && namesById.TryGetValue(gapResolver, out var gn) ? gn : null;
            var resolvedAt = plan.ResolvedAt ?? gap?.ResolvedAt ?? plan.UpdatedAt;

            foreach (var (docId, clausePage) in planDocs)
            {
                // The section the action amends ("Section 7.7", "Definitions section") is where the note
                // belongs; the clause's evidence page is only the fallback.
                var page = await FindSectionPageAsync(docId, plan.ActionPlan ?? "", ct) ?? clausePage;
                targets.Add(new NdActionPlanEmbedTarget(
                    docId,
                    page,
                    AnchorText: TrimForAnchor(gapText) ?? clauseNo,
                    clauseNo,
                    clauseTitle,
                    gapText,
                    plan.ActionPlan ?? "",
                    plan.ResponsibilityLabel,
                    resolvedAt,
                    resolvedByName,
                    judgmentContext.GapDescription,
                    judgmentContext.PolicyExtracts,
                    GapIndex: gapIndexForText));
            }
        }

        return targets
            .GroupBy(t => t.StoredDocumentId)
            .Select(g => new NdActionPlanEmbedJob(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>Which attached document(s) a clause's evidence came from, and the page in each — see
    /// the class doc for the two resolution paths.</summary>
    private sealed record AttachedDocName(Guid Id, string? Title, string? OriginalFileName);

    /// <summary>
    /// The attached document(s) a clause's resolved actions belong in, with the page in each. Best
    /// evidence first: the documents the judgment itself cited (document_reference), then documents that
    /// contain its verbatim policy quotes, then the single best retrieval match. A note is never dropped
    /// into a document the clause has no evidence in; an unplaceable action is reported instead.
    /// </summary>
    private async Task<Dictionary<Guid, int?>> ResolveDocsForPointAsync(
        NdAnalysisPoint point,
        NdRegulForwardFinding? finding,
        List<Guid> attachedDocIds,
        List<AttachedDocName> docTitles,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, int?>();

        RegulJudgmentResult? judgment = null;
        if (finding?.ResultJson is { Length: > 0 } resultJson)
        {
            try { judgment = NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>(resultJson); }
            catch { /* no usable judgment JSON */ }
        }

        // 1. Documents the judgment cited, with the page from the citation label ("Doc — 7.7 p.37").
        if (judgment != null && !string.IsNullOrWhiteSpace(judgment.DocumentReference))
        {
            var segments = judgment.DocumentReference
                .Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                foreach (var d in docTitles)
                {
                    if (!NameMatchesReference(segment, d.Title ?? "") && !NameMatchesReference(segment, d.OriginalFileName ?? ""))
                        continue;
                    var pageMatch = System.Text.RegularExpressions.Regex.Match(segment, @"p\.\s*(\d+)");
                    int? page = pageMatch.Success ? int.Parse(pageMatch.Groups[1].Value) : null;
                    if (!result.ContainsKey(d.Id)) result[d.Id] = page;
                    else result[d.Id] ??= page;
                    break;
                }
            }
        }
        if (result.Count > 0) return result;

        // 2. Documents whose parsed text contains one of the judgment's verbatim policy quotes.
        if (judgment != null)
        {
            foreach (var quote in judgment.PolicyExtract.Where(q => !string.IsNullOrWhiteSpace(q)).Take(5))
            {
                foreach (var d in docTitles)
                {
                    if (result.ContainsKey(d.Id)) continue;
                    var markdown = await LoadParsedMarkdownAsync(d.Id, ct);
                    if (string.IsNullOrWhiteSpace(markdown) || !QuoteAppearsInMarkdown(markdown, quote)) continue;
                    result[d.Id] = PolicyPageResolver.ResolveQuoteLocation(markdown, quote).Page;
                }
            }
        }
        if (result.Count > 0) return result;

        // 3. The clause's single best retrieval match (V5 RetrievalJson).
        if (finding?.RetrievalJson is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var listName in new[] { "fusedMatches", "matches", "bm25Matches" })
                {
                    if (!doc.RootElement.TryGetProperty(listName, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var m in arr.EnumerateArray())
                    {
                        if (!TryGetGuidProperty(m, "sourceDocumentId", "SourceDocumentId", out var docId)
                            || !attachedDocIds.Contains(docId)) continue;
                        result[docId] = TryGetIntProperty(m, "sourcePage", "SourcePage", out var pageNum) ? pageNum : null;
                        return result;
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Could not parse RetrievalJson for point {PointId}", point.Id);
            }
        }

        // 4. A run with one internal document: that document.
        if (attachedDocIds.Count == 1)
        {
            var onlyId = attachedDocIds[0];
            result[onlyId] = judgment != null ? await TryResolvePageFromJudgmentQuoteAsync(onlyId, judgment, ct) : null;
            return result;
        }

        logger.LogWarning(
            "Finalize embed: no evidence document found for clause point {PointId}; its resolved actions are not embedded",
            point.Id);
        return result;
    }

    /// <summary>When an action names the document it amends ("Amend the AML Manual ...", "Amend the
    /// Customer and Nationality Risk Ranking Methodology ..."), keep only that document: matched against
    /// the file name and against the opening of the document's own text, where its real title sits.
    /// Otherwise every evidence document of the clause.</summary>
    private async Task<Dictionary<Guid, int?>> NarrowToDocumentNamedInActionAsync(
        Dictionary<Guid, int?> candidates, string actionText, List<AttachedDocName> docTitles, CancellationToken ct)
    {
        if (candidates.Count <= 1 || string.IsNullOrWhiteSpace(actionText)) return candidates;
        var normAction = Normalize(actionText);
        var namedPhrase = System.Text.RegularExpressions.Regex.Match(
            actionText,
            @"^\s*\[?\d*\]?\s*(?:amend|update|revise|extend)\s+(?:the\s+)?(?<name>.+?)(?:'s|\s*\(|\s+to\s+include|\s+section|,|:)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var normPhrase = namedPhrase.Success ? Normalize(namedPhrase.Groups["name"].Value) : "";

        var named = new Dictionary<Guid, int?>();
        foreach (var (docId, page) in candidates)
        {
            var d = docTitles.FirstOrDefault(t => t.Id == docId);
            if (d == null) continue;
            var core = CoreName(d.Title ?? d.OriginalFileName ?? "");
            var matches = core.Length >= 5 && normAction.Contains(core, StringComparison.Ordinal);
            if (!matches && normPhrase.Length >= 12)
            {
                var markdown = await LoadParsedMarkdownAsync(docId, ct);
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    var opening = Normalize(markdown.Length > 6000 ? markdown[..6000] : markdown);
                    matches = opening.Contains(normPhrase, StringComparison.Ordinal);
                }
            }
            if (matches) named[docId] = page;
        }
        return named.Count > 0 ? named : candidates;
    }

    private static readonly System.Text.RegularExpressions.Regex SectionNumberInAction = new(
        @"\b(?:section|rule|paragraph|clause)\s+(?<num>\d+(?:\.\d+)*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex SectionNameInAction = new(
        @"^\s*\[?\d*\]?\s*(?:amend|update|revise|extend)\s+(?:the\s+)?(?:[\w\s'\.-]{0,40}?'s\s+)?(?<name>[A-Za-z][A-Za-z /&-]{2,50}?)\s+section\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Table-of-contents lines ("7.7 Financial Transactions ........ 37") are not the section.</summary>
    private static readonly System.Text.RegularExpressions.Regex TocLine = new(
        @"(\.{3,}|…|\s\d{1,3}\s*$)");

    /// <summary>Page of the section an action names, from the document's own page-marked text; null when
    /// the action names no section or it can't be found.</summary>
    private async Task<int?> FindSectionPageAsync(Guid docId, string actionText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actionText)) return null;
        var numMatch = SectionNumberInAction.Match(actionText);
        var nameMatch = numMatch.Success ? null : SectionNameInAction.Match(actionText);
        var number = numMatch.Success ? numMatch.Groups["num"].Value : null;
        var name = nameMatch is { Success: true } ? nameMatch.Groups["name"].Value.Trim() : null;
        if (number == null && string.IsNullOrWhiteSpace(name)) return null;

        var markdown = await LoadParsedMarkdownAsync(docId, ct);
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        foreach (var (page, text) in PolicyPageResolver.SplitMarkdownIntoPageSegments(markdown))
        {
            foreach (var raw in text.Split('\n'))
            {
                var isHeading = raw.TrimStart().StartsWith('#');
                var line = raw.Trim().TrimStart('#', '*', ' ').TrimEnd('*', ' ');
                if (line.Length == 0 || line.Length > 120 || TocLine.IsMatch(line)) continue;
                if (number != null)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, $@"^{System.Text.RegularExpressions.Regex.Escape(number)}(\.|\s|$)"))
                        return page;
                }
                else if ((isHeading || line.Length <= 60)
                         && line.Contains(name!, StringComparison.OrdinalIgnoreCase))
                {
                    return page;
                }
            }
        }
        return null;
    }

    /// <summary>"Internal A M L M a n u a l 290626 azure (1).pdf" → "amlmanual".</summary>
    internal static string CoreName(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), @"\.(pdf|docx?|xlsx?)$", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\b(internal|azure|copy|final|draft|v\d+)\b", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\d+", " ");
        return Normalize(t);
    }

    private static NdRegulForwardFinding? ResolveFindingForPoint(
        NdAnalysisPoint point,
        Dictionary<Guid, NdRegulForwardFinding> findingByPointId,
        List<NdRegulForwardFinding> findings)
    {
        if (findingByPointId.TryGetValue(point.Id, out var linked)) return linked;
        var (clauseNo, _) = ParseClause(point.PointSnapshot);
        if (string.IsNullOrWhiteSpace(clauseNo)) return null;
        return findings.FirstOrDefault(f =>
            string.Equals(f.ClauseNo?.TrimStart('§'), clauseNo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.ClauseNo, clauseNo, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int?> TryResolvePageFromJudgmentQuoteAsync(
        Guid docId, RegulJudgmentResult judgment, CancellationToken ct)
    {
        var quote = judgment.PolicyExtract.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q));
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var markdown = await LoadParsedMarkdownAsync(docId, ct);
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        return PolicyPageResolver.ResolveQuoteLocation(markdown, quote).Page;
    }

    private async Task<string?> LoadParsedMarkdownAsync(Guid docId, CancellationToken ct)
    {
        var fromLocal = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => e.StoredDocumentId == docId && e.MarkdownText != null)
            .OrderByDescending(e => e.ParsedAt)
            .Select(e => e.MarkdownText)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(fromLocal)) return fromLocal;

        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId, ct);
        if (doc == null) return null;

        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);
        var row = await parseCache.GetParseCacheAsync(cacheKey, ct);
        if (string.IsNullOrWhiteSpace(row?.Markdown) && !string.IsNullOrWhiteSpace(doc.FileHash))
            row = await parseCache.GetParseCacheAsync(doc.FileHash.Trim(), ct);
        return row?.Markdown;
    }

    private static bool QuoteAppearsInMarkdown(string markdown, string quote)
    {
        if (markdown.Contains(quote.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var compactMd = Normalize(markdown);
        var compactQuote = Normalize(quote);
        return compactQuote.Length >= 12 && compactMd.Contains(compactQuote, StringComparison.Ordinal);
    }

    private static bool TryGetGuidProperty(JsonElement el, string camel, string pascal, out Guid id)
    {
        id = Guid.Empty;
        if (el.TryGetProperty(camel, out var prop) || el.TryGetProperty(pascal, out prop))
            return Guid.TryParse(prop.GetString(), out id);
        return false;
    }

    private static bool TryGetIntProperty(JsonElement el, string camel, string pascal, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(camel, out var prop) && !el.TryGetProperty(pascal, out prop))
            return false;
        if (prop.ValueKind != JsonValueKind.Number) return false;
        value = prop.GetInt32();
        return true;
    }

    private static bool NameMatchesReference(string reference, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference)) return false;
        var normName = Normalize(name);
        var normRef = Normalize(reference);
        return normName.Length > 3 && normRef.Contains(normName, StringComparison.Ordinal);
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static (string? GapDescription, IReadOnlyList<string> PolicyExtracts) ParseJudgmentContext(
        NdRegulForwardFinding? finding)
    {
        if (finding?.ResultJson is not { Length: > 0 } json)
            return (null, []);

        try
        {
            var judgment = NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>(json);
            var extracts = judgment.PolicyExtract
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
            var gap = string.IsNullOrWhiteSpace(judgment.GapDescription) || judgment.GapDescription.Trim() == "N/A"
                ? null
                : judgment.GapDescription.Trim();
            return (gap, extracts);
        }
        catch
        {
            return (null, []);
        }
    }

    private static string? TrimForAnchor(string? gapText)
    {
        if (string.IsNullOrWhiteSpace(gapText)) return null;
        var t = gapText.Trim();
        return t.Length > 60 ? t[..60] : t;
    }

    /// <summary>The specific gap's own text within a clause's (possibly multi-gap) CAP block, falling
    /// back to the whole block when the numbered split doesn't line up (single-gap clauses).</summary>
    private static string ExtractGapText(NdAnalysisPoint point, int gapIndex)
    {
        var whole = point.OriginalAiActionPlan ?? point.FinalActionPlan ?? point.LandingAiActionPlan ?? "";
        if (gapIndex <= 0) return whole;
        var marker = $"({gapIndex})";
        var idx = whole.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return whole;
        var next = whole.IndexOf($"({gapIndex + 1})", idx, StringComparison.Ordinal);
        var slice = next > idx ? whole[(idx + marker.Length)..next] : whole[(idx + marker.Length)..];
        return slice.Trim();
    }

    private static (string ClauseNo, string? ClauseTitle) ParseClause(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return ("", null);
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var root = doc.RootElement;
            string? Get(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                }
                return null;
            }
            var clauseNo = Get("pointNumber", "clauseNo", "point_id") ?? "";
            var title = Get("pointTitle", "title");
            return (clauseNo.TrimStart('§'), title);
        }
        catch (JsonException)
        {
            return ("", null);
        }
    }

    private static List<Guid> ParseGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
