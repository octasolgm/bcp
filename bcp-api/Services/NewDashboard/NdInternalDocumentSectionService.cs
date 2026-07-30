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

        doc.SectionExtractStatus = "processing";
        doc.SectionExtractError = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var clauses = await ExtractPolicyClausesAsync(doc, ct);
            var sections = await ReplaceLibrarySectionsAsync(doc, clauses, ct);

            doc.SectionExtractStatus = "extracted";
            doc.SectionCount = sections.Count;
            doc.SectionExtractedAt = DateTimeOffset.UtcNow;
            doc.SectionExtractedBy = extractedBy;
            doc.SectionExtractError = null;
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
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
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

        var cachedJson = await cache.GetExtractPointsJsonAsync(
            cacheKey,
            LandingAiPolicyClauseExtractService.PolicyClausesSchemaKey,
            ct);
        if (string.IsNullOrWhiteSpace(cachedJson))
            return [];

        var clauses = PolicyClauseExtractNormalizer.DedupeClauseNumbers(
            PolicyClauseParser.ParseFromExtractJson(cachedJson));
        if (clauses.Count == 0)
            return [];

        var sections = await ReplaceLibrarySectionsAsync(doc, clauses, ct);
        doc.SectionExtractStatus = "extracted";
        doc.SectionCount = sections.Count;
        doc.SectionExtractedAt ??= DateTimeOffset.UtcNow;
        if (extractedBy.HasValue)
            doc.SectionExtractedBy = extractedBy;
        doc.SectionExtractError = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Imported {Count} sections from per-document extract cache into library for doc {DocId}",
            sections.Count,
            doc.Id);

        return sections;
    }

    private async Task<List<PolicyClause>> ExtractPolicyClausesAsync(StoredDocument doc, CancellationToken ct)
    {
        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        var payload = await internalParse.EnsureParsedAsync(doc, bytes, ct);
        var fileName = payload.FileName ?? doc.OriginalFileName ?? doc.Title ?? "policy.pdf";
        var cacheKey = payload.FileHash;

        return (await policyClauseExtract.ExtractFromMarkdownAsync(
            cacheKey,
            fileName,
            payload.Markdown,
            ct)).ToList();
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
