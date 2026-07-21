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

    public async Task<InternalDocPayload> ParseByIdAsync(Guid documentId, CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI is not configured. Set LandingAi:ApiKey in appsettings.");

        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new InvalidOperationException("Internal document not found.");

        if (string.IsNullOrWhiteSpace(doc.StoragePath))
            throw new InvalidOperationException("Document has no storage path.");

        if (!storage.IsConfigured)
            throw new InvalidOperationException("Supabase Storage not configured.");

        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        return await EnsureParsedAsync(doc, bytes, ct);
    }

    public async Task<InternalDocPayload> EnsureParsedAsync(
        StoredDocument doc,
        byte[] pdfBytes,
        CancellationToken ct = default)
    {
        if (!client.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI is not configured. Set LandingAi:ApiKey in appsettings.");

        var hash = !string.IsNullOrWhiteSpace(doc.FileHash)
            ? doc.FileHash.Trim()
            : LandingAiCacheRepository.HashBuffer(pdfBytes);

        var cached = await cache.GetParseCacheAsync(hash, ct);
        if (!string.IsNullOrWhiteSpace(cached?.Markdown))
        {
            await MarkParsedAsync(doc, hash, ct);
            return new InternalDocPayload(
                hash,
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
                "Landing AI internal parse ({File}, {Kb} KB)",
                doc.OriginalFileName,
                pdfBytes.Length / 1024);

            var markdown = await client.ParseDocumentAsync(pdfBytes, doc.OriginalFileName, ct);
            await cache.SaveParseCacheAsync(hash, doc.OriginalFileName, markdown, _opts.ParseModel, ct);
            await MarkParsedAsync(doc, hash, ct);
            return new InternalDocPayload(hash, doc.OriginalFileName, markdown, pdfBytes);
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

    private async Task MarkParsedAsync(StoredDocument doc, string hash, CancellationToken ct)
    {
        var changed = doc.ParseStatus != "parsed"
            || doc.FileHash != hash
            || doc.ParseError != null;

        doc.ParseStatus = "parsed";
        doc.FileHash = hash;
        doc.ParseError = null;
        doc.ParsedAt ??= DateTimeOffset.UtcNow;
        doc.UpdatedAt = DateTimeOffset.UtcNow;

        if (changed)
            await db.SaveChangesAsync(ct);
    }
}
