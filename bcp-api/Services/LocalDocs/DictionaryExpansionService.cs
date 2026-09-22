using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Query expansion — hybrid pipeline Step 1 (see docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md
/// and docs/roadmap/QUERY-EXPANSION-PLAN.md). Maintains the acronym/full-form dictionary
/// (<see cref="Data.Entities.NdDictionaryEntry"/>) and expands a clause's terms with their known
/// counterparts before a future search step (Step 3, not built yet) runs. Not an AI step — a
/// flat dictionary lookup, same as the underlying harvesting is a flat regex, not a model call.
/// </summary>
public sealed class DictionaryExpansionService(
    AppDbContext db,
    IHostEnvironment env,
    LocalEmbeddingService embedder,
    ILogger<DictionaryExpansionService> logger)
{
    private const string SeedFileName = "dictionary-seed.json";
    private const string SynonymSeedFileName = "synonym-seed.json";

    // Synonym candidate harvesting (embedding-similarity, not text-shape) — see
    // SynonymCandidateHarvestAsync and NdSynonymEntry's doc comment for the full reasoning.
    private const double SynonymSimilarityThreshold = 0.90;
    private const double SynonymMaxWordOverlap = 0.5;
    private const int SynonymMinSectionChars = 20;
    private const int SynonymMaxSectionChars = 400;
    private const int SynonymMaxSectionsConsidered = 150;
    private const int SynonymMaxCandidatesPerHarvest = 15;
    private const int SynonymStoredTextMaxChars = 140;

    private sealed record SeedEntry(string Acronym, string Definition);
    private sealed record SynonymSeedEntry(string TermA, string TermB);

    /// <summary>Scans one document's already-parsed text for "Full Form (ABBR)" pairs and stores
    /// any new ones, then separately flags any bare short-form token used in the document that
    /// still has no known definition anywhere (an "unresolved" placeholder row — empty
    /// Definition — for the admin dictionary page to surface and let someone fill in). Called
    /// synchronously right after structural Extract succeeds — a regex pass over in-memory text,
    /// not a background job (unlike embedding-based indexing, which genuinely needs one). Never
    /// throws — a harvest failure must not fail the Extract call that triggered it; the caller
    /// still wraps this in try/catch as a second line of defense.</summary>
    public async Task HarvestFromDocumentAsync(string? markdownText, Guid sourceDocumentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(markdownText)) return;

        var harvested = AcronymHarvester.Harvest(markdownText);
        foreach (var h in harvested)
        {
            await UpsertAsync(h.Acronym, h.Definition, "auto", sourceDocumentId, h.SourcePage, ct);
            // A real definition just showed up for this acronym — any earlier "unresolved"
            // placeholder (empty definition) for the same acronym is stale now, clear it.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM nd_dictionary_entries
                WHERE lower(acronym) = lower({h.Acronym}) AND definition = ''", ct);
        }

        var known = await db.NdDictionaryEntries.AsNoTracking()
            .Select(e => e.Acronym.ToLower())
            .Distinct()
            .ToListAsync(ct);
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);

        var candidates = AcronymHarvester.FindCandidates(markdownText);
        var flagged = 0;
        foreach (var c in candidates)
        {
            if (knownSet.Contains(c.Acronym.ToLowerInvariant())) continue;
            await UpsertAsync(c.Acronym, "", "auto", sourceDocumentId, c.SourcePage, ct);
            knownSet.Add(c.Acronym.ToLowerInvariant()); // avoid inserting the same unresolved token twice in one pass
            flagged++;
        }

        if (harvested.Count > 0 || flagged > 0)
            logger.LogInformation(
                "Document {DocId}: harvested {Resolved} acronym pair(s), flagged {Unresolved} unresolved",
                sourceDocumentId, harvested.Count, flagged);
    }

    /// <summary>Loads the manually curated fallback list (SeedData/dictionary-seed.json) at
    /// startup — idempotent, only inserts pairs not already present (auto-harvested or from a
    /// previous startup).</summary>
    public async Task LoadSeedAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", SeedFileName);
        if (!File.Exists(path)) return;

        List<SeedEntry> entries;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            entries = JsonSerializer.Deserialize<List<SeedEntry>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read dictionary seed file at {Path}", path);
            return;
        }

        foreach (var e in entries)
            await UpsertAsync(e.Acronym, e.Definition, "manual", null, null, ct);

        logger.LogInformation("Dictionary seed load complete ({Count} entries checked)", entries.Count);
    }

    /// <summary>Loads the manually curated synonym seed list (SeedData/synonym-seed.json) at
    /// startup — same idempotent pattern as <see cref="LoadSeedAsync"/>. There is no auto-harvest
    /// counterpart for synonyms (no reliable text shape to detect them from), so this seed list
    /// plus whatever an admin adds via the synonym admin page is the entire table.</summary>
    public async Task LoadSynonymSeedAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(env.ContentRootPath, "SeedData", SynonymSeedFileName);
        if (!File.Exists(path)) return;

        List<SynonymSeedEntry> entries;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            entries = JsonSerializer.Deserialize<List<SynonymSeedEntry>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read synonym seed file at {Path}", path);
            return;
        }

        foreach (var e in entries)
            await UpsertSynonymAsync(e.TermA, e.TermB, "manual", null, null, true, ct);

        logger.LogInformation("Synonym seed load complete ({Count} entries checked)", entries.Count);
    }

    /// <summary>Suggests synonym candidates from one document's already-extracted sections, using
    /// embedding similarity instead of a text shape (see <see cref="NdSynonymEntry"/>'s doc
    /// comment for why acronym-style regex harvesting can't work for synonyms). Compares every
    /// qualifying section pair within the document; a pair is a candidate when its embeddings are
    /// highly similar (same meaning) but its wording is not (low word overlap — otherwise it's
    /// two near-identical sentences, not two different phrasings). Inserted with
    /// <c>Source = "auto"</c> and <c>IsActive = false</c> — never affects a live analysis run
    /// until an admin reviews and activates it on the synonym admin page. Caps both the section
    /// count considered and the candidates stored per run so one large document can't flood the
    /// review queue. Never throws — same defensive contract as <see cref="HarvestFromDocumentAsync"/>.</summary>
    public async Task HarvestSynonymCandidatesAsync(
        IReadOnlyList<LocalSection> sections, Guid sourceDocumentId, CancellationToken ct)
    {
        var pool = sections
            .Where(s => s.ClauseText.Length is >= SynonymMinSectionChars and <= SynonymMaxSectionChars)
            .Take(SynonymMaxSectionsConsidered)
            .ToList();
        if (pool.Count < 2) return;

        var vectors = new float[pool.Count][];
        var wordSets = new HashSet<string>[pool.Count];
        for (var i = 0; i < pool.Count; i++)
        {
            vectors[i] = embedder.Embed(pool[i].ClauseText);
            wordSets[i] = new HashSet<string>(
                pool[i].ClauseText.ToLowerInvariant().Split(
                    [' ', '\t', '\n', '\r', ',', '.', ';', ':', '(', ')'],
                    StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
        }

        var candidates = new List<(double Similarity, int I, int J)>();
        for (var i = 0; i < pool.Count; i++)
        {
            for (var j = i + 1; j < pool.Count; j++)
            {
                var overlap = JaccardOverlap(wordSets[i], wordSets[j]);
                if (overlap > SynonymMaxWordOverlap) continue; // too similarly worded to be "different phrasing"

                var similarity = CosineSimilarity(vectors[i], vectors[j]);
                if (similarity < SynonymSimilarityThreshold) continue;

                candidates.Add((similarity, i, j));
            }
        }

        var stored = 0;
        foreach (var (_, i, j) in candidates.OrderByDescending(c => c.Similarity).Take(SynonymMaxCandidatesPerHarvest))
        {
            var termA = Truncate(pool[i].ClauseText, SynonymStoredTextMaxChars);
            var termB = Truncate(pool[j].ClauseText, SynonymStoredTextMaxChars);
            await UpsertSynonymAsync(termA, termB, "auto", sourceDocumentId, pool[i].SourcePage, false, ct);
            stored++;
        }

        if (stored > 0)
            logger.LogInformation(
                "Document {DocId}: suggested {Count} synonym candidate pair(s) for admin review",
                sourceDocumentId, stored);
    }

    private static double JaccardOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var k = 0; k < a.Length; k++)
        {
            dot += a[k] * b[k];
            magA += a[k] * a[k];
            magB += b[k] * b[k];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static string Truncate(string text, int maxChars)
    {
        text = text.Trim();
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }

    private async Task UpsertSynonymAsync(
        string termA, string termB, string source, Guid? sourceDocumentId, int? sourcePage, bool isActive,
        CancellationToken ct)
    {
        termA = termA.Trim();
        termB = termB.Trim();
        if (termA.Length == 0 || termB.Length == 0) return;

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO nd_synonym_entries
                (id, term_a, term_b, source, source_document_id, source_page, is_active, created_at)
            VALUES
                ({Guid.NewGuid()}, {termA}, {termB}, {source}, {sourceDocumentId}, {sourcePage}, {isActive}, now())
            ON CONFLICT (lower(term_a), lower(term_b)) DO NOTHING", ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>An empty <paramref name="definition"/> is a deliberate, valid value — it marks an
    /// "unresolved" placeholder row (a short form seen in a document with no known full form
    /// yet), not a data-quality problem. Only a missing/blank acronym is rejected.</summary>
    private async Task UpsertAsync(
        string acronym, string definition, string source, Guid? sourceDocumentId, int? sourcePage, CancellationToken ct)
    {
        acronym = acronym.Trim();
        definition = definition.Trim();
        if (acronym.Length == 0) return;

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO nd_dictionary_entries
                (id, acronym, definition, source, source_document_id, source_page, is_active, created_at)
            VALUES
                ({Guid.NewGuid()}, {acronym}, {definition}, {source}, {sourceDocumentId}, {sourcePage}, true, now())
            ON CONFLICT (lower(acronym), lower(definition)) DO NOTHING", ct);
    }

    /// <summary>One matched counterpart term for a clause. <see cref="TermA"/>/<see cref="TermB"/>
    /// are the entry's canonical stored pair (Acronym/Definition, or TermA/TermB) — always in
    /// that fixed order, so a consumer can edit the row without caring which side happened to
    /// match this particular clause. <see cref="MatchedText"/>/<see cref="AddedText"/> record
    /// which direction actually fired for display ("clause said X, added Y").</summary>
    public sealed record ExpansionMatch(Guid EntryId, string TermA, string TermB, string MatchedText, string AddedText);

    /// <summary>Step 1's actual deliverable, in full detail: every acronym match and every
    /// synonym match found in a clause, kept separate so a consumer can label them distinctly
    /// (the two dictionaries have different provenance — see <see cref="NdSynonymEntry"/>'s doc
    /// comment). <see cref="AllTerms"/> is the flattened form Step 4 actually embeds.</summary>
    public sealed record QueryExpansionResult(
        IReadOnlyList<ExpansionMatch> AcronymMatches,
        IReadOnlyList<ExpansionMatch> SynonymMatches)
    {
        public IReadOnlyList<string> AllTerms =>
            AcronymMatches.Select(m => m.AddedText)
                .Concat(SynonymMatches.Select(m => m.AddedText))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>Given a clause's text, finds every known counterpart term in it — both
    /// acronym/full-form pairs (<see cref="Data.Entities.NdDictionaryEntry"/>) and plain synonym
    /// pairs (<see cref="Data.Entities.NdSynonymEntry"/>) — for Step 4's embedding search to add
    /// to its query. Only active entries are considered.</summary>
    public async Task<QueryExpansionResult> ExpandQueryDetailedAsync(string clauseText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clauseText))
            return new QueryExpansionResult([], []);

        var entries = await db.NdDictionaryEntries.AsNoTracking()
            .Where(e => e.IsActive)
            .ToListAsync(ct);

        // "Resolved" only — an entry harvested with just the acronym/term detected and no
        // counterpart filled in yet has nothing useful to expand with, and worse: matching against
        // an EMPTY definition via string.Contains("") is trivially true for every clause (an empty
        // string is "contained" in anything), so an unresolved entry was firing as a spurious match
        // on every single clause regardless of content — pure noise, confirmed live (e.g. "LFI →",
        // "→ OFFICE" with nothing on the other side). Skip any entry missing either side.
        var acronymMatches = new List<ExpansionMatch>();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Acronym) || string.IsNullOrWhiteSpace(e.Definition)) continue;
            if (Regex.IsMatch(clauseText, $@"\b{Regex.Escape(e.Acronym)}\b"))
                acronymMatches.Add(new ExpansionMatch(e.Id, e.Acronym, e.Definition, e.Acronym, e.Definition));
            if (clauseText.Contains(e.Definition, StringComparison.OrdinalIgnoreCase))
                acronymMatches.Add(new ExpansionMatch(e.Id, e.Acronym, e.Definition, e.Definition, e.Acronym));
        }

        var synonyms = await db.NdSynonymEntries.AsNoTracking()
            .Where(s => s.IsActive)
            .ToListAsync(ct);

        var synonymMatches = new List<ExpansionMatch>();
        foreach (var s in synonyms)
        {
            if (string.IsNullOrWhiteSpace(s.TermA) || string.IsNullOrWhiteSpace(s.TermB)) continue;
            if (clauseText.Contains(s.TermA, StringComparison.OrdinalIgnoreCase))
                synonymMatches.Add(new ExpansionMatch(s.Id, s.TermA, s.TermB, s.TermA, s.TermB));
            if (clauseText.Contains(s.TermB, StringComparison.OrdinalIgnoreCase))
                synonymMatches.Add(new ExpansionMatch(s.Id, s.TermA, s.TermB, s.TermB, s.TermA));
        }

        return new QueryExpansionResult(acronymMatches, synonymMatches);
    }

    /// <summary>Flattened form of <see cref="ExpandQueryDetailedAsync"/> — the list of added
    /// terms only, for a caller that just needs text to embed and doesn't need per-match
    /// provenance.</summary>
    public async Task<IReadOnlyList<string>> ExpandQueryAsync(string clauseText, CancellationToken ct = default) =>
        (await ExpandQueryDetailedAsync(clauseText, ct)).AllTerms;
}
