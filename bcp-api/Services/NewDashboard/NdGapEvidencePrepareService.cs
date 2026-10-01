using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.Storage;
using Reguliq.Api.Workers;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Parse / extract / index gap-evidence uploads so Regul reruns can use the same local-docs
/// pipeline as analyse-regul-full-v2 (Azure DI + section index for hybrid runs).
/// </summary>
public sealed class NdGapEvidencePrepareService(
    AppDbContext db,
    SupabaseStorageService storage,
    LocalDocumentExtractionService extraction,
    NdInternalParseService internalParse,
    NdInternalDocumentSectionService internalSectionService,
    IndexingJobQueue indexingQueue,
    DictionaryExpansionService dictionary,
    ILogger<NdGapEvidencePrepareService> logger)
{
    public const string DefaultLocalEngine = OcrEngineNames.AzureDocIntelligence;
    private static readonly TimeSpan IndexWaitTimeout = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan IndexPollInterval = TimeSpan.FromSeconds(2);

    public async Task PrepareDocumentsAsync(
        IReadOnlyList<Guid> storedDocumentIds,
        string? workflowEngine,
        CancellationToken ct = default)
    {
        if (storedDocumentIds.Count == 0) return;

        foreach (var docId in storedDocumentIds.Distinct())
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            try
            {
                // Same azure-di parse → extract → index path as analyse-regul-full-v2 / internal-documents-azure-di.
                // Judgment LLM remains the admin-configured Regul workflow model at rerun time.
                if (AnalysisWorkflowEngine.IsRegulFamily(workflowEngine)
                    || string.Equals(workflowEngine, AnalysisWorkflowEngine.BcpLanding, StringComparison.OrdinalIgnoreCase))
                {
                    await PrepareHybridLocalPipelineAsync(docId, ct);
                }
                else
                {
                    await PrepareLegacyRegulParseAsync(docId, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gap evidence prepare failed for stored document {DocId}", docId);
                throw;
            }
        }
    }

    private async Task PrepareLegacyRegulParseAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct)
            ?? throw new InvalidOperationException("Gap evidence document not found.");

        if (string.Equals(doc.ParseStatus, "parsed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(doc.SectionExtractStatus, "extracted", StringComparison.OrdinalIgnoreCase))
            return;

        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            throw new InvalidOperationException("Gap evidence file is not available in storage.");

        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        await internalParse.EnsureParsedAsync(doc, bytes, ct);
        await internalSectionService.EnsureSectionsForWorkflowAsync(doc, ct);
    }

    private async Task PrepareHybridLocalPipelineAsync(Guid storedDocumentId, CancellationToken ct)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId, ct)
            ?? throw new InvalidOperationException("Gap evidence document not found.");

        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            throw new InvalidOperationException("Gap evidence file is not available in storage.");

        var fileName = doc.OriginalFileName ?? doc.Title ?? Path.GetFileName(doc.StoragePath) ?? "document";
        if (!SupportedDocumentTypes.IsSupported(fileName))
            throw new InvalidOperationException(
                $"'{Path.GetExtension(fileName)}' is not supported for gap evidence local parse.");

        var engine = DefaultLocalEngine;
        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == storedDocumentId && x.Engine == engine, ct);
        if (row == null)
        {
            row = new NdLocalDocumentExtraction
            {
                Id = Guid.NewGuid(),
                StoredDocumentId = storedDocumentId,
                Engine = engine,
            };
            db.NdLocalDocumentExtractions.Add(row);
            await db.SaveChangesAsync(ct);
        }

        if (row.IndexStatus == "indexed" && row.ExtractStatus == "extracted" && row.Status == "parsed")
        {
            await SyncStoredDocumentParseStatusAsync(doc, "parsed", null, ct);
            return;
        }

        if (row.Status != "parsed" || string.IsNullOrWhiteSpace(row.MarkdownText))
            await ParseLocalRowAsync(doc, row, fileName, engine, ct);

        if (row.ExtractStatus != "extracted" || row.SectionCount is null or 0)
            await ExtractLocalRowAsync(doc, row, fileName, storedDocumentId, ct);

        if (row.IndexStatus != "indexed")
            await WaitForIndexedAsync(row.Id, ct);

        await SyncStoredDocumentParseStatusAsync(doc, "parsed", null, ct);
    }

    private async Task ParseLocalRowAsync(
        StoredDocument doc,
        NdLocalDocumentExtraction row,
        string fileName,
        string engine,
        CancellationToken ct)
    {
        await SyncStoredDocumentParseStatusAsync(doc, "processing", null, ct);
        row.Status = "processing";
        row.Error = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (!OcrEngineNames.IsAzureDocIntelligence(engine))
            throw new NotSupportedException($"Gap evidence local prepare only supports engine '{DefaultLocalEngine}'.");

        var signedUrl = await storage.CreateSignedUrlAsync(doc.StoragePath!, expiresInSeconds: 900, ct);

        LocalParseResult result;
        try
        {
            result = await extraction.ParseWithAzureDocIntelligenceAsync(signedUrl, fileName, ct);
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.Error = ex.Message;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await SyncStoredDocumentParseStatusAsync(doc, "failed", ex.Message, ct);
            throw;
        }

        row.Status = "parsed";
        row.TotalPages = result.TotalPages;
        row.OcrPageCount = result.OcrPageCount;
        row.MarkdownText = result.Markdown;
        row.WarningsJson = JsonSerializer.Serialize(result.Warnings);
        row.Error = null;
        row.ParsedAt = DateTimeOffset.UtcNow;
        row.ExtractStatus = "pending";
        row.SectionCount = null;
        row.SectionsJson = "[]";
        row.ExtractError = null;
        row.ExtractedAt = null;
        row.IndexStatus = "pending";
        row.IndexError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ExtractLocalRowAsync(
        StoredDocument doc,
        NdLocalDocumentExtraction row,
        string fileName,
        Guid storedDocumentId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(row.MarkdownText))
            throw new InvalidOperationException("Gap evidence parse produced no markdown.");

        row.ExtractStatus = "processing";
        row.ExtractError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        LocalExtractionResult result;
        try
        {
            result = extraction.ExtractFromMarkdown(
                fileName, row.MarkdownText, row.TotalPages ?? 0, row.OcrPageCount ?? 0);
        }
        catch (Exception ex)
        {
            row.ExtractStatus = "failed";
            row.ExtractError = ex.Message;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }

        row.ExtractStatus = "extracted";
        row.SectionCount = result.Sections.Count;
        row.SectionsJson = JsonSerializer.Serialize(result.Sections);
        row.WarningsJson = JsonSerializer.Serialize(result.Warnings);
        row.ExtractError = null;
        row.ExtractedAt = DateTimeOffset.UtcNow;
        row.IndexStatus = "pending";
        row.IndexError = null;
        ApplyStructuralCoverage(row, result.Sections);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        indexingQueue.Enqueue(new IndexingJobMessage(row.Id));

        try
        {
            await dictionary.HarvestFromDocumentAsync(row.MarkdownText, storedDocumentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Acronym harvest failed for gap evidence {DocId}", storedDocumentId);
        }

        try
        {
            await dictionary.HarvestSynonymCandidatesAsync(result.Sections, storedDocumentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Synonym harvest failed for gap evidence {DocId}", storedDocumentId);
        }
    }

    private async Task WaitForIndexedAsync(Guid extractionId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + IndexWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            var row = await db.NdLocalDocumentExtractions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == extractionId, ct);
            if (row == null)
                throw new InvalidOperationException("Gap evidence extraction row disappeared during indexing.");
            if (row.IndexStatus == "indexed") return;
            if (row.IndexStatus == "failed")
                throw new InvalidOperationException(
                    row.IndexError ?? "Gap evidence indexing failed — retry upload or reindex from local docs.");
            await Task.Delay(IndexPollInterval, ct);
        }

        throw new InvalidOperationException(
            "Gap evidence indexing is still running. Wait a minute and click Rerun all gaps again.");
    }

    private async Task SyncStoredDocumentParseStatusAsync(
        StoredDocument doc,
        string status,
        string? error,
        CancellationToken ct)
    {
        doc.ParseStatus = status;
        doc.ParseError = error;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static void ApplyStructuralCoverage(NdLocalDocumentExtraction row, IReadOnlyList<LocalSection> sections)
    {
        if (string.IsNullOrWhiteSpace(row.MarkdownText))
        {
            row.StructuralCoverageRatio = null;
            row.StructuralCoverageOrphanSnippet = null;
            return;
        }

        var report = LocalStructuralCoverage.Compute(row.MarkdownText, sections);
        row.StructuralCoverageRatio = report.CoverageRatio;
        row.StructuralCoverageOrphanSnippet = report.OrphanSnippet;
    }
}
