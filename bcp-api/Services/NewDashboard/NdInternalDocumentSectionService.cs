using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Landing AI policy-clause extract for internal document library (mirror regulation point extract).
/// Persists to nd_internal_document_sections; Regul reverse phase reuses these when present.
/// </summary>
public class NdInternalDocumentSectionService(
    AppDbContext db,
    NdInternalParseService internalParse,
    LandingAiPolicyClauseExtractService policyClauseExtract,
    LandingAiCacheRepository cache,
    SupabaseStorageService storage,
    ILogger<NdInternalDocumentSectionService> logger)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> RunningExtracts = new();
    /// <summary>Mark <c>processing</c> as failed after this — hung Landing calls hold the in-memory lock until cleared.</summary>
    private static readonly TimeSpan StaleProcessingAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OrphanedProcessingAfter = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<NdInternalDocumentSection>> ListSectionsAsync(
        Guid storedDocumentId,
        CancellationToken ct = default)
    {
        return await db.NdInternalDocumentSections
            .AsNoTracking()
            .Where(s => s.StoredDocumentId == storedDocumentId)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.SectionRef)
            .ToListAsync(ct);
    }

    /// <summary>
    /// If extract was left in <c>processing</c> after API restart or a hung Landing call, mark failed so UI can retry.
    /// </summary>
    public async Task<StoredDocument?> RecoverStaleSectionExtractIfNeededAsync(
        Guid storedDocumentId,
        CancellationToken ct = default)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct);
        if (doc == null) return null;

        if (!string.Equals(doc.SectionExtractStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return doc;

        var orphaned = !RunningExtracts.ContainsKey(storedDocumentId);
        var tooOld = doc.UpdatedAt <= DateTimeOffset.UtcNow - StaleProcessingAfter;
        var hungOrRestarted = orphaned && doc.UpdatedAt <= DateTimeOffset.UtcNow - OrphanedProcessingAfter;

        if (!tooOld && !hungOrRestarted)
            return doc;

        RunningExtracts.TryRemove(storedDocumentId, out _);
        var ageMin = (DateTimeOffset.UtcNow - doc.UpdatedAt).TotalMinutes;
        doc.SectionExtractStatus = "failed";
        doc.SectionExtractError =
            "Section extract did not finish (Landing AI slow, timeout, or API restart). Retry extract once.";
        doc.SectionExtractProgressLabel = null;
        doc.SectionExtractProgressPct = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Recovered stale section extract as failed for doc {DocId} (orphaned={Orphaned}, ageMin={Age:F1})",
            storedDocumentId,
            orphaned,
            ageMin);
        return doc;
    }

    public async Task RecoverAllStaleSectionExtractsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - StaleProcessingAfter;
        var staleIds = await db.StoredDocuments
            .Where(d =>
                d.SectionExtractStatus == "processing"
                && d.UpdatedAt <= cutoff
                && (d.DocKind == "document" || d.DocKind == "internal"))
            .Select(d => d.Id)
            .ToListAsync(ct);

        foreach (var id in staleIds)
            await RecoverStaleSectionExtractIfNeededAsync(id, ct);
    }

    public async Task<IReadOnlyList<NdInternalDocumentSection>> ExtractAndSaveSectionsAsync(
        Guid storedDocumentId,
        Guid? extractedBy,
        bool force = false,
        CancellationToken ct = default)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct)
            ?? throw new InvalidOperationException("Internal document not found.");

        var existing = await db.NdInternalDocumentSections
            .Where(s => s.StoredDocumentId == storedDocumentId)
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync(ct);

        if (!force
            && existing.Count > 0
            && string.Equals(doc.SectionExtractStatus, "extracted", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Using {Count} saved library sections for {DocId} (no Landing AI call)",
                existing.Count,
                storedDocumentId);
            return existing;
        }

        if (!force && existing.Count == 0)
        {
            var hydrated = await TryHydrateSectionsFromExtractCacheAsync(doc, extractedBy, ct);
            if (hydrated.Count > 0)
                return hydrated;
        }

        if (string.Equals(doc.ParseStatus, "parsed", StringComparison.OrdinalIgnoreCase) == false)
            throw new InvalidOperationException("Document must be parsed before section extract. Run parse first.");

        if (!storage.IsConfigured)
            throw new InvalidOperationException("Supabase Storage not configured.");

        if (string.IsNullOrWhiteSpace(doc.StoragePath))
            throw new InvalidOperationException("Document has no storage path.");

        if (!force
            && string.Equals(doc.SectionExtractStatus, "processing", StringComparison.OrdinalIgnoreCase)
            && doc.UpdatedAt > DateTimeOffset.UtcNow - StaleProcessingAfter)
        {
            throw new InvalidOperationException(
                "Section extract is already running for this document. " +
                "Landing AI may take several minutes (3 chunks). Do not click Extract again — credits are charged per chunk.");
        }

        if (!force
            && string.Equals(doc.SectionExtractStatus, "processing", StringComparison.OrdinalIgnoreCase)
            && doc.UpdatedAt <= DateTimeOffset.UtcNow - StaleProcessingAfter)
        {
            logger.LogWarning(
                "Section extract for {DocId} was stale in processing — allowing retry",
                storedDocumentId);
        }

        if (!RunningExtracts.TryAdd(storedDocumentId, 0))
        {
            throw new InvalidOperationException(
                "Section extract is already running for this document. Wait for the current run to finish.");
        }

        doc.SectionExtractStatus = "processing";
        doc.SectionExtractError = null;
        doc.SectionExtractProgressLabel = "Starting section extract…";
        doc.SectionExtractProgressPct = 5;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            async Task ReportProgress(ExtractionProgressUpdate update) =>
                await ReportSectionProgressAsync(doc, update.Label, update.Percent, ct);

            await ReportProgress(new ExtractionProgressUpdate("Loading parsed document…", 8));
            var clauses = await ExtractPolicyClausesAsync(doc, ReportProgress, ct);
            await ReportProgress(new ExtractionProgressUpdate($"Saving {clauses.Count} sections to library…", 95));
            var sections = await ReplaceLibrarySectionsAsync(doc, clauses, ct);

            doc.SectionExtractStatus = "extracted";
            doc.SectionCount = sections.Count;
            doc.SectionExtractedAt = DateTimeOffset.UtcNow;
            doc.SectionExtractedBy = extractedBy;
            doc.SectionExtractError = null;
            doc.SectionExtractProgressLabel = null;
            doc.SectionExtractProgressPct = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Saved {Count} internal document sections for {DocId}",
                sections.Count,
                storedDocumentId);

            return sections;
        }
        catch (Exception ex)
        {
            doc.SectionExtractStatus = "failed";
            doc.SectionExtractError = ex.Message;
            doc.SectionExtractProgressLabel = null;
            doc.SectionExtractProgressPct = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
        finally
        {
            RunningExtracts.TryRemove(storedDocumentId, out _);
        }
    }

    /// <summary>Library sections if present; otherwise Landing extract, persist to library, and return.</summary>
    public async Task<IReadOnlyList<(string SectionRef, string SectionText, int? SourcePage)>> EnsureSectionsForWorkflowAsync(
        StoredDocument doc,
        CancellationToken ct = default)
    {
        var existing = await db.NdInternalDocumentSections
            .AsNoTracking()
            .Where(s => s.StoredDocumentId == doc.Id)
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            logger.LogInformation(
                "Using {Count} library sections for internal doc {DocId}",
                existing.Count,
                doc.Id);
            return existing.Select(s => (s.SectionRef, s.SectionText, s.SourcePage)).ToList();
        }

        var hydrated = await TryHydrateSectionsFromExtractCacheAsync(doc, null, ct);
        if (hydrated.Count > 0)
        {
            logger.LogInformation(
                "Hydrated {Count} library sections from extract cache for doc {DocId}",
                hydrated.Count,
                doc.Id);
            return hydrated.Select(s => (s.SectionRef, s.SectionText, s.SourcePage)).ToList();
        }

        logger.LogInformation(
            "No library sections for internal doc {DocId} — extracting via Landing AI",
            doc.Id);

        var saved = await ExtractAndSaveSectionsAsync(doc.Id, null, force: false, ct);
        return saved.Select(s => (s.SectionRef, s.SectionText, s.SourcePage)).ToList();
    }

    private async Task<List<NdInternalDocumentSection>> TryHydrateSectionsFromExtractCacheAsync(
        StoredDocument doc,
        Guid? extractedBy,
        CancellationToken ct)
    {
        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);

        var cachedClauses = await policyClauseExtract.TryLoadCachedClausesAsync(
            cacheKey,
            doc.FileHash,
            ct);
        if (cachedClauses is { Count: > 0 })
        {
            doc.SectionExtractProgressLabel = "Importing sections from extract cache…";
            doc.SectionExtractProgressPct = 92;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var sections = await ReplaceLibrarySectionsAsync(doc, cachedClauses.Select(c =>
                new PolicyClause(c.ClauseNo, c.ClauseText, c.SourcePage)).ToList(), ct);
            doc.SectionExtractStatus = "extracted";
            doc.SectionCount = sections.Count;
            doc.SectionExtractedAt ??= DateTimeOffset.UtcNow;
            if (extractedBy.HasValue)
                doc.SectionExtractedBy = extractedBy;
            doc.SectionExtractError = null;
            doc.SectionExtractProgressLabel = null;
            doc.SectionExtractProgressPct = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Imported {Count} sections from extract cache (full or partial chunks) for doc {DocId}",
                sections.Count,
                doc.Id);
            return sections;
        }

        return [];
    }

    private async Task<List<PolicyClause>> ExtractPolicyClausesAsync(
        StoredDocument doc,
        Func<ExtractionProgressUpdate, Task>? reportProgress,
        CancellationToken ct)
    {
        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        var payload = await internalParse.EnsureParsedAsync(doc, bytes, ct);
        var fileName = payload.FileName ?? doc.OriginalFileName ?? doc.Title ?? "policy.pdf";
        var cacheKey = payload.FileHash;
        var contentHash = (doc.FileHash ?? "").Trim();

        return (await policyClauseExtract.ExtractFromMarkdownAsync(
            cacheKey,
            fileName,
            payload.Markdown,
            contentHash.Length > 0 ? contentHash : null,
            reportProgress,
            ct)).ToList();
    }

    private async Task ReportSectionProgressAsync(
        StoredDocument doc,
        string label,
        int? percent,
        CancellationToken ct)
    {
        doc.SectionExtractProgressLabel = label;
        doc.SectionExtractProgressPct = percent;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<NdInternalDocumentSection>> ReplaceLibrarySectionsAsync(
        StoredDocument doc,
        IReadOnlyList<PolicyClause> clauses,
        CancellationToken ct)
    {
        var existing = await db.NdInternalDocumentSections
            .Where(s => s.StoredDocumentId == doc.Id)
            .ToListAsync(ct);
        if (existing.Count > 0)
            db.NdInternalDocumentSections.RemoveRange(existing);

        var sections = new List<NdInternalDocumentSection>();
        for (var i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            sections.Add(new NdInternalDocumentSection
            {
                StoredDocumentId = doc.Id,
                SectionRef = clause.ClauseNo,
                SectionText = clause.ClauseText,
                SourcePage = clause.SourcePage > 0 ? clause.SourcePage : null,
                DisplayOrder = i,
            });
        }

        if (sections.Count == 0)
            throw new InvalidOperationException("No policy sections found in internal document.");

        db.NdInternalDocumentSections.AddRange(sections);
        await db.SaveChangesAsync(ct);
        return sections;
    }
}
