using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>Parse internal PDFs to markdown (manual or on-demand during analysis).</summary>
public class NdInternalParseService(
    AppDbContext db,
    LandingAiDocumentParseService documentParse,
    LandingAiHttpClient client,
    LandingAiCacheRepository cache,
    SupabaseStorageService storage,
    IOptions<LandingAiOptions> options,
    ILogger<NdInternalParseService> logger)
{
    private readonly LandingAiOptions _opts = options.Value;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> RunningParses = new();
    /// <summary>Mark <c>processing</c> as failed after this — hung Landing calls hold status until cleared.</summary>
    private static readonly TimeSpan StaleProcessingAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OrphanedProcessingAfter = TimeSpan.FromMinutes(2);

    public async Task<bool> HasParsedMarkdownAsync(string fileHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) return false;
        var row = await cache.GetParseCacheAsync(fileHash.Trim(), ct);
        return !string.IsNullOrWhiteSpace(row?.Markdown);
    }

    public async Task<string> ResolveDisplayParseStatusAsync(StoredDocument doc, CancellationToken ct = default)
    {
        var recovered = await RecoverStaleParseIfNeededAsync(doc.Id, ct);
        var status = (recovered?.ParseStatus ?? doc.ParseStatus ?? "").Trim().ToLowerInvariant();
        return status switch
        {
            "parsed" or "processing" or "failed" or "pending" => status,
            _ => "pending",
        };
    }

    /// <summary>
    /// If parse was left in <c>processing</c> after API restart or a hung Landing call, mark failed (or parsed if cache exists).
    /// </summary>
    public async Task<StoredDocument?> RecoverStaleParseIfNeededAsync(
        Guid documentId,
        CancellationToken ct = default)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc == null) return null;

        if (!string.Equals(doc.ParseStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return doc;

        var hash = doc.FileHash?.Trim();
        if (!string.IsNullOrWhiteSpace(hash))
        {
            var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);
            var cached = await cache.ResolveParseCacheAsync(cacheKey, hash, _opts.ParseModel, ct);
            if (!string.IsNullOrWhiteSpace(cached?.Markdown))
            {
                await MarkParsedAsync(doc, hash, null, ct);
                logger.LogInformation(
                    "Recovered stale parse as parsed for doc {DocId} (markdown already in cache)",
                    documentId);
                return doc;
            }
        }

        var orphaned = !RunningParses.ContainsKey(documentId);
        var tooOld = doc.UpdatedAt <= DateTimeOffset.UtcNow - StaleProcessingAfter;
        var hungOrRestarted = orphaned && doc.UpdatedAt <= DateTimeOffset.UtcNow - OrphanedProcessingAfter;

        if (!tooOld && !hungOrRestarted)
            return doc;

        RunningParses.TryRemove(documentId, out _);
        var ageMin = (DateTimeOffset.UtcNow - doc.UpdatedAt).TotalMinutes;
        doc.ParseStatus = "failed";
        doc.ParseError =
            "Parse did not finish (Landing AI slow, timeout, or API restart). Retry parse once.";
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Recovered stale parse as failed for doc {DocId} (orphaned={Orphaned}, ageMin={Age:F1})",
            documentId,
            orphaned,
            ageMin);
        return doc;
    }

    public async Task RecoverAllStaleParsesAsync(CancellationToken ct = default)
    {
        var processingIds = await db.StoredDocuments
            .Where(d =>
                (d.DocKind == "document" || d.DocKind == "internal")
                && d.ParseStatus != null
                && d.ParseStatus.ToLower() == "processing")
            .Select(d => d.Id)
            .ToListAsync(ct);

        foreach (var id in processingIds)
            await RecoverStaleParseIfNeededAsync(id, ct);
    }

    public async Task<InternalDocPayload> ParseByIdAsync(
        Guid documentId,
        Guid? parsedBy = null,
        CancellationToken ct = default)
    {
        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new InvalidOperationException("Internal document not found.");

        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI is not configured. Set LandingAi:ApiKey in appsettings.");

        if (string.IsNullOrWhiteSpace(doc.StoragePath))
            throw new InvalidOperationException("Document has no storage path.");

        if (!storage.IsConfigured)
            throw new InvalidOperationException("Supabase Storage not configured.");

        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        return await EnsureParsedAsync(doc, bytes, ct, parsedBy);
    }

    public async Task<InternalDocPayload> EnsureParsedAsync(
        StoredDocument doc,
        byte[] pdfBytes,
        CancellationToken ct = default,
        Guid? parsedBy = null)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI is not configured. Set LandingAi:ApiKey in appsettings.");

        var hash = LandingAiCacheRepository.HashBuffer(pdfBytes);
        if (string.IsNullOrWhiteSpace(doc.FileHash))
            doc.FileHash = hash;
        else
            hash = doc.FileHash.Trim();

        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(db, doc, ct);
        var cached = await cache.ResolveParseCacheAsync(cacheKey, hash, _opts.ParseModel, ct);
        if (!string.IsNullOrWhiteSpace(cached?.Markdown))
        {
            await MarkParsedAsync(doc, hash, parsedBy, ct);
            return new InternalDocPayload(
                cacheKey,
                doc.OriginalFileName,
                cached.Markdown,
                pdfBytes);
        }

        if (string.Equals(doc.ParseStatus, "processing", StringComparison.OrdinalIgnoreCase)
            && !RunningParses.ContainsKey(doc.Id)
            && doc.UpdatedAt > DateTimeOffset.UtcNow - OrphanedProcessingAfter)
        {
            throw new InvalidOperationException(
                "Parse is already running for this document. Landing AI may take several minutes — do not click Parse again.");
        }

        if (!RunningParses.TryAdd(doc.Id, 0))
        {
            throw new InvalidOperationException(
                "Parse is already running for this document. Wait for the current run to finish.");
        }

        doc.ParseStatus = "processing";
        doc.ParseError = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            logger.LogInformation(
                "Internal document parse ({File}, {Kb} KB)",
                doc.OriginalFileName,
                pdfBytes.Length / 1024);

            var fileName = doc.OriginalFileName
                ?? Path.GetFileName(doc.StoragePath)
                ?? doc.Title
                ?? "document";
            var markdown = await documentParse.ParseToMarkdownAsync(pdfBytes, fileName, ct);
            await cache.SaveParseCacheAsync(cacheKey, fileName, markdown, _opts.ParseModel, ct);
            await MarkParsedAsync(doc, hash, parsedBy, ct);
            return new InternalDocPayload(cacheKey, doc.OriginalFileName ?? fileName, markdown, pdfBytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            doc.ParseStatus = "failed";
            doc.ParseError =
                "Parse was cancelled (browser closed, tab left, or request timeout). Retry parse once.";
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            doc.ParseStatus = "failed";
            doc.ParseError = ex.Message;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
        }
        finally
        {
            RunningParses.TryRemove(doc.Id, out _);
        }
    }

    private async Task MarkParsedAsync(
        StoredDocument doc,
        string hash,
        Guid? parsedBy,
        CancellationToken ct)
    {
        var changed = doc.ParseStatus != "parsed"
            || doc.FileHash != hash
            || doc.ParseError != null
            || (parsedBy.HasValue && doc.ParsedBy != parsedBy);

        doc.ParseStatus = "parsed";
        doc.FileHash = hash;
        doc.ParseError = null;
        doc.ParsedAt ??= DateTimeOffset.UtcNow;
        if (parsedBy.HasValue)
            doc.ParsedBy = parsedBy;
        doc.UpdatedAt = DateTimeOffset.UtcNow;

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
