using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.Pdf;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public enum DemoRegulationPipelineMode
{
    ParseOnly,
    ExtractOnly,
    ParseThenExtract,
}

public class NdRegulationUploadService(
    AppDbContext db,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    LandingAiGovExtractService govExtract,
    LandingAiDocumentParseService documentParse,
    LandingAiCacheRepository parseCache,
    NdRegulationPointRepairService pointPageRepair,
    NdDocumentPageReferenceResolver pageResolver,
    IOptions<LandingAiOptions> landingAiOptions,
    IServiceScopeFactory scopeFactory,
    NdDemoUserDirectory demoDirectory,
    ILogger<NdRegulationUploadService> logger)
{
    private static readonly ConcurrentDictionary<Guid, byte> RunningExtracts = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningParses = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningDemoRegulationJobs = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ExtractCancellation = new();
    /// <summary>No in-memory job and status still processing — likely API restart or aborted run.</summary>
    private static readonly TimeSpan StaleProcessingAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StaleParseProcessingAfter = TimeSpan.FromSeconds(45);
    public async Task<NdRegulationDocument> UploadAndExtractAsync(
        byte[] bytes,
        string fileName,
        string contentType,
        Guid? departmentId,
        Guid userId,
        CancellationToken ct)
    {
        if (!storage.IsConfigured)
            throw new InvalidOperationException("Supabase Storage not configured.");

        var baseTitle = Path.GetFileNameWithoutExtension(fileName).Trim();
        var prepared = await uploadPrep.PrepareAsync(
            bytes,
            fileName,
            contentType,
            "regulations/nd",
            ct);

        var title = await AllocateRegulationDisplayNameAsync(baseTitle, prepared.FileHash, ct);

        var pdfPageCount = TryCountPdfPages(bytes, fileName);

        var stored = new Data.Entities.StoredDocument
        {
            Title = title,
            OriginalFileName = prepared.OriginalFileName,
            FileType = prepared.FileType,
            Category = "Regulation",
            FilterKey = "regulation",
            DocKind = "regulation",
            ContentType = prepared.ContentType,
            StorageBucket = storage.Bucket,
            StoragePath = prepared.StoragePath,
            FileHash = prepared.FileHash,
            SizeBytes = prepared.SizeBytes,
            Pages = pdfPageCount > 0
                ? pdfPageCount
                : Math.Max(1, (int)Math.Round(prepared.SizeBytes / 45000.0)),
            UploadedBy = userId,
        };
        stored.ExtractionCacheKey = NdRegulationCacheKeys.ForStoredDocument(stored.Id);
        db.StoredDocuments.Add(stored);
        await db.SaveChangesAsync(ct);

        var regDoc = new NdRegulationDocument
        {
            StoredDocumentId = stored.Id,
            Name = title,
            FilePath = prepared.StoragePath,
            DepartmentId = departmentId,
            ExtractionStatus = "pending",
            CreatedBy = userId,
        };
        db.NdRegulationDocuments.Add(regDoc);
        await db.SaveChangesAsync(ct);

        return regDoc;
    }

    /// <summary>
    /// Recompute stored PDF page references from native PDF text (preferred) or parse cache — no Landing AI calls.
    /// Also re-counts <c>stored_documents.pages</c> from the uploaded PDF (PdfPig).
    /// </summary>
    public async Task<(int PdfPages, int PointsUpdated)> RefreshPointPageReferencesAsync(
        Guid regulationId,
        CancellationToken ct)
    {
        var pdfPages = await SyncStoredPdfPageCountAsync(regulationId, ct);
        var pointsUpdated = await pointPageRepair.RefreshPagesAsync(regulationId, ct);
        return (pdfPages, pointsUpdated);
    }

    /// <summary>Download the regulation PDF and persist accurate page count on stored_documents.</summary>
    public async Task<int> SyncStoredPdfPageCountAsync(Guid regulationId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct);
        if (regDoc?.StoredDocumentId is not Guid storedId) return 0;

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
        if (stored is null) return 0;

        var (bytes, fileName) = await DownloadRegulationBytesAsync(regDoc, ct);
        var count = TryCountPdfPages(bytes, fileName);
        if (count <= 0) return stored.Pages;

        var previous = stored.Pages;
        if (previous != count)
        {
            stored.Pages = count;
            stored.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Updated stored_documents.pages for {StoredId} ({File}): {Old} -> {New}",
                storedId, fileName, previous, count);
        }

        return count;
    }

    private async Task<string> AllocateRegulationDisplayNameAsync(
        string baseTitle,
        string fileHash,
        CancellationToken ct)
    {
        var sameHashCount = await db.StoredDocuments.CountAsync(
            s => s.DocKind == "regulation" && s.FileHash == fileHash,
            ct);
        if (sameHashCount > 0)
            return $"{baseTitle} (v{sameHashCount + 1})";

        var hasVisibleSameName = await db.NdRegulationDocuments.AnyAsync(
            d => d.Status == 1 && d.Name == baseTitle,
            ct);
        if (!hasVisibleSameName)
            return baseTitle;

        return await NextVersionedDisplayNameAsync(baseTitle, ct);
    }

    private async Task<string> NextVersionedDisplayNameAsync(string baseTitle, CancellationToken ct)
    {
        var prefix = baseTitle + " (v";
        var names = await db.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.Status == 1 && (d.Name == baseTitle || d.Name.StartsWith(prefix)))
            .Select(d => d.Name)
            .ToListAsync(ct);

        var maxVersion = 1;
        foreach (var name in names)
        {
            if (string.Equals(name, baseTitle, StringComparison.Ordinal))
                maxVersion = Math.Max(maxVersion, 1);
            else if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(')'))
            {
                var inner = name.Substring(prefix.Length, name.Length - prefix.Length - 1);
                if (int.TryParse(inner, out var v))
                    maxVersion = Math.Max(maxVersion, v);
            }
        }

        return $"{baseTitle} (v{maxVersion + 1})";
    }

    /// <summary>Clear stuck <c>processing</c> when no background job is running (e.g. after API restart).</summary>
    public async Task<NdRegulationDocument?> TryRefreshExtractionStatusAsync(Guid regulationId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct);
        if (regDoc == null) return null;
        await MarkStaleProcessingAsFailedAsync(regDoc, ct);
        return regDoc;
    }

    public async Task<bool> StopExtractAsync(Guid regulationId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct);
        if (regDoc == null) return false;

        if (ExtractCancellation.TryGetValue(regDoc.Id, out var cts))
        {
            cts.Cancel();
            return true;
        }

        if (!string.Equals(regDoc.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return false;

        regDoc.ExtractionStatus = "paused";
        regDoc.ExtractionProgressLabel = "Stopped — click Extract to resume from saved progress.";
        regDoc.ExtractionProgressPct = null;
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Parse regulation PDF to markdown (Landing AI) without extracting points.</summary>
    public async Task<NdRegulationDocument> ParseByRegulationIdAsync(
        Guid regulationId,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await ResolveOrCreateRegulationRowAsync(regulationId, userId, ct);
        await ThrowIfLiveAiForbiddenAsync(userId, regDoc.CreatedBy, ct);
        if (string.Equals(regDoc.ExtractionStatus, "manual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Manual regulation entries do not require parse.");

        if (string.Equals(regDoc.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase)
            && RunningExtracts.ContainsKey(regDoc.Id))
            throw new InvalidOperationException(
                "Extraction is running for this document. Wait for it to finish or stop it first.");

        if (!RunningParses.TryAdd(regDoc.Id, 0))
            throw new InvalidOperationException(
                "Parse is already running for this document. Wait for the current run to finish.");

        try
        {
            regDoc.ExtractionStatus = "processing";
            regDoc.ExtractionProgressLabel = "Parsing document…";
            regDoc.ExtractionProgressPct = 5;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var (bytes, fileName) = await DownloadRegulationBytesAsync(regDoc, ct);
            var landingOpts = landingAiOptions.Value;
            var cacheKey = await ResolveExtractionCacheKeyAsync(db, regDoc, ct);
            var fileHash = LandingAiCacheRepository.HashBuffer(bytes);

            if (regDoc.StoredDocumentId is Guid storedId)
            {
                var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                if (stored != null && string.IsNullOrWhiteSpace(stored.FileHash))
                {
                    stored.FileHash = fileHash;
                    stored.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            string markdown;
            var cached = await parseCache.GetParseCacheAsync(cacheKey, ct);
            if (!string.IsNullOrWhiteSpace(cached?.Markdown))
            {
                markdown = cached.Markdown;
            }
            else
            {
                async Task ReportParseProgress(ExtractionProgressUpdate update)
                {
                    regDoc.ExtractionProgressLabel = update.Label;
                    regDoc.ExtractionProgressPct = update.Percent;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                markdown = await documentParse.ParseToMarkdownAsync(
                    bytes, fileName, ReportParseProgress, null, ct);
                await parseCache.SaveParseCacheAsync(
                    cacheKey, fileName, markdown, landingOpts.ParseModel, ct);
            }

            regDoc.ExtractionMarkdown = markdown;
            regDoc.ExtractionStatus = "parsed";
            regDoc.ExtractionProgressLabel = null;
            regDoc.ExtractionProgressPct = null;
            regDoc.ExtractionParseChunkCompleted = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            if (regDoc.StoredDocumentId is Guid storedIdAfterParse)
            {
                var storedAfterParse = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedIdAfterParse, ct);
                if (storedAfterParse != null)
                {
                    storedAfterParse.ParseStatus = "parsed";
                    storedAfterParse.ParseError = null;
                    var parsedPages = TryCountPdfPages(bytes, fileName);
                    if (parsedPages > 0) storedAfterParse.Pages = parsedPages;
                    storedAfterParse.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            await db.SaveChangesAsync(ct);
            return regDoc;
        }
        finally
        {
            RunningParses.TryRemove(regDoc.Id, out _);
        }
    }

    /// <summary>Resolve ND or legacy stored-document id and queue Landing AI extraction (returns immediately).</summary>
    public async Task<NdRegulationDocument> ExtractByRegulationIdAsync(
        Guid regulationId,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await ResolveOrCreateRegulationRowAsync(regulationId, userId, ct);
        await ThrowIfLiveAiForbiddenAsync(userId, regDoc.CreatedBy, ct);
        await MarkStaleProcessingAsFailedAsync(regDoc, ct);
        var isResume = string.Equals(regDoc.ExtractionStatus, "paused", StringComparison.OrdinalIgnoreCase);
        if (!isResume)
            regDoc.ExtractionParseChunkCompleted = null;
        await BeginExtractAsync(regDoc, userId, isResume, ct);
        QueueExtractJob(regDoc.Id, userId);
        return regDoc;
    }

    public async Task RunExtractJobAsync(Guid docId, Guid userId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var innerStorage = scope.ServiceProvider.GetRequiredService<SupabaseStorageService>();
        var innerGov = scope.ServiceProvider.GetRequiredService<LandingAiGovExtractService>();
        var innerCache = scope.ServiceProvider.GetRequiredService<LandingAiCacheRepository>();
        var landingOpts = scope.ServiceProvider.GetRequiredService<IOptions<LandingAiOptions>>().Value;

        var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        var demoDirectory = scope.ServiceProvider.GetRequiredService<NdDemoUserDirectory>();
        var demoInterception = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
        var demoCtx = await demoDirectory.ResolveContextAsync(new JwtUser(userId, null), ct);
        if (demoInterception.CanMutateRegulationDocument(regDoc, demoCtx))
        {
            try
            {
                await demoInterception.SimulateRegulationExtractAsync(innerDb, regDoc, userId, demoCtx, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo regulation extraction failed for {DocId}", docId);
                regDoc.ExtractionStatus = "failed";
                regDoc.ExtractionProgressLabel = "Extraction failed. Run Extract to try again.";
                regDoc.ExtractionProgressPct = null;
                regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                await innerDb.SaveChangesAsync(ct);
            }
            return;
        }

        byte[] bytes;
        string fileName;
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await innerDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct)
                ?? throw new InvalidOperationException("Stored document not found.");
            bytes = await innerStorage.DownloadAsync(stored.StoragePath, ct);
            fileName = stored.OriginalFileName ?? Path.GetFileName(stored.StoragePath);
        }
        else if (!string.IsNullOrWhiteSpace(regDoc.FilePath))
        {
            bytes = await innerStorage.DownloadAsync(regDoc.FilePath, ct);
            fileName = regDoc.Name + ".pdf";
        }
        else
        {
            throw new InvalidOperationException("No file available for extraction.");
        }

        try
        {
            await ExtractInternalAsync(innerDb, innerGov, innerCache, landingOpts, regDoc, bytes, fileName, userId, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Regulation extraction paused for {DocId}", docId);
            regDoc.ExtractionStatus = "paused";
            var savedPart = regDoc.ExtractionParseChunkCompleted is int c && c >= 0 ? c + 1 : (int?)null;
            regDoc.ExtractionProgressLabel = savedPart is > 0
                ? $"Paused after part {savedPart} — click Extract to resume."
                : "Paused — click Extract to resume from saved progress.";
            regDoc.ExtractionProgressPct = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await innerDb.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regulation extraction failed for {DocId}", docId);
            regDoc.ExtractionStatus = "failed";
            regDoc.ExtractionProgressLabel = "Extraction failed. Run Extract to try again.";
            regDoc.ExtractionProgressPct = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await innerDb.SaveChangesAsync(ct);
        }
    }

    /// <summary>Background demo parse/extract from DB seed — same status progression as live AI, no Landing AI.</summary>
    public bool QueueDemoRegulationJob(Guid docId, Guid userId, DemoRegulationPipelineMode mode)
    {
        if (!RunningDemoRegulationJobs.TryAdd(docId, 0))
        {
            logger.LogInformation("Demo regulation job already running for {DocId} — skipping duplicate queue", docId);
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();

                if (mode is DemoRegulationPipelineMode.ParseOnly or DemoRegulationPipelineMode.ParseThenExtract)
                {
                    var parseDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId);
                    if (parseDoc != null)
                        await demo.SimulateRegulationParseAsync(innerDb, parseDoc, userId, CancellationToken.None);
                }

                if (mode is DemoRegulationPipelineMode.ExtractOnly or DemoRegulationPipelineMode.ParseThenExtract)
                {
                    var extractDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId);
                    if (extractDoc != null)
                        await demo.SimulateRegulationExtractAsync(innerDb, extractDoc, userId, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo regulation pipeline failed for {DocId}", docId);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId);
                    if (regDoc != null)
                    {
                        regDoc.ExtractionStatus = "failed";
                        regDoc.ExtractionProgressLabel = "Processing failed. Try Parse or Extract again.";
                        regDoc.ExtractionProgressPct = null;
                        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                        await innerDb.SaveChangesAsync();
                    }
                }
                catch (Exception markEx)
                {
                    logger.LogWarning(markEx, "Could not mark demo regulation job failed for {DocId}", docId);
                }
            }
            finally
            {
                RunningDemoRegulationJobs.TryRemove(docId, out _);
            }
        });
        return true;
    }

    private void QueueExtractJob(Guid docId, Guid userId)
    {
        if (!RunningExtracts.TryAdd(docId, 0))
        {
            logger.LogInformation("Extract already running for regulation {DocId}", docId);
            return;
        }

        var cts = new CancellationTokenSource();
        ExtractCancellation[docId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunExtractJobAsync(docId, userId, cts.Token);
            }
            finally
            {
                RunningExtracts.TryRemove(docId, out _);
                if (ExtractCancellation.TryRemove(docId, out var linked))
                    linked.Dispose();
            }
        });
    }

    private async Task<NdRegulationDocument> ResolveOrCreateRegulationRowAsync(
        Guid regulationId,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct);

        if (regDoc != null) return regDoc;

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(
                d => d.Id == regulationId && d.DocKind == "regulation", ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        regDoc = new NdRegulationDocument
        {
            StoredDocumentId = stored.Id,
            Name = stored.Title,
            FilePath = "",
            ExtractionStatus = "pending",
            CreatedBy = userId,
        };
        db.NdRegulationDocuments.Add(regDoc);
        await db.SaveChangesAsync(ct);
        return regDoc;
    }

    private async Task BeginExtractAsync(NdRegulationDocument regDoc, Guid userId, bool isResume, CancellationToken ct)
    {
        if (string.Equals(regDoc.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase)
            && RunningExtracts.ContainsKey(regDoc.Id)
            && DateTimeOffset.UtcNow - regDoc.UpdatedAt < StaleProcessingAfter)
        {
            return;
        }

        regDoc.ExtractionStatus = "processing";
        regDoc.ExtractionProgressLabel = isResume
            ? "Resuming extraction…"
            : "Starting extraction…";
        regDoc.ExtractionProgressPct = isResume ? regDoc.ExtractionProgressPct ?? 5 : 5;
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkStaleProcessingAsFailedAsync(NdRegulationDocument regDoc, CancellationToken ct)
    {
        if (!string.Equals(regDoc.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return;
        if (RunningExtracts.ContainsKey(regDoc.Id))
            return;
        if (RunningParses.ContainsKey(regDoc.Id))
            return;
        if (RunningDemoRegulationJobs.ContainsKey(regDoc.Id))
            return;
        if (NdDemoInterceptionService.IsRegulationDemoJobRunning(regDoc.Id))
            return;

        var label = regDoc.ExtractionProgressLabel ?? "";
        var looksLikeParse = label.Contains("Parsing", StringComparison.OrdinalIgnoreCase)
            || label.Contains("markdown", StringComparison.OrdinalIgnoreCase)
            || label.Contains("parse results", StringComparison.OrdinalIgnoreCase)
            || (regDoc.ExtractionProgressPct is null or <= 15);
        var staleAfter = looksLikeParse ? StaleParseProcessingAfter : StaleProcessingAfter;
        if (DateTimeOffset.UtcNow - regDoc.UpdatedAt < staleAfter)
            return;

        regDoc.ExtractionStatus = "failed";
        regDoc.ExtractionProgressLabel = looksLikeParse
            ? "Parse did not finish. Click Parse to try again."
            : "Previous extraction did not finish. Run Extract again.";
        regDoc.ExtractionProgressPct = null;
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<NdRegulationDocument> ExtractAsync(Guid docId, Guid userId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        await MarkStaleProcessingAsFailedAsync(regDoc, ct);
        await BeginExtractAsync(regDoc, userId, isResume: false, ct);

        byte[] bytes;
        string fileName;
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct)
                ?? throw new InvalidOperationException("Stored document not found.");
            bytes = await storage.DownloadAsync(stored.StoragePath, ct);
            fileName = stored.OriginalFileName ?? Path.GetFileName(stored.StoragePath);
        }
        else if (!string.IsNullOrWhiteSpace(regDoc.FilePath))
        {
            bytes = await storage.DownloadAsync(regDoc.FilePath, ct);
            fileName = regDoc.Name + ".pdf";
        }
        else
        {
            throw new InvalidOperationException("No file available for extraction.");
        }

        try
        {
            await ExtractInternalAsync(
                db,
                govExtract,
                parseCache,
                landingAiOptions.Value,
                regDoc,
                bytes,
                fileName,
                userId,
                ct);
        }
        catch
        {
            regDoc.ExtractionStatus = "failed";
            regDoc.ExtractionProgressLabel = "Extraction failed. Run Extract to try again.";
            regDoc.ExtractionProgressPct = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }

        return regDoc;
    }

    private async Task ExtractInternalAsync(
        AppDbContext dbCtx,
        LandingAiGovExtractService gov,
        LandingAiCacheRepository cache,
        LandingAiOptions landingOpts,
        NdRegulationDocument regDoc,
        byte[] bytes,
        string fileName,
        Guid userId,
        CancellationToken ct)
    {
        async Task ReportProgress(ExtractionProgressUpdate update)
        {
            regDoc.ExtractionProgressLabel = update.Label;
            regDoc.ExtractionProgressPct = update.Percent;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);
        }

        var fileHash = LandingAiCacheRepository.HashBuffer(bytes);
        var cacheKey = await ResolveExtractionCacheKeyAsync(dbCtx, regDoc, ct);
        RegulationParseCheckpoint? checkpoint = null;
        if (regDoc.ExtractionParseChunkCompleted is int completed && completed >= 0)
        {
            var cached = await cache.GetParseCacheAsync(cacheKey, ct);
            checkpoint = new RegulationParseCheckpoint
            {
                ResumeFromChunkIndex = completed + 1,
                PartialMarkdown = cached?.Markdown,
                OnChunkParsedAsync = async (chunkIndex, mergedMarkdown) =>
                {
                    regDoc.ExtractionParseChunkCompleted = chunkIndex;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await cache.SaveParseCacheAsync(cacheKey, fileName, mergedMarkdown, landingOpts.ParseModel, ct);
                    await dbCtx.SaveChangesAsync(ct);
                },
            };
        }
        else
        {
            checkpoint = new RegulationParseCheckpoint
            {
                OnChunkParsedAsync = async (chunkIndex, mergedMarkdown) =>
                {
                    regDoc.ExtractionParseChunkCompleted = chunkIndex;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await cache.SaveParseCacheAsync(cacheKey, fileName, mergedMarkdown, landingOpts.ParseModel, ct);
                    await dbCtx.SaveChangesAsync(ct);
                },
            };
        }

        var result = await gov.ExtractFromUploadAsync(
            bytes, fileName, null, ReportProgress, checkpoint, cacheKey, ct);

        var countedPages = TryCountPdfPages(bytes, fileName);
        int? pdfPageCount = countedPages > 0 ? countedPages : null;

        var parseMarkdown = (await cache.GetParseCacheAsync(cacheKey, ct))?.Markdown;
        if (!string.IsNullOrWhiteSpace(parseMarkdown))
            regDoc.ExtractionMarkdown = parseMarkdown;

        await ReportProgress(new ExtractionProgressUpdate($"Saving {result.Points.Count} regulation points…", 90));

        var nativePdf = LandingAiDocumentFormats.IsPdf(fileName, bytes)
            ? PdfNativePageDocument.TryCreate(bytes)
            : null;

        var existingPoints = await dbCtx.NdRegulationPoints
            .IgnoreQueryFilters()
            .Where(p => p.RegulationDocumentId == regDoc.Id && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);
        foreach (var existing in existingPoints)
            existing.Status = NdRegulationPointStatus.Removed;

        var pointsJson = JsonSerializer.Serialize(new { points = result.Points });
        regDoc.ExtractionResult = pointsJson;
        regDoc.ExtractionStatus = "completed";
        regDoc.ExtractionProgressLabel = null;
        regDoc.ExtractionProgressPct = null;
        regDoc.ExtractionParseChunkCompleted = null;
        regDoc.ExtractedAt = DateTimeOffset.UtcNow;
        regDoc.ExtractedBy = userId;
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;

        var order = 0;
        foreach (var p in result.Points)
        {
            var json = JsonSerializer.Serialize(p);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pointId = root.TryGetProperty("point_id", out var pid) ? pid.GetString() ?? "" : "";
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            var section = root.TryGetProperty("section", out var s) ? s.GetString() : null;
            var pointType = root.TryGetProperty("point_type", out var pt) ? pt.GetString() : null;
            int? pageHint = null;
            if (root.TryGetProperty("page_hint", out var ph) && ph.ValueKind == JsonValueKind.Number && ph.TryGetInt32(out var hint) && hint > 0)
                pageHint = hint;

            int? resolvedPage = null;
            if (regDoc.StoredDocumentId is Guid storedId)
            {
                var stored = await dbCtx.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                if (stored is not null)
                {
                    resolvedPage = await pageResolver.ResolveSectionPageAsync(
                        stored,
                        parseMarkdown,
                        pointId,
                        title,
                        text,
                        ct);
                }
            }

            if (resolvedPage is null or <= 0 && nativePdf is not null)
                resolvedPage = nativePdf.ResolveSectionPage(pointId, title, text);

            if (resolvedPage is null or <= 0 && !string.IsNullOrWhiteSpace(parseMarkdown))
            {
                resolvedPage = PolicyPageResolver.ResolveGovPointPage(
                    parseMarkdown,
                    pointId,
                    section,
                    title,
                    text,
                    pageHint,
                    nativePdf?.TotalPages ?? pdfPageCount);
                resolvedPage = PolicyPageResolver.RefinePageGuess(resolvedPage, pointId, nativePdf?.TotalPages ?? pdfPageCount);
            }

            var isAnnex = GovPointClassifier.IsAnnexPoint(pointId, title, section);
            var isIntro = GovPointClassifier.IsIntroductionPoint(pointId, title, text, section, pointType);

            dbCtx.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = regDoc.Id,
                PointNumber = pointId,
                PointTitle = title,
                PointContent = text,
                PageReference = FormatPointPageReference(section ?? pointId, resolvedPage),
                IsIntroductionPoint = isIntro,
                IsAnnexPoint = isAnnex,
            });
            order++;
        }

        if (regDoc.StoredDocumentId is Guid sid)
        {
            var stored = await dbCtx.StoredDocuments.FirstOrDefaultAsync(d => d.Id == sid, ct);
            if (stored != null)
            {
                stored.FileHash = result.FileHash;
                stored.PointCount = result.Points.Count;
                if (nativePdf?.TotalPages is > 0)
                    stored.Pages = nativePdf.TotalPages;
                else if (pdfPageCount is > 0)
                    stored.Pages = pdfPageCount.Value;
                stored.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await dbCtx.SaveChangesAsync(ct);
    }

    /// <summary>PdfPig page count (preferred), PdfSharp fallback, or 0 when not a readable PDF.</summary>
    private static int TryCountPdfPages(byte[] bytes, string fileName)
    {
        try
        {
            if (!LandingAiDocumentFormats.IsPdf(fileName, bytes)) return 0;
            var pigCount = PdfNativePageDocument.TryGetPageCount(bytes);
            if (pigCount > 0) return pigCount;
            return LandingAiDocumentParseService.GetPdfPageCount(bytes);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(byte[] Bytes, string FileName)> DownloadRegulationBytesAsync(
        NdRegulationDocument regDoc,
        CancellationToken ct)
    {
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct)
                ?? throw new InvalidOperationException("Stored document not found.");
            var bytes = await storage.DownloadAsync(stored.StoragePath, ct);
            var fileName = stored.OriginalFileName ?? Path.GetFileName(stored.StoragePath);
            return (bytes, fileName);
        }

        if (!string.IsNullOrWhiteSpace(regDoc.FilePath))
        {
            var bytes = await storage.DownloadAsync(regDoc.FilePath, ct);
            return (bytes, regDoc.Name + ".pdf");
        }

        throw new InvalidOperationException("No file available for this regulation.");
    }

    private static async Task<string> ResolveExtractionCacheKeyAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        CancellationToken ct)
    {
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await dbCtx.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
            if (stored != null)
                return await EnsureExtractionCacheKeyAsync(dbCtx, stored, ct);
        }

        return NdRegulationCacheKeys.ForRegulationDocument(regDoc.Id);
    }

    private static async Task<string> EnsureExtractionCacheKeyAsync(
        AppDbContext dbCtx,
        Data.Entities.StoredDocument stored,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(stored.ExtractionCacheKey))
            return stored.ExtractionCacheKey.Trim();

        stored.ExtractionCacheKey = NdRegulationCacheKeys.ForStoredDocument(stored.Id);
        stored.UpdatedAt = DateTimeOffset.UtcNow;
        await dbCtx.SaveChangesAsync(ct);
        return stored.ExtractionCacheKey;
    }

    private static string? FormatPointPageReference(string? section, int? pdfPage)
    {
        var sec = section?.Trim();
        if (pdfPage is > 0)
            return string.IsNullOrWhiteSpace(sec) ? $"p. {pdfPage}" : $"{sec} · p. {pdfPage}";
        return sec;
    }

    private static string NormalizeKey(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }

    private async Task ThrowIfLiveAiForbiddenAsync(
        Guid actingUserId,
        Guid? resourceOwnerId,
        CancellationToken ct)
    {
        if (await demoDirectory.ShouldSimulateForProfilesAsync(actingUserId, resourceOwnerId, ct))
            throw new InvalidOperationException(
                "Demo accounts and demo-owned documents use simulated processing only (no live AI).");
    }
}
