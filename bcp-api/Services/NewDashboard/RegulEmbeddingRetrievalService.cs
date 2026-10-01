using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LocalDocs;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Hybrid pipeline Steps 1-6 (V5 / RegulPipelineHybrid engine only): expands each gov clause's
/// terms via the acronym + synonym dictionaries (Step 1, <see cref="DictionaryExpansionService"/>),
/// splits it into distinct obligations (Step 2, <see cref="SubObligationSplitter"/>), retrieves
/// candidate internal sections two independent ways per sub-obligation — keyword/lexical via BM25
/// (Step 3, <see cref="Bm25Scorer"/>) and semantic via the local ONNX embedding model (Step 4,
/// <see cref="LocalEmbeddingService"/>) — against the run's already-indexed internal sections,
/// merges the two sides into one ranked list (Step 5) and trims it (Step 6, both in
/// <see cref="HybridFusionSelector"/>). Persists everything onto
/// <see cref="Data.NewDashboard.Entities.NdRegulForwardFinding.RetrievalJson"/> before the LLM
/// judgment call runs — see docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md. This is the first
/// bridge between the local-docs indexing subsystem and the Regul analysis-run subsystem; the two
/// were never wired together before.
///
/// Both retrieval steps use a dynamic cutoff (<see cref="Bm25Scorer.SelectDynamic"/> for BM25,
/// a relative-score threshold for embeddings) instead of a fixed top-N — a clause with only a
/// handful of genuinely relevant sections returns only those; a clause with many strongly
/// relevant sections returns all of them. <see cref="HybridFusionSelector.SelectDynamic"/> applies
/// the same principle to the fused list.
///
/// Never blocks or fails the run: any error here is logged and the affected finding's
/// RetrievalJson stays null, forward judgment proceeds unaffected — same defensive pattern as
/// <see cref="DictionaryExpansionService.HarvestFromDocumentAsync"/>.
/// </summary>
public sealed class RegulEmbeddingRetrievalService(
    AppDbContext db,
    DictionaryExpansionService dictionary,
    LocalEmbeddingService embedder,
    ILogger<RegulEmbeddingRetrievalService> logger)
{
    private const int PreviewLength = 400;
    // Relative-threshold dynamic cutoff for embedding retrieval, mirroring Bm25Scorer.SelectDynamic
    // — keep any section whose similarity is within this fraction of the clause's own best match,
    // instead of a fixed top-N. A hard ceiling still applies so one very generic clause can't pull
    // in the entire corpus.
    private const double EmbeddingRelativeThreshold = 0.85;
    private const int EmbeddingMaxKeep = 300;

    // camelCase so the frontend's RetrievalJson consumer (NdPipelinePanelService) can rely on the
    // same casing convention as the rest of the API's JSON responses.
    private static readonly JsonSerializerOptions RetrievalJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public sealed record RetrievalMatch(
        Guid SectionId,
        string? ClauseNo,
        string TextPreview,
        Guid SourceDocumentId,
        string? SourceDocumentName,
        int? SourcePage,
        double Similarity,
        string? MatchedSubObligation = null);

    public sealed record Bm25Match(
        Guid SectionId,
        string? ClauseNo,
        string TextPreview,
        Guid SourceDocumentId,
        string? SourceDocumentName,
        int? SourcePage,
        double Score,
        string? MatchedSubObligation = null);

    public sealed record RetrievalPreview(
        IReadOnlyList<DictionaryExpansionService.ExpansionMatch> AcronymMatches,
        IReadOnlyList<DictionaryExpansionService.ExpansionMatch> SynonymMatches,
        IReadOnlyList<Bm25Match> Bm25Matches,
        IReadOnlyList<RetrievalMatch> Matches,
        // Step 2 — how this clause was broken down before retrieval. A single entry (equal to the
        // full clause text) means the splitter found no confident evidence of more than one
        // obligation, so the clause was searched as-is.
        IReadOnlyList<string> SubObligations,
        // Step 5 + 6 — Bm25Matches and Matches above fused into one ranked list and trimmed
        // (HybridFusionSelector). This is what Step 7 (build context) now uses; the two raw lists
        // above are kept for the pipeline panel's own BM25/embedding breakdown, not consumed
        // further downstream. Nullable/defaulted so a RetrievalJson row saved before this field
        // existed still deserializes cleanly.
        IReadOnlyList<HybridFusionSelector.FusedMatch>? FusedMatches = null);

    /// <summary>Same JSON shape as the clause's RetrievalJson, which the pipeline panel reads.</summary>
    public static string SerializePreview(RetrievalPreview preview) =>
        JsonSerializer.Serialize(preview, RetrievalJsonOptions);

    public async Task RunRetrievalAsync(NdAnalysisRun run, CancellationToken ct)
    {
        try
        {
            var internalDocIds = (JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [])
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .ToList();

            var corpusDocIds = internalDocIds.Distinct().ToList();

            if (corpusDocIds.Count == 0)
            {
                logger.LogInformation("Retrieval skipped for run {RunId}: no internal documents in corpus", run.Id);
                return;
            }

            // Step 3's corpus — full section text, loaded once per run (not per clause). BM25
            // scoring itself is plain in-memory term-frequency math, so this is the only DB round
            // trip it needs; everything downstream just re-scores the same corpus per clause.
            var corpus = await LoadCorpusAsync(corpusDocIds, ct);
            if (corpus == null)
            {
                logger.LogInformation(
                    "Retrieval skipped for run {RunId}: none of the {Count} corpus document(s) are indexed yet",
                    run.Id, corpusDocIds.Count);
                return;
            }

            var findings = await db.NdRegulForwardFindings
                .Where(f => f.AnalysisRunId == run.Id
                    && !f.ClauseNo.StartsWith(NdRegulReverseIntRows.IntClausePrefix))
                .ToListAsync(ct);

            var processed = 0;
            foreach (var finding in findings)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                if (string.IsNullOrWhiteSpace(finding.ClauseText)) continue;

                finding.RetrievalJson = JsonSerializer.Serialize(
                    await BuildPreviewAsync(corpus, finding.ClauseText, ct),
                    RetrievalJsonOptions);
                finding.UpdatedAt = DateTimeOffset.UtcNow;
                processed++;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Retrieval complete for run {RunId}: {Processed}/{Total} clause(s) processed against {ExtractionCount} indexed internal document(s)",
                run.Id, processed, findings.Count, corpus.ExtractionIds.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Retrieval failed for run {RunId} — forward judgment proceeds without retrieval preview", run.Id);
        }
    }

    /// <summary>Steps 1-6 for one clause against a loaded corpus — exactly what the analysis run stores
    /// on the clause's RetrievalJson.</summary>
    private async Task<RetrievalPreview> BuildPreviewAsync(LoadedCorpus corpus, string clauseText, CancellationToken ct)
    {
        // Step 2 — split this clause into its distinct obligations (free, local, regex — see
        // SubObligationSplitter). Each sub-obligation gets its own Step 1 expansion and Step 3/4
        // retrieval, so a bundled clause searched as one blended query can't wash out a section
        // that only matches one of its several obligations.
        var subObligations = SubObligationSplitter.Split(clauseText);
        var hits = new QueryHits();
        foreach (var subText in subObligations)
        {
            await ScoreQueryAsync(
                corpus,
                subText,
                subObligations.Count > 1 ? Truncate(subText, PreviewLength) : null,
                hits,
                ct);
        }

        // Step 5 — fuse the two lists into one ranked list (0.4 BM25 + 0.6 embedding, each
        // normalized against this clause's own best score on that side); Step 6 — trim it with the
        // same dynamic-cutoff principle as Steps 3/4, not a fixed count.
        var fused = HybridFusionSelector.Fuse(hits.Bm25BySection.Values.ToList(), hits.EmbeddingBySection.Values.ToList());
        return new RetrievalPreview(
            hits.Acronyms.Values.ToList(),
            hits.Synonyms.Values.ToList(),
            hits.Bm25BySection.Values.ToList(),
            hits.EmbeddingBySection.Values.ToList(),
            subObligations,
            HybridFusionSelector.SelectDynamic(fused));
    }

    /// <summary>The analysis run's own retrieval for one clause, over any set of indexed documents
    /// (e.g. the run's internal documents plus newly uploaded gap evidence). Returned rather than
    /// saved, so the clause's original retrieval record stays as it was.</summary>
    public async Task<RetrievalPreview?> RetrievePreviewForClauseAsync(
        IReadOnlyCollection<Guid> corpusDocIds,
        string clauseText,
        CancellationToken ct)
    {
        if (corpusDocIds.Count == 0 || string.IsNullOrWhiteSpace(clauseText)) return null;
        var corpus = await LoadCorpusAsync(corpusDocIds, ct);
        return corpus == null ? null : await BuildPreviewAsync(corpus, clauseText, ct);
    }

    public sealed record EvidenceSection(
        Guid SectionId,
        string? ClauseNo,
        string Text,
        Guid SourceDocumentId,
        string? SourceDocumentName,
        int? SourcePage,
        double Score);

    /// <summary>
    /// Same Steps 1-6 hybrid search, restricted to the given documents and returned instead of
    /// written to RetrievalJson — gap evidence re-checks must leave the clause's original retrieval
    /// record from the analysis run untouched.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceSection>> RetrieveFromDocumentsAsync(
        IReadOnlyCollection<Guid> corpusDocIds,
        IReadOnlyList<string> queryTexts,
        int maxSections,
        CancellationToken ct)
    {
        if (corpusDocIds.Count == 0) return [];
        var corpus = await LoadCorpusAsync(corpusDocIds, ct);
        if (corpus == null) return [];

        var hits = new QueryHits();
        foreach (var query in queryTexts.Where(q => !string.IsNullOrWhiteSpace(q)))
        {
            foreach (var subText in SubObligationSplitter.Split(query))
                await ScoreQueryAsync(corpus, subText, null, hits, ct);
        }

        var fused = HybridFusionSelector.SelectDynamic(
            HybridFusionSelector.Fuse(hits.Bm25BySection.Values.ToList(), hits.EmbeddingBySection.Values.ToList()));

        return fused
            .Take(maxSections)
            .Select(m => new EvidenceSection(
                m.SectionId,
                m.ClauseNo,
                corpus.SectionById.TryGetValue(m.SectionId, out var section) ? section.ClauseText : m.TextPreview,
                m.SourceDocumentId,
                m.SourceDocumentName,
                m.SourcePage,
                Math.Round(m.FusedScore, 4)))
            .ToList();
    }

    private sealed record CorpusSection(Guid Id, Guid ExtractionId, string? ClauseNo, string ClauseText, int? SourcePage);

    private sealed record LoadedCorpus(
        List<Guid> ExtractionIds,
        Dictionary<Guid, CorpusSection> SectionById,
        Bm25Scorer.Corpus Bm25Corpus,
        Dictionary<Guid, string?> DocNameById,
        Dictionary<Guid, Guid> StoredDocIdByExtractionId);

    private sealed class QueryHits
    {
        public Dictionary<string, DictionaryExpansionService.ExpansionMatch> Acronyms { get; } = new();
        public Dictionary<string, DictionaryExpansionService.ExpansionMatch> Synonyms { get; } = new();
        public Dictionary<Guid, Bm25Match> Bm25BySection { get; } = new();
        public Dictionary<Guid, RetrievalMatch> EmbeddingBySection { get; } = new();
    }

    private async Task<LoadedCorpus?> LoadCorpusAsync(IReadOnlyCollection<Guid> corpusDocIds, CancellationToken ct)
    {
        var docIds = corpusDocIds.ToList();
        var extractions = await db.NdLocalDocumentExtractions
            .AsNoTracking()
            .Where(e => docIds.Contains(e.StoredDocumentId) && e.IndexStatus == "indexed")
            .ToListAsync(ct);
        if (extractions.Count == 0) return null;

        var extractionIds = extractions.Select(e => e.Id).ToList();
        var docNameById = await db.StoredDocuments
            .AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => (string?)(d.Title ?? d.OriginalFileName), ct);

        var sections = await db.NdLocalDocumentExtractionSections
            .AsNoTracking()
            .Where(s => extractionIds.Contains(s.ExtractionId))
            .Select(s => new { s.Id, s.ExtractionId, s.ClauseNo, s.ClauseText, s.SourcePage })
            .ToListAsync(ct);

        return new LoadedCorpus(
            extractionIds,
            sections.ToDictionary(
                s => s.Id,
                s => new CorpusSection(s.Id, s.ExtractionId, s.ClauseNo, s.ClauseText, s.SourcePage)),
            Bm25Scorer.BuildCorpus(sections.Select(s => (s.Id, s.ClauseText)).ToList()),
            docNameById,
            extractions.ToDictionary(e => e.Id, e => e.StoredDocumentId));
    }

    /// <summary>Steps 1, 3 and 4 for one query text, merged into <paramref name="hits"/> keeping each
    /// section's best score.</summary>
    private async Task ScoreQueryAsync(
        LoadedCorpus corpus,
        string subText,
        string? subObligationLabel,
        QueryHits hits,
        CancellationToken ct)
    {
        var expanded = await dictionary.ExpandQueryDetailedAsync(subText, ct);
        foreach (var m in expanded.AcronymMatches) hits.Acronyms[$"{m.EntryId}:{m.MatchedText}"] = m;
        foreach (var m in expanded.SynonymMatches) hits.Synonyms[$"{m.EntryId}:{m.MatchedText}"] = m;
        var queryText = expanded.AllTerms.Count == 0
            ? subText
            : subText + " " + string.Join(" ", expanded.AllTerms);

        // Step 3 — BM25, dynamic cutoff (see Bm25Scorer.SelectDynamic doc comment): not a fixed
        // count, only however many sections actually clear the relevance bar for this query.
        var bm25Selected = Bm25Scorer.SelectDynamic(Bm25Scorer.Score(corpus.Bm25Corpus, queryText));
        foreach (var r in bm25Selected)
        {
            var s = corpus.SectionById[r.SectionId];
            if (hits.Bm25BySection.TryGetValue(s.Id, out var existing) && existing.Score >= r.Score) continue;
            var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(s.ExtractionId);
            hits.Bm25BySection[s.Id] = new Bm25Match(
                s.Id,
                s.ClauseNo,
                Truncate(s.ClauseText, PreviewLength),
                storedDocId,
                corpus.DocNameById.GetValueOrDefault(storedDocId),
                s.SourcePage,
                Math.Round(r.Score, 4),
                subObligationLabel);
        }

        // Step 4 — embedding retrieval, same dynamic-cutoff principle: pull a generous candidate
        // set from pgvector by distance, then keep only those within EmbeddingRelativeThreshold of
        // this query's own best match.
        var queryVector = new Vector(embedder.Embed(queryText));
        var extractionIds = corpus.ExtractionIds;
        var candidates = await db.NdLocalDocumentExtractionSections
            .AsNoTracking()
            .Where(s => extractionIds.Contains(s.ExtractionId) && s.Embedding != null)
            .OrderBy(s => s.Embedding!.CosineDistance(queryVector))
            .Take(EmbeddingMaxKeep)
            .Select(s => new
            {
                s.Id,
                s.ExtractionId,
                s.ClauseNo,
                s.ClauseText,
                s.SourcePage,
                Distance = s.Embedding!.CosineDistance(queryVector),
            })
            .ToListAsync(ct);

        var bestSimilarity = candidates.Count == 0 ? 0 : 1 - candidates.Min(c => c.Distance);
        var similarityCutoff = bestSimilarity * EmbeddingRelativeThreshold;
        foreach (var x in candidates.Select(m => new { m, Similarity = 1 - m.Distance }))
        {
            if (x.Similarity < similarityCutoff) continue;
            if (hits.EmbeddingBySection.TryGetValue(x.m.Id, out var existing) && existing.Similarity >= x.Similarity) continue;
            var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(x.m.ExtractionId);
            hits.EmbeddingBySection[x.m.Id] = new RetrievalMatch(
                x.m.Id,
                x.m.ClauseNo,
                Truncate(x.m.ClauseText, PreviewLength),
                storedDocId,
                corpus.DocNameById.GetValueOrDefault(storedDocId),
                x.m.SourcePage,
                Math.Round(x.Similarity, 4),
                subObligationLabel);
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "…" : text;
}
