using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Services.LocalDocs;

/// <summary>
/// Builds and embeds the search passages of one indexed document (retrieval pipeline v4+). Called by the indexing
/// job right after sections are embedded, and by retrieval for a document indexed before passages existed or
/// embedded with another model, so a run never searches a document without passages.
/// </summary>
public sealed class NdPassageIndexService(
    AppDbContext db,
    PassageEmbeddingService embeddings,
    ILogger<NdPassageIndexService> logger)
{
    // One build at a time per extraction in this process: two analyses (or an analysis and the indexing job)
    // reaching the same document together would otherwise both write a full set of passages.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> BuildLocks = new();

    /// <summary>
    /// The extraction each document is searched with: one per document, Azure Document Intelligence first, then
    /// the most recently indexed. A file indexed under several OCR engines would otherwise put the same text into
    /// the search more than once.
    /// </summary>
    public static List<NdLocalDocumentExtraction> PickSearchExtractions(IEnumerable<NdLocalDocumentExtraction> indexed) =>
        indexed
            .GroupBy(e => e.StoredDocumentId)
            .Select(g => g
                .OrderByDescending(e => OcrEngineNames.IsAzureDocIntelligence(e.Engine))
                .ThenByDescending(e => e.IndexedAt ?? DateTimeOffset.MinValue)
                .First())
            .ToList();

    /// <summary>True when the extraction has one set of passages embedded with the configured model (a set
    /// written twice by an earlier overlapping build does not count).</summary>
    public async Task<bool> HasCurrentPassagesAsync(Guid extractionId, CancellationToken ct)
    {
        var model = embeddings.ModelName;
        var passages = db.NdLocalDocumentPassages.AsNoTracking().Where(p => p.ExtractionId == extractionId);
        if (!await passages.AnyAsync(p => p.EmbeddingModel == model && p.Embedding != null, ct)) return false;
        var duplicated = await passages
            .GroupBy(p => new { p.SectionIndex, p.PassageIndex })
            .AnyAsync(g => g.Count() > 1, ct);
        return !duplicated;
    }

    /// <summary>Builds the extraction's passages unless it already has a current set. True when it built them.</summary>
    public async Task<bool> EnsureCurrentAsync(Guid extractionId, CancellationToken ct)
    {
        var gate = BuildLocks.GetOrAdd(extractionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await HasCurrentPassagesAsync(extractionId, ct)) return false;
            await RebuildCoreAsync(extractionId, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Replaces the extraction's passages: cut from its stored section rows, embedded with the configured
    /// model. Returns the number of passages written.</summary>
    public async Task<int> RebuildAsync(Guid extractionId, CancellationToken ct)
    {
        var gate = BuildLocks.GetOrAdd(extractionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RebuildCoreAsync(extractionId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> RebuildCoreAsync(Guid extractionId, CancellationToken ct)
    {
        var extraction = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => e.Id == extractionId)
            .Select(e => new { e.Id, e.StoredDocumentId })
            .FirstOrDefaultAsync(ct);
        if (extraction == null) return 0;

        var title = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.Id == extraction.StoredDocumentId)
            .Select(d => string.IsNullOrWhiteSpace(d.Title) ? d.OriginalFileName : d.Title)
            .FirstOrDefaultAsync(ct) ?? "";

        var sectionRows = await db.NdLocalDocumentExtractionSections.AsNoTracking()
            .Where(s => s.ExtractionId == extractionId)
            .OrderBy(s => s.SectionIndex)
            .Select(s => new { s.Id, s.SectionIndex, s.ClauseNo, s.ClauseText, s.SourcePage })
            .ToListAsync(ct);

        // Page blocks (multi-page sections) live in the extraction's SectionsJson; use them when they line up
        // with the stored rows, so each passage carries the page it is on rather than the section's first page.
        var blocks = await LoadPageBlocksAsync(extractionId, sectionRows.Count, ct);
        var sections = sectionRows
            .Select((s, i) => new LocalSection(s.ClauseNo ?? "", s.ClauseText, s.SourcePage, null, blocks?[i]))
            .ToList();
        var passages = LocalPassageSplitter.Split(title, sections);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var vectors = await embeddings.EmbedManyAsync(
            passages.Select(p => string.IsNullOrWhiteSpace(p.HeadingPath) ? p.Text : $"{p.HeadingPath}\n{p.Text}").ToList(), ct);

        var existing = await db.NdLocalDocumentPassages.Where(p => p.ExtractionId == extractionId).ToListAsync(ct);
        db.NdLocalDocumentPassages.RemoveRange(existing);
        var model = embeddings.ModelName;
        var rows = new List<NdLocalDocumentPassage>(passages.Count);
        for (var i = 0; i < passages.Count; i++)
        {
            var p = passages[i];
            rows.Add(new NdLocalDocumentPassage
            {
                Id = Guid.NewGuid(),
                ExtractionId = extractionId,
                SectionId = sectionRows[p.SectionIndex].Id,
                SectionIndex = sectionRows[p.SectionIndex].SectionIndex,
                PassageIndex = p.PassageIndex,
                ClauseNo = p.ClauseNo,
                HeadingPath = p.HeadingPath,
                PassageText = p.Text,
                SourcePage = p.SourcePage,
                Embedding = new Pgvector.Vector(vectors[i]),
                EmbeddingModel = model,
            });
        }

        db.NdLocalDocumentPassages.AddRange(rows);
        await db.SaveChangesAsync(ct);
        // The context may be an analysis run's: do not keep thousands of vectors tracked for the rest of the run.
        foreach (var row in rows) db.Entry(row).State = EntityState.Detached;
        logger.LogInformation(
            "Passages for extraction {ExtractionId}: {Passages} passage(s) from {Sections} section(s), model {Model}, built in {Ms} ms",
            extractionId, passages.Count, sectionRows.Count, model, timer.ElapsedMilliseconds);
        return passages.Count;
    }

    private async Task<List<IReadOnlyList<LocalSectionPageBlock>?>?> LoadPageBlocksAsync(
        Guid extractionId, int sectionCount, CancellationToken ct)
    {
        var json = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => e.Id == extractionId)
            .Select(e => e.SectionsJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<List<LocalSection>>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return stored is { Count: > 0 } && stored.Count == sectionCount
                ? stored.Select(s => s.PageBlocks).ToList()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
