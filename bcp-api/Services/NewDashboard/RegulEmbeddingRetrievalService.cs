using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.Llm;
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
    PassageEmbeddingService passageEmbeddings,
    NdPassageIndexService passageIndex,
    RegulWorkflowLlmSettingsService settings,
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
        string? MatchedSubObligation = null,
        // Pipeline v2: "expanded" when the best score came from the expanded-wording search, null for the
        // clause's own wording.
        string? MatchedVia = null);

    public sealed record Bm25Match(
        Guid SectionId,
        string? ClauseNo,
        string TextPreview,
        Guid SourceDocumentId,
        string? SourceDocumentName,
        int? SourcePage,
        double Score,
        string? MatchedSubObligation = null,
        string? MatchedVia = null);

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
        IReadOnlyList<HybridFusionSelector.FusedMatch>? FusedMatches = null,
        // Retrieval pipeline version that produced this record (NdRegulPipelineVersions). Records saved
        // before versions existed have no value and are v1.
        int PipelineVersion = NdRegulPipelineVersions.V1,
        // Pipeline v2: the reworded sub-obligation texts (acronyms/synonyms swapped) that were also searched.
        IReadOnlyList<string>? ExpandedQueries = null,
        // Time Steps 1-6 took for this clause (not set on records saved before it was added).
        long? ElapsedMs = null,
        // v4+: the embedding model of the passages and queries ("local:bge-micro-v2", "azure-openai:<deployment>").
        string? EmbeddingModel = null);

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

            var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
            if (pipelineVersion >= NdRegulPipelineVersions.V4Passages)
                await PrepareSearchPassagesAsync(run, corpusDocIds, ct);

            // Step 3's corpus — full section text (v4+: passages and their vectors), loaded once per run (not per
            // clause). Everything downstream just re-scores the same corpus per clause.
            var corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
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

            run.RegulPipelineVersion = pipelineVersion;
            logger.LogInformation("Regul retrieval for run {RunId} uses pipeline {Version}", run.Id,
                NdRegulPipelineVersions.Label(pipelineVersion));

            var embeddingModel = pipelineVersion >= NdRegulPipelineVersions.V4Passages
                ? await passageEmbeddings.ModelNameAsync(ct)
                : null;
            var processed = 0;
            foreach (var finding in findings)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException();
                if (string.IsNullOrWhiteSpace(finding.ClauseText)) continue;

                var clauseTimer = System.Diagnostics.Stopwatch.StartNew();
                var preview = await BuildPreviewAsync(corpus, finding.ClauseText, pipelineVersion, ct);
                preview = preview with { ElapsedMs = clauseTimer.ElapsedMilliseconds, EmbeddingModel = embeddingModel };
                finding.RetrievalJson = JsonSerializer.Serialize(preview, RetrievalJsonOptions);
                logger.LogInformation(
                    "Regul Steps 1-6 ({Pipeline}) for clause {ClauseNo} in {Ms} ms: step1 acronyms={Acronyms} synonyms={Synonyms} expanded queries={Expanded}; step2 sub-obligations={Subs}; " +
                    "step3 bm25={Bm25} ({Bm25Expanded} via expanded wording); step4 embedding={Embedding} ({EmbeddingExpanded} via expanded wording); step5+6 fused and selected={Fused} — {Selected}",
                    NdRegulPipelineVersions.Label(pipelineVersion),
                    finding.ClauseNo,
                    clauseTimer.ElapsedMilliseconds,
                    preview.AcronymMatches.Count,
                    preview.SynonymMatches.Count,
                    preview.ExpandedQueries?.Count ?? 0,
                    preview.SubObligations.Count,
                    preview.Bm25Matches.Count,
                    preview.Bm25Matches.Count(m => m.MatchedVia == ExpandedVia),
                    preview.Matches.Count,
                    preview.Matches.Count(m => m.MatchedVia == ExpandedVia),
                    preview.FusedMatches?.Count ?? 0,
                    string.Join(" | ", (preview.FusedMatches ?? []).Select(m =>
                        $"{m.SourceDocumentName} {m.ClauseNo} p.{m.SourcePage}")));
                finding.UpdatedAt = DateTimeOffset.UtcNow;
                processed++;
                // Per clause, so the pipeline panel can show retrieval progress (N of M clauses).
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation(
                "Retrieval complete for run {RunId}: {Processed}/{Total} clause(s) processed against {ExtractionCount} indexed internal document(s), {Units} search unit(s)",
                run.Id, processed, findings.Count, corpus.ExtractionIds.Count, corpus.SectionById.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Judging with an empty context would report every clause as a gap, so stop the run instead.
            logger.LogError(ex, "Retrieval failed for run {RunId}", run.Id);
            throw new InvalidOperationException($"Retrieval (Steps 1-6) failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// v4+: builds the search passages of any run document that has none yet (indexed before passages existed,
    /// or embedded with another model). One-time per document; the run shows phase "passages" meanwhile so the
    /// pipeline panel can say why Steps 1-6 have not started.
    /// </summary>
    private async Task PrepareSearchPassagesAsync(NdAnalysisRun run, IReadOnlyCollection<Guid> corpusDocIds, CancellationToken ct)
    {
        var docIds = corpusDocIds.ToList();
        var indexed = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => docIds.Contains(e.StoredDocumentId) && e.IndexStatus == "indexed")
            .ToListAsync(ct);
        var missing = new List<Guid>();
        foreach (var e in NdPassageIndexService.PickSearchExtractions(indexed))
            if (!await passageIndex.HasCurrentPassagesAsync(e.Id, ct)) missing.Add(e.Id);
        if (missing.Count == 0) return;

        var phase = run.RegulPipelinePhase;
        run.RegulPipelinePhase = "passages";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < missing.Count; i++)
        {
            logger.LogInformation(
                "Preparing search passages (one-time) for run {RunId}: document {Index} of {Count}, extraction {ExtractionId}",
                run.Id, i + 1, missing.Count, missing[i]);
            await passageIndex.EnsureCurrentAsync(missing[i], ct);
        }

        logger.LogInformation("Search passages ready for run {RunId}: {Count} document(s) in {Ms} ms", run.Id, missing.Count, timer.ElapsedMilliseconds);
        run.RegulPipelinePhase = phase;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Steps 1-6 again for one clause of a run (clause rerun), against the run's internal documents.
    /// Saves the clause's RetrievalJson exactly like <see cref="RunRetrievalAsync"/> does for every clause.</summary>
    public async Task RunRetrievalForFindingAsync(NdAnalysisRun run, NdRegulForwardFinding finding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(finding.ClauseText)) return;
        var corpusDocIds = (JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [])
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();
        if (corpusDocIds.Count == 0) return;

        var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
        if (corpus == null)
        {
            logger.LogInformation("Clause retrieval skipped for {ClauseNo}: no indexed internal documents", finding.ClauseNo);
            return;
        }

        var preview = await BuildPreviewAsync(corpus, finding.ClauseText, pipelineVersion, ct);
        preview = preview with
        {
            ElapsedMs = timer.ElapsedMilliseconds,
            EmbeddingModel = pipelineVersion >= NdRegulPipelineVersions.V4Passages ? await passageEmbeddings.ModelNameAsync(ct) : null,
        };
        finding.RetrievalJson = JsonSerializer.Serialize(preview, RetrievalJsonOptions);
        finding.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Regul Steps 1-6 (clause rerun, {Pipeline}) for clause {ClauseNo}: bm25={Bm25}, embedding={Embedding}, expanded queries={Expanded}, selected={Fused}",
            NdRegulPipelineVersions.Label(pipelineVersion), finding.ClauseNo, preview.Bm25Matches.Count,
            preview.Matches.Count, preview.ExpandedQueries?.Count ?? 0, preview.FusedMatches?.Count ?? 0);
    }

    /// <summary>Steps 1-6 for one clause against a loaded corpus — exactly what the analysis run stores
    /// on the clause's RetrievalJson.</summary>
    private async Task<RetrievalPreview> BuildPreviewAsync(
        LoadedCorpus corpus, string clauseText, int pipelineVersion, CancellationToken ct)
    {
        // Step 2 — split this clause into its distinct obligations (free, local, regex — see
        // SubObligationSplitter). Each sub-obligation gets its own Step 1 expansion and Step 3/4
        // retrieval, so a bundled clause searched as one blended query can't wash out a section
        // that only matches one of its several obligations.
        var subObligations = SubObligationSplitter.Split(clauseText, pipelineVersion);
        if (corpus.Vectors != null) await PrewarmQueryVectorsAsync(subObligations, pipelineVersion, ct);
        var hits = new QueryHits();
        foreach (var subText in subObligations)
        {
            await ScoreQueryAsync(
                corpus,
                subText,
                subObligations.Count > 1 ? Truncate(subText, PreviewLength) : null,
                hits,
                pipelineVersion,
                ct);
        }

        // Step 5 + 6. v1/v2: 0.4 BM25 + 0.6 embedding (each normalized against this clause's best), trimmed
        // to >= 50% of the best, 5..60 sections. v3: combined by rank over every part's own relevant matches,
        // every selected section kept (no count limit).
        var selected = SelectFused(hits, pipelineVersion);
        return new RetrievalPreview(
            hits.Acronyms.Values.ToList(),
            hits.Synonyms.Values.ToList(),
            hits.Bm25BySection.Values.ToList(),
            hits.EmbeddingBySection.Values.ToList(),
            subObligations,
            selected,
            pipelineVersion,
            pipelineVersion >= NdRegulPipelineVersions.V2ExpandedWording ? hits.ExpandedQueries : null);
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
        var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
        var corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
        return corpus == null ? null : await BuildPreviewAsync(corpus, clauseText, pipelineVersion, ct);
    }

    /// <summary>One expected-evidence snippet: whether a selected unit contains it (and its rank in the selection),
    /// and where it is in the indexed text at all. Empty <see cref="FoundIn"/> means the snippet is not in the indexed
    /// text (a parsing / extraction problem, or the snippet was typed differently).</summary>
    public sealed record AnchorCheck(string Snippet, bool Selected, int? Rank, string? SelectedLabel, IReadOnlyList<string> FoundIn);

    public sealed record RetrievalCheckResult(
        int PipelineVersion,
        int Parts,
        int ExpandedQueries,
        int Selected,
        int ContextChars,
        long ElapsedMs,
        IReadOnlyList<AnchorCheck> Snippets);

    // The retrieval check runs several clauses against the same documents; load them once per request.
    private (string Key, LoadedCorpus Corpus)? _checkCorpus;

    /// <summary>
    /// Free retrieval check: Steps 1-6 for one clause exactly as an analysis runs them (current pipeline version),
    /// without any AI call, then looks each expected snippet up in the selected sections / passages and in the
    /// whole indexed corpus. Null when none of the documents is indexed.
    /// </summary>
    public async Task<RetrievalCheckResult?> CheckClauseAsync(
        IReadOnlyCollection<Guid> corpusDocIds, string clauseText, IReadOnlyList<string> snippets, CancellationToken ct)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
        var key = $"{pipelineVersion}|{string.Join(',', corpusDocIds.OrderBy(g => g))}";
        LoadedCorpus? corpus;
        if (_checkCorpus is { } cached && cached.Key == key)
        {
            corpus = cached.Corpus;
        }
        else
        {
            corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
            if (corpus == null) return null;
            _checkCorpus = (key, corpus);
        }

        var preview = await BuildPreviewAsync(corpus, clauseText, pipelineVersion, ct);
        var selected = preview.FusedMatches ?? [];
        var rankById = selected.Select((m, i) => (m.SectionId, Rank: i + 1)).ToDictionary(x => x.SectionId, x => x.Rank);
        var contextChars = selected.Sum(m => corpus.SectionById.TryGetValue(m.SectionId, out var u) ? u.ClauseText.Length : m.TextPreview.Length);

        var checks = new List<AnchorCheck>();
        foreach (var snippet in snippets.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
        {
            var units = corpus.SectionById.Values.Where(u => SnippetMatches(u.ClauseText, snippet)).ToList();
            var best = units
                .Where(u => rankById.ContainsKey(u.Id))
                .OrderBy(u => rankById[u.Id])
                .FirstOrDefault();
            checks.Add(new AnchorCheck(
                snippet,
                best != null,
                best == null ? null : rankById[best.Id],
                best == null ? null : UnitLabel(corpus, best),
                units.Select(u => UnitLabel(corpus, u)).Distinct().ToList()));
        }

        return new RetrievalCheckResult(
            pipelineVersion,
            preview.SubObligations.Count,
            preview.ExpandedQueries?.Count ?? 0,
            selected.Count,
            contextChars,
            timer.ElapsedMilliseconds,
            checks);
    }

    private static string UnitLabel(LoadedCorpus corpus, CorpusSection unit)
    {
        var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(unit.ExtractionId);
        var name = corpus.DocNameById.GetValueOrDefault(storedDocId) ?? "document";
        var no = string.IsNullOrWhiteSpace(unit.ClauseNo) ? "" : $" - {unit.ClauseNo}";
        var page = unit.SourcePage is int p ? $" p.{p}" : "";
        return $"{name}{no}{page}";
    }

    /// <summary>A snippet matches a unit when its normalized text appears in it, or, tolerating line breaks and
    /// small parsing differences, when every word of 3+ letters of the snippet appears in the unit.</summary>
    public static bool SnippetMatches(string unitText, string snippet)
    {
        var text = NdRegulPolicyContextService.NormalizeForMatching(unitText);
        var needle = NdRegulPolicyContextService.NormalizeForMatching(snippet);
        if (needle.Length == 0) return false;
        if (text.Contains(needle, StringComparison.Ordinal)) return true;
        var words = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).Distinct().ToList();
        if (words.Count < 3) return false;
        var textWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return words.All(textWords.Contains);
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
        var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
        var corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
        if (corpus == null) return [];

        var hits = new QueryHits();
        foreach (var query in queryTexts.Where(q => !string.IsNullOrWhiteSpace(q)))
        {
            foreach (var subText in SubObligationSplitter.Split(query, pipelineVersion))
                await ScoreQueryAsync(corpus, subText, null, hits, pipelineVersion, ct);
        }

        var fused = SelectFused(hits, pipelineVersion);

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

    /// <summary>
    /// A run's search corpus held in memory for gap verification (pipeline v5): passages, vectors and the
    /// dictionary are loaded once, so searches during the parallel judgment phase never touch the database.
    /// Searches are serialized (one at a time per service, whatever the session: the query-vector and dictionary
    /// caches are shared); only the AI calls around them run in parallel.
    /// </summary>
    public sealed class EvidenceSession
    {
        private readonly LoadedCorpus _corpus;

        internal EvidenceSession(object corpus, int pipelineVersion)
        {
            _corpus = (LoadedCorpus)corpus;
            PipelineVersion = pipelineVersion;
        }

        public int PipelineVersion { get; }
        internal object Corpus => _corpus;
    }

    private readonly SemaphoreSlim _evidenceGate = new(1, 1);

    /// <summary>In-memory evidence session for the given documents; null below pipeline v4 (sections are scored in
    /// the database there) or when none of the documents is indexed.</summary>
    public async Task<EvidenceSession?> CreateEvidenceSessionAsync(IReadOnlyCollection<Guid> corpusDocIds, CancellationToken ct)
    {
        var pipelineVersion = await settings.GetPipelineVersionAsync(ct);
        if (pipelineVersion < NdRegulPipelineVersions.V4Passages || corpusDocIds.Count == 0) return null;
        var corpus = await LoadCorpusAsync(corpusDocIds, pipelineVersion, ct);
        if (corpus == null) return null;
        // Loads the dictionary tables now, so query expansion during the session needs no database access.
        await dictionary.ExpandQueryDetailedAsync("warm-up", ct);
        return new EvidenceSession(corpus, pipelineVersion);
    }

    /// <summary>Steps 1-6 for free-text queries against a session's corpus (same expansion, splitting, relevance gates
    /// and rank fusion as an analysis), every selected unit returned with its full text. No count limit.</summary>
    public async Task<IReadOnlyList<EvidenceSection>> SearchEvidenceAsync(
        EvidenceSession session, IReadOnlyList<string> queries, CancellationToken ct)
    {
        var corpus = (LoadedCorpus)session.Corpus;
        await _evidenceGate.WaitAsync(ct);
        try
        {
            var hits = new QueryHits();
            var parts = queries.Where(q => !string.IsNullOrWhiteSpace(q))
                .SelectMany(q => SubObligationSplitter.Split(q, session.PipelineVersion))
                .ToList();
            if (corpus.Vectors != null) await PrewarmQueryVectorsAsync(parts, session.PipelineVersion, ct);
            foreach (var part in parts)
                await ScoreQueryAsync(corpus, part, null, hits, session.PipelineVersion, ct);

            return SelectFused(hits, session.PipelineVersion)
                .Select(m => new EvidenceSection(
                    m.SectionId,
                    m.ClauseNo,
                    corpus.SectionById.TryGetValue(m.SectionId, out var unit) ? unit.ClauseText : m.TextPreview,
                    m.SourceDocumentId,
                    m.SourceDocumentName,
                    m.SourcePage,
                    Math.Round(m.FusedScore, 4)))
                .ToList();
        }
        finally
        {
            _evidenceGate.Release();
        }
    }

    private sealed record CorpusSection(Guid Id, Guid ExtractionId, string? ClauseNo, string ClauseText, int? SourcePage);

    /// <summary>The run's search units: whole sections (v1-v3) or passages with their heading path (v4+, where
    /// <see cref="VectorById"/> also holds every passage vector for in-memory scoring).</summary>
    private sealed record LoadedCorpus(
        List<Guid> ExtractionIds,
        Dictionary<Guid, CorpusSection> SectionById,
        Bm25Scorer.Corpus Bm25Corpus,
        Dictionary<Guid, string?> DocNameById,
        Dictionary<Guid, Guid> StoredDocIdByExtractionId,
        IReadOnlyList<(Guid Id, float[] Vector, double Norm)>? Vectors = null);

    private sealed class QueryHits
    {
        public Dictionary<string, DictionaryExpansionService.ExpansionMatch> Acronyms { get; } = new();
        public Dictionary<string, DictionaryExpansionService.ExpansionMatch> Synonyms { get; } = new();
        public Dictionary<Guid, Bm25Match> Bm25BySection { get; } = new();
        public Dictionary<Guid, RetrievalMatch> EmbeddingBySection { get; } = new();
        public List<string> ExpandedQueries { get; } = [];
        /// <summary>v3: reciprocal-rank score per section, summed over every query's BM25 and embedding list.</summary>
        public Dictionary<Guid, double> RankScoreBySection { get; } = new();
    }

    private static List<HybridFusionSelector.FusedMatch> SelectFused(QueryHits hits, int pipelineVersion) =>
        pipelineVersion >= NdRegulPipelineVersions.V3RelevanceSelection
            ? HybridFusionSelector.FuseByRank(
                hits.Bm25BySection.Values.ToList(), hits.EmbeddingBySection.Values.ToList(), hits.RankScoreBySection)
            : HybridFusionSelector.SelectDynamic(
                HybridFusionSelector.Fuse(hits.Bm25BySection.Values.ToList(), hits.EmbeddingBySection.Values.ToList()));

    public const string ExpandedVia = "expanded";

    private async Task<LoadedCorpus?> LoadCorpusAsync(IReadOnlyCollection<Guid> corpusDocIds, int pipelineVersion, CancellationToken ct)
    {
        var docIds = corpusDocIds.ToList();
        var indexed = await db.NdLocalDocumentExtractions
            .AsNoTracking()
            .Where(e => docIds.Contains(e.StoredDocumentId) && e.IndexStatus == "indexed")
            .ToListAsync(ct);
        // One index per document: a file indexed under several OCR engines would otherwise put the same
        // policy text into the corpus (and the judgment context) more than once.
        var extractions = NdPassageIndexService.PickSearchExtractions(indexed);
        if (extractions.Count == 0) return null;

        var extractionIds = extractions.Select(e => e.Id).ToList();
        var docNameById = await db.StoredDocuments
            .AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => (string?)(d.Title ?? d.OriginalFileName), ct);

        if (pipelineVersion >= NdRegulPipelineVersions.V4Passages)
            return await LoadPassageCorpusAsync(extractionIds, docNameById, extractions.ToDictionary(e => e.Id, e => e.StoredDocumentId), ct);

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

    /// <summary>
    /// v4+ corpus: every passage of the run's documents with its vector. A document indexed before passages
    /// existed, or embedded with another model, gets its passages built now, so it is never left out.
    /// </summary>
    private async Task<LoadedCorpus> LoadPassageCorpusAsync(
        List<Guid> extractionIds,
        Dictionary<Guid, string?> docNameById,
        Dictionary<Guid, Guid> storedDocIdByExtractionId,
        CancellationToken ct)
    {
        foreach (var extractionId in extractionIds)
            await passageIndex.EnsureCurrentAsync(extractionId, ct);

        var model = await passageEmbeddings.ModelNameAsync(ct);
        var passages = await db.NdLocalDocumentPassages
            .AsNoTracking()
            .Where(p => extractionIds.Contains(p.ExtractionId) && p.EmbeddingModel == model)
            .Select(p => new { p.Id, p.ExtractionId, p.ClauseNo, p.HeadingPath, p.PassageText, p.SourcePage, p.Embedding })
            .ToListAsync(ct);

        var units = passages.ToDictionary(
            p => p.Id,
            p => new CorpusSection(p.Id, p.ExtractionId, p.ClauseNo, PassageContextText(p.HeadingPath, p.PassageText), p.SourcePage));
        var vectors = passages
            .Where(p => p.Embedding != null)
            .Select(p =>
            {
                var v = p.Embedding!.ToArray();
                return (p.Id, v, Math.Sqrt(v.Sum(x => (double)x * x)));
            })
            .ToList();

        return new LoadedCorpus(
            extractionIds,
            units,
            Bm25Scorer.BuildCorpus(units.Values.Select(u => (u.Id, u.ClauseText)).ToList()),
            docNameById,
            storedDocIdByExtractionId,
            vectors);
    }

    /// <summary>What the search scores and the AI reads for a passage: its heading path, then its text. Not in square
    /// brackets: those are the evidence labels the AI cites.</summary>
    public static string PassageContextText(string? headingPath, string passageText) =>
        string.IsNullOrWhiteSpace(headingPath) ? passageText : $"Heading: {headingPath}\n{passageText}";

    /// <summary>Full text of retrieved search units by id: section rows (v1-v3) or passages (v4+, heading path
    /// included). The retrieval record only stores a short preview.</summary>
    public async Task<Dictionary<Guid, string>> LoadUnitTextsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var list = ids.Distinct().ToList();
        var texts = await db.NdLocalDocumentExtractionSections.AsNoTracking()
            .Where(s => list.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ClauseText, ct);
        var missing = list.Where(id => !texts.ContainsKey(id)).ToList();
        if (missing.Count == 0) return texts;
        var passages = await db.NdLocalDocumentPassages.AsNoTracking()
            .Where(p => missing.Contains(p.Id))
            .Select(p => new { p.Id, p.HeadingPath, p.PassageText })
            .ToListAsync(ct);
        foreach (var p in passages) texts[p.Id] = PassageContextText(p.HeadingPath, p.PassageText);
        return texts;
    }

    // Step 1 per query text, once per job (the same part is expanded for the vector pre-pass and for the search).
    private readonly Dictionary<(string Text, int Version), DictionaryExpansionService.QueryExpansionResult> _expansions = new();

    private async Task<DictionaryExpansionService.QueryExpansionResult> ExpandAsync(string subText, int pipelineVersion, CancellationToken ct)
    {
        if (_expansions.TryGetValue((subText, pipelineVersion), out var cached)) return cached;
        var expanded = await dictionary.ExpandQueryDetailedAsync(subText, ct);
        // v4+: an acronym whose letters do not match the initials of its full form ("GPML" for "Money Laundering")
        // would search nonsense wording; it is left out. v1-v3 unchanged.
        if (pipelineVersion >= NdRegulPipelineVersions.V4Passages)
            expanded = new DictionaryExpansionService.QueryExpansionResult(
                expanded.AcronymMatches.Where(m => IsPlausibleAcronymPair(m.TermA, m.TermB)).ToList(),
                expanded.SynonymMatches);
        _expansions[(subText, pipelineVersion)] = expanded;
        return expanded;
    }

    /// <summary>The texts Steps 3-4 search for one part: the part with its counterpart terms appended, then (v2+)
    /// the part reworded with each counterpart.</summary>
    private static (string QueryText, IReadOnlyList<string> Variants) SearchTexts(
        string subText, DictionaryExpansionService.QueryExpansionResult expanded, int pipelineVersion)
    {
        var queryText = expanded.AllTerms.Count == 0
            ? subText
            : subText + " " + string.Join(" ", expanded.AllTerms);
        // v2: the clause may say "CDD" while the policy says "customer due diligence" (or the reverse, or a
        // synonym). Appending the counterpart to a long clause barely moves its BM25 score or its embedding,
        // so search the sub-obligation again with every matched term swapped for its counterpart: sections
        // written in the other form then score like the clause itself on both sides.
        // v4: a term with several equivalents (timeframe / time period / period of time / duration) is searched
        // once with each of them, not only with the first.
        IReadOnlyList<string> variants = pipelineVersion < NdRegulPipelineVersions.V2ExpandedWording
            ? []
            : pipelineVersion >= NdRegulPipelineVersions.V4Passages
                ? BuildExpandedWordingVariants(subText, expanded)
                : BuildExpandedWording(subText, expanded) is { } single ? [single] : [];
        return (queryText, variants);
    }

    /// <summary>
    /// v4+: embeds every text the parts will search in a few batched calls before searching, instead of one call per
    /// text (with Azure OpenAI one call per text made Steps 1-6 take ~30 s for a long clause).
    /// </summary>
    private async Task PrewarmQueryVectorsAsync(IEnumerable<string> subTexts, int pipelineVersion, CancellationToken ct)
    {
        if (pipelineVersion < NdRegulPipelineVersions.V4Passages) return;
        var texts = new List<string>();
        foreach (var subText in subTexts)
        {
            var (queryText, variants) = SearchTexts(subText, await ExpandAsync(subText, pipelineVersion, ct), pipelineVersion);
            texts.Add(queryText);
            texts.AddRange(variants);
        }

        var missing = texts.Distinct(StringComparer.Ordinal).Where(t => !_queryVectors.ContainsKey(t)).ToList();
        if (missing.Count == 0) return;
        var vectors = await passageEmbeddings.EmbedManyAsync(missing, ct);
        for (var i = 0; i < missing.Count; i++) _queryVectors[missing[i]] = vectors[i];
    }

    /// <summary>
    /// True when an acronym pair is plausible: the acronym's letters equal the initials of its full form, skipping
    /// small words (of, the, and, ...) or not, with hyphenated parts as words and an all-capitals word giving all its
    /// letters ("CBUAE" = "Central Bank of the UAE"). Pairs that are not acronyms (both sides several words) pass.
    /// </summary>
    public static bool IsPlausibleAcronymPair(string termA, string termB)
    {
        static bool IsAcronym(string t) => !string.IsNullOrWhiteSpace(t) && !t.Trim().Contains(' ')
            && t.Count(char.IsLetter) >= 2 && t.Count(char.IsUpper) >= 2;
        var a = (termA ?? "").Trim();
        var b = (termB ?? "").Trim();
        string acronym, full;
        if (IsAcronym(a) && !IsAcronym(b)) (acronym, full) = (a, b);
        else if (IsAcronym(b) && !IsAcronym(a)) (acronym, full) = (b, a);
        else return true;

        var letters = new string(acronym.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        var words = Regex.Split(full, @"[\s\-/]+").Where(w => w.Any(char.IsLetter)).ToList();
        string Initials(IEnumerable<string> ws) => string.Concat(ws.Select(w =>
        {
            var clean = new string(w.Where(char.IsLetter).ToArray());
            return clean.Length > 1 && clean.All(char.IsUpper) ? clean : clean[..1].ToUpperInvariant();
        }));
        if (Initials(words) == letters) return true;
        var small = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "of", "the", "and", "for", "to", "in", "on", "a", "an", "&" };
        return Initials(words.Where(w => !small.Contains(w))) == letters;
    }

    /// <summary>Steps 1, 3 and 4 for one query text, merged into <paramref name="hits"/> keeping each
    /// section's best score. Pipeline v2 also searches the expanded wording (see <see cref="BuildExpandedWording"/>).</summary>
    private async Task ScoreQueryAsync(
        LoadedCorpus corpus,
        string subText,
        string? subObligationLabel,
        QueryHits hits,
        int pipelineVersion,
        CancellationToken ct)
    {
        var expanded = await ExpandAsync(subText, pipelineVersion, ct);
        foreach (var m in expanded.AcronymMatches) hits.Acronyms[$"{m.EntryId}:{m.MatchedText}"] = m;
        foreach (var m in expanded.SynonymMatches) hits.Synonyms[$"{m.EntryId}:{m.MatchedText}"] = m;
        var (queryText, variants) = SearchTexts(subText, expanded, pipelineVersion);
        await SearchAsync(corpus, queryText, subObligationLabel, null, hits, pipelineVersion, ct);
        foreach (var reworded in variants)
        {
            hits.ExpandedQueries.Add(reworded);
            await SearchAsync(corpus, reworded, subObligationLabel, ExpandedVia, hits, pipelineVersion, ct);
        }
    }

    /// <summary>Step 3 (BM25) and Step 4 (embedding) for one query text.</summary>
    private async Task SearchAsync(
        LoadedCorpus corpus,
        string queryText,
        string? subObligationLabel,
        string? via,
        QueryHits hits,
        int pipelineVersion,
        CancellationToken ct)
    {
        if (pipelineVersion >= NdRegulPipelineVersions.V4Passages && corpus.Vectors != null)
        {
            await SearchRelevantInMemoryAsync(corpus, queryText, subObligationLabel, via, hits, ct);
            return;
        }

        if (pipelineVersion >= NdRegulPipelineVersions.V3RelevanceSelection)
        {
            await SearchRelevantAsync(corpus, queryText, subObligationLabel, via, hits, ct);
            return;
        }

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
                subObligationLabel,
                via);
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
            // Ids and distances only — every section's text is already in the loaded corpus, so pulling
            // up to 300 full section texts over the wire again per query was pure transfer cost.
            .Select(s => new { s.Id, Distance = s.Embedding!.CosineDistance(queryVector) })
            .ToListAsync(ct);

        var bestSimilarity = candidates.Count == 0 ? 0 : 1 - candidates.Min(c => c.Distance);
        var similarityCutoff = bestSimilarity * EmbeddingRelativeThreshold;
        foreach (var x in candidates.Select(m => new { m, Similarity = 1 - m.Distance }))
        {
            if (x.Similarity < similarityCutoff) continue;
            if (!corpus.SectionById.TryGetValue(x.m.Id, out var section)) continue;
            if (hits.EmbeddingBySection.TryGetValue(x.m.Id, out var existing) && existing.Similarity >= x.Similarity) continue;
            var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(section.ExtractionId);
            hits.EmbeddingBySection[x.m.Id] = new RetrievalMatch(
                x.m.Id,
                section.ClauseNo,
                Truncate(section.ClauseText, PreviewLength),
                storedDocId,
                corpus.DocNameById.GetValueOrDefault(storedDocId),
                section.SourcePage,
                Math.Round(x.Similarity, 4),
                subObligationLabel,
                via);
        }
    }

    /// <summary>
    /// Pipeline v3 Steps 3 and 4 for one query text. Every section is scored on both sides (no candidate limit),
    /// and a section is kept when it stands clearly above the rest of the documents for this query
    /// (<see cref="HybridFusionSelector.SelectRelevant"/>), so the number kept follows the scores, not a count.
    /// Each kept section's rank on each side adds to its reciprocal-rank score for Step 5.
    /// </summary>
    private async Task SearchRelevantAsync(
        LoadedCorpus corpus,
        string queryText,
        string? subObligationLabel,
        string? via,
        QueryHits hits,
        CancellationToken ct)
    {
        AddBm25Relevant(corpus, queryText, subObligationLabel, via, hits);

        var queryVector = new Vector(embedder.Embed(queryText));
        var extractionIds = corpus.ExtractionIds;
        var distances = await db.NdLocalDocumentExtractionSections
            .AsNoTracking()
            .Where(s => extractionIds.Contains(s.ExtractionId) && s.Embedding != null)
            .OrderBy(s => s.Embedding!.CosineDistance(queryVector))
            .Select(s => new { s.Id, Distance = s.Embedding!.CosineDistance(queryVector) })
            .ToListAsync(ct);
        AddEmbeddingRelevant(
            corpus, distances.Select(d => (d.Id, 1 - d.Distance)).ToList(), subObligationLabel, via, hits);
    }

    /// <summary>v4+: same as <see cref="SearchRelevantAsync"/> over passages, with the query compared to every
    /// passage vector in memory (vectors were loaded once for the run) instead of a database query per search.</summary>
    private async Task SearchRelevantInMemoryAsync(
        LoadedCorpus corpus,
        string queryText,
        string? subObligationLabel,
        string? via,
        QueryHits hits,
        CancellationToken ct)
    {
        AddBm25Relevant(corpus, queryText, subObligationLabel, via, hits);

        var query = await EmbedQueryAsync(queryText, ct);
        var queryNorm = Math.Sqrt(query.Sum(x => (double)x * x));
        var similarities = new List<(Guid SectionId, double Score)>(corpus.Vectors!.Count);
        foreach (var (id, vector, norm) in corpus.Vectors!)
        {
            if (norm == 0 || queryNorm == 0 || vector.Length != query.Length) continue;
            double dot = 0;
            for (var k = 0; k < vector.Length; k++) dot += vector[k] * query[k];
            similarities.Add((id, dot / (norm * queryNorm)));
        }

        AddEmbeddingRelevant(corpus, similarities, subObligationLabel, via, hits);
    }

    // One embedding per distinct query text per job (the same part can be searched from several entry points).
    private readonly Dictionary<string, float[]> _queryVectors = new(StringComparer.Ordinal);

    private async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
    {
        if (_queryVectors.TryGetValue(text, out var cached)) return cached;
        var vector = await passageEmbeddings.EmbedAsync(text, ct);
        _queryVectors[text] = vector;
        return vector;
    }

    private static void AddBm25Relevant(LoadedCorpus corpus, string queryText, string? subObligationLabel, string? via, QueryHits hits)
    {
        var bm25Relevant = HybridFusionSelector.SelectRelevant(
            Bm25Scorer.Score(corpus.Bm25Corpus, queryText), corpus.Bm25Corpus.Docs.Count);
        for (var i = 0; i < bm25Relevant.Count; i++)
        {
            var (sectionId, score) = bm25Relevant[i];
            AddRankScore(hits, sectionId, i);
            var s = corpus.SectionById[sectionId];
            if (hits.Bm25BySection.TryGetValue(s.Id, out var existing) && existing.Score >= score) continue;
            var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(s.ExtractionId);
            hits.Bm25BySection[s.Id] = new Bm25Match(
                s.Id,
                s.ClauseNo,
                Truncate(s.ClauseText, PreviewLength),
                storedDocId,
                corpus.DocNameById.GetValueOrDefault(storedDocId),
                s.SourcePage,
                Math.Round(score, 4),
                subObligationLabel,
                via);
        }
    }

    private static void AddEmbeddingRelevant(
        LoadedCorpus corpus,
        IReadOnlyList<(Guid SectionId, double Score)> similarities,
        string? subObligationLabel,
        string? via,
        QueryHits hits)
    {
        var embeddingRelevant = HybridFusionSelector.SelectRelevant(similarities, similarities.Count);
        for (var i = 0; i < embeddingRelevant.Count; i++)
        {
            var (sectionId, similarity) = embeddingRelevant[i];
            if (!corpus.SectionById.TryGetValue(sectionId, out var section)) continue;
            AddRankScore(hits, sectionId, i);
            if (hits.EmbeddingBySection.TryGetValue(sectionId, out var existing) && existing.Similarity >= similarity) continue;
            var storedDocId = corpus.StoredDocIdByExtractionId.GetValueOrDefault(section.ExtractionId);
            hits.EmbeddingBySection[sectionId] = new RetrievalMatch(
                sectionId,
                section.ClauseNo,
                Truncate(section.ClauseText, PreviewLength),
                storedDocId,
                corpus.DocNameById.GetValueOrDefault(storedDocId),
                section.SourcePage,
                Math.Round(similarity, 4),
                subObligationLabel,
                via);
        }
    }

    private static void AddRankScore(QueryHits hits, Guid sectionId, int zeroBasedRank) =>
        hits.RankScoreBySection[sectionId] =
            hits.RankScoreBySection.GetValueOrDefault(sectionId) + HybridFusionSelector.RankScore(zeroBasedRank);

    /// <summary>
    /// The sub-obligation with every query-expansion match swapped for its counterpart ("CDD" -> "customer due
    /// diligence", "customer due diligence" -> "CDD", synonym A -> synonym B). Longest match first, and a
    /// replaced span is never rewritten again by a shorter swap. Null when nothing matched.
    /// </summary>
    public static string? BuildExpandedWording(string text, DictionaryExpansionService.QueryExpansionResult expanded)
    {
        var swaps = expanded.AcronymMatches
            .Select(m => (Matched: m.MatchedText, Added: m.AddedText, IsAcronym: m.MatchedText == m.TermA))
            .Concat(expanded.SynonymMatches.Select(m => (Matched: m.MatchedText, Added: m.AddedText, IsAcronym: false)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Matched) && !string.IsNullOrWhiteSpace(x.Added))
            .DistinctBy(x => x.Matched.ToLowerInvariant())
            .OrderByDescending(x => x.Matched.Length)
            .ToList();
        if (swaps.Count == 0) return null;

        // Placeholder tokens (a control character never present in parsed text) mark replaced spans.
        const char Mark = '\u0001';
        var result = text;
        var replacements = new List<string>();
        foreach (var (matched, added, isAcronym) in swaps)
        {
            // Acronyms match case-sensitively on word boundaries, as Step 1 found them; full forms and
            // synonyms case-insensitively, also on word boundaries ("policy" never rewrites "policyholder").
            var pattern = isAcronym ? $@"\b{Regex.Escape(matched)}\b" : DictionaryExpansionService.WholePhrasePattern(matched);
            var options = isAcronym ? RegexOptions.None : RegexOptions.IgnoreCase;
            var token = $"{Mark}{replacements.Count}{Mark}";
            var next = Regex.Replace(result, pattern, token, options);
            if (next == result) continue;
            result = next;
            replacements.Add(added);
        }
        if (replacements.Count == 0) return null;
        for (var i = 0; i < replacements.Count; i++)
            result = result.Replace($"{Mark}{i}{Mark}", replacements[i], StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    /// v4: the expanded wording once per alternative. A term matched with several equivalents ("timeframe" with
    /// "time period", "period of time", "duration") gives one rewording per equivalent; other matched terms take
    /// their first (or only) counterpart in every variant. Empty when nothing matched; no count limit, the number
    /// of variants is the size of the largest matched group.
    /// </summary>
    public static IReadOnlyList<string> BuildExpandedWordingVariants(string text, DictionaryExpansionService.QueryExpansionResult expanded)
    {
        var groups = expanded.AcronymMatches
            .Concat(expanded.SynonymMatches)
            .Where(m => !string.IsNullOrWhiteSpace(m.MatchedText) && !string.IsNullOrWhiteSpace(m.AddedText))
            .GroupBy(m => m.MatchedText.ToLowerInvariant())
            .Select(g => g.GroupBy(m => m.AddedText.ToLowerInvariant()).Select(x => x.First()).ToList())
            .ToList();
        if (groups.Count == 0) return [];

        var rounds = groups.Max(g => g.Count);
        var variants = new List<string>();
        for (var round = 0; round < rounds; round++)
        {
            var picked = groups.Select(g => g[Math.Min(round, g.Count - 1)]).ToList();
            var acronyms = picked.Where(m => expanded.AcronymMatches.Contains(m)).ToList();
            var synonyms = picked.Where(m => !expanded.AcronymMatches.Contains(m)).ToList();
            var reworded = BuildExpandedWording(text, new DictionaryExpansionService.QueryExpansionResult(acronyms, synonyms));
            if (reworded != null && !variants.Contains(reworded, StringComparer.Ordinal)) variants.Add(reworded);
        }

        return variants;
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length > maxChars ? text[..maxChars] + "…" : text;
}
