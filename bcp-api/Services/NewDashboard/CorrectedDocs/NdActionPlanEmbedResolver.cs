using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
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
/// A clause whose document can't be identified either way is skipped — the corrected copy generator
/// still produces a copy of that document, it simply has nothing embedded for that clause instead of
/// a guess planted at the wrong place.
/// </summary>
public class NdActionPlanEmbedResolver(AppDbContext db, ILogger<NdActionPlanEmbedResolver> logger)
{
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
            if (gap == null) continue;

            var (clauseNo, clauseTitle) = ParseClause(point.PointSnapshot);
            if (string.IsNullOrWhiteSpace(clauseNo)) continue;

            if (!docsByPoint.TryGetValue(point.Id, out var pointDocs))
            {
                pointDocs = await ResolveDocsForPointAsync(
                    point, findingByPointId.GetValueOrDefault(point.Id), docIds, docTitles, ct);
                docsByPoint[point.Id] = pointDocs;
            }
            if (pointDocs.Count == 0) continue;

            var gapText = ExtractGapText(point, gap.GapIndex);
            var finding = ResolveFindingForPoint(point, findingByPointId, findings);
            var judgmentContext = ParseJudgmentContext(finding);
            var resolvedByName = plan.ResolvedBy.HasValue && namesById.TryGetValue(plan.ResolvedBy.Value, out var n)
                ? n
                : gap.ResolvedBy.HasValue && namesById.TryGetValue(gap.ResolvedBy.Value, out var gn) ? gn : null;
            var resolvedAt = plan.ResolvedAt ?? gap.ResolvedAt ?? plan.UpdatedAt;

            foreach (var (docId, page) in pointDocs)
            {
                targets.Add(new NdActionPlanEmbedTarget(
                    docId,
                    page,
                    AnchorText: TrimForAnchor(gapText) ?? clauseNo,
                    clauseNo,
                    clauseTitle,
                    gapText,
                    plan.ActionPlan,
                    plan.ResponsibilityLabel,
                    resolvedAt,
                    resolvedByName,
                    judgmentContext.GapDescription,
                    judgmentContext.PolicyExtracts));
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

    private async Task<Dictionary<Guid, int?>> ResolveDocsForPointAsync(
        NdAnalysisPoint point,
        NdRegulForwardFinding? finding,
        List<Guid> attachedDocIds,
        List<AttachedDocName> docTitles,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, int?>();

        if (finding?.RetrievalJson is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var matchLists = new[] { "fusedMatches", "matches", "bm25Matches" };
                // Only the strongest few ranked matches decide which document(s) a clause embeds
                // into — these lists commonly carry 30-60 candidates apiece, and scanning all of
                // them (as this used to) means every document attached to the run eventually shows
                // up somewhere in every clause's list, so nearly everything "resolved" to every
                // document instead of the one or two it actually came from.
                const int topRankedMatches = 3;
                foreach (var listName in matchLists)
                {
                    if (!root.TryGetProperty(listName, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var m in arr.EnumerateArray().Take(topRankedMatches))
                    {
                        if (!TryGetGuidProperty(m, "sourceDocumentId", "SourceDocumentId", out var docId)
                            || !attachedDocIds.Contains(docId)) continue;
                        int? page = TryGetIntProperty(m, "sourcePage", "SourcePage", out var pageNum)
                            ? pageNum
                            : null;
                        if (!result.ContainsKey(docId)) result[docId] = page;
                    }
                    if (result.Count > 0) break; // fused (best-ranked) list wins if it named anything
                }
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "Could not parse RetrievalJson for point {PointId}", point.Id);
            }
        }

        if (result.Count > 0) return result;

        RegulJudgmentResult? judgment = null;
        if (finding?.ResultJson is { Length: > 0 } resultJson)
        {
            try
            {
                judgment = NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>(resultJson);
            }
            catch
            {
                // No usable judgment JSON — fall through to other heuristics.
            }
        }

        // Fallback: match the judgment's own document_reference text against the attached titles, then
        // locate its cited quote in that document's own parsed markdown for a real page number.
        if (judgment != null)
        {
            var reference = judgment.DocumentReference ?? "";
            foreach (var d in docTitles)
            {
                var title = d.Title ?? "";
                var fileName = d.OriginalFileName ?? "";
                if (!NameMatchesReference(reference, title) && !NameMatchesReference(reference, fileName)) continue;

                var page = await TryResolvePageFromJudgmentQuoteAsync(d.Id, judgment, ct);
                result[d.Id] = page;
                break;
            }
        }

        if (result.Count > 0) return result;

        // Policy quote appears in exactly one attached document's parsed text — use that doc even when
        // document_reference was empty or did not match a title.
        if (judgment != null)
        {
            var quote = judgment.PolicyExtract.FirstOrDefault(q => !string.IsNullOrWhiteSpace(q));
            if (!string.IsNullOrWhiteSpace(quote))
            {
                foreach (var d in docTitles)
                {
                    var markdown = await LoadParsedMarkdownAsync(d.Id, ct);
                    if (string.IsNullOrWhiteSpace(markdown)) continue;
                    if (!QuoteAppearsInMarkdown(markdown, quote)) continue;
                    result[d.Id] = PolicyPageResolver.ResolveQuoteLocation(markdown, quote).Page;
                    break;
                }
            }
        }

        if (result.Count > 0) return result;

        // Single internal document on the run — embed resolved actions there rather than dropping them
        // when retrieval and judgment metadata could not name a document.
        if (attachedDocIds.Count == 1)
        {
            var onlyId = attachedDocIds[0];
            int? page = null;
            if (judgment != null)
                page = await TryResolvePageFromJudgmentQuoteAsync(onlyId, judgment, ct);
            result[onlyId] = page;
        }

        return result;
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

    private async Task<string?> LoadParsedMarkdownAsync(Guid docId, CancellationToken ct) =>
        await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => e.StoredDocumentId == docId && e.MarkdownText != null)
            .OrderByDescending(e => e.ParsedAt)
            .Select(e => e.MarkdownText)
            .FirstOrDefaultAsync(ct);

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
