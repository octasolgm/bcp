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

    public async Task<bool> HasParsedMarkdownAsync(string fileHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) return false;
        var row = await cache.GetParseCacheAsync(fileHash.Trim(), ct);
        return !string.IsNullOrWhiteSpace(row?.Markdown);
    }

    public async Task<string> ResolveDisplayParseStatusAsync(StoredDocument doc, CancellationToken ct = default)
    {
        var status = (doc.ParseStatus ?? "").Trim().ToLowerInvariant();
        return status switch
        {
            "parsed" or "processing" or "failed" or "pending" => status,
            _ => "pending",
        };
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
        catch (Exception ex)
        {
            doc.ParseStatus = "failed";
            doc.ParseError = ex.Message;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw;
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
