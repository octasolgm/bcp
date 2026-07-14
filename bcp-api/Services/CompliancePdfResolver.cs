using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services;

public record CompliancePdfContext(
    string FileHash,
    string InternalDocId,
    byte[]? PdfBytes,
    string FileName);

/// <summary>
/// Resolves internal compliance PDF bytes for dual-verify — in-memory session cache,
/// Supabase Storage (by stored document id / file hash), then legacy fallbacks.
/// </summary>
public class CompliancePdfResolver(
    AppDbContext db,
    DualVerifyStoreService store,
    SupabaseStorageService storage,
    IConfiguration config,
    ILogger<CompliancePdfResolver> logger)
{
    public async Task<CompliancePdfContext> ResolveForJobAsync(
        byte[]? uploadedPdf,
        Guid? storedDocumentId,
        string? internalDocIdHint,
        string? fileName,
        CancellationToken ct = default)
    {
        if (uploadedPdf is { Length: > 0 })
        {
            var hash = LandingAiCacheRepository.HashBuffer(uploadedPdf);
            var docId = storedDocumentId?.ToString()
                ?? (Guid.TryParse(internalDocIdHint, out var parsed) ? parsed.ToString() : null)
                ?? internalDocIdHint
                ?? "internal-imptfs";
            return new CompliancePdfContext(hash, docId, uploadedPdf, fileName ?? "compliance.pdf");
        }

        var doc = await FindStoredDocumentAsync(storedDocumentId, internalDocIdHint, null, ct);
        if (doc != null)
        {
            var bytes = await DownloadDocAsync(doc, ct);
            var hash = !string.IsNullOrWhiteSpace(doc.FileHash)
                ? doc.FileHash!
                : bytes is { Length: > 0 }
                    ? LandingAiCacheRepository.HashBuffer(bytes)
                    : AnalysisBundleSeedService.InternalFileHash;
            return new CompliancePdfContext(
                hash,
                doc.Id.ToString(),
                bytes,
                doc.OriginalFileName ?? fileName ?? "compliance.pdf");
        }

        return new CompliancePdfContext(
            AnalysisBundleSeedService.InternalFileHash,
            internalDocIdHint ?? "internal-imptfs",
            null,
            fileName ?? "I M P T F S.pdf");
    }

    public async Task<byte[]?> ResolveForWorkerAsync(DualVerifyJobMessage job, CancellationToken ct = default)
    {
        var cached = store.GetInternalPdf(job.SessionId);
        if (cached is { Length: > 0 }) return cached;

        var fromStorage = await ResolveFromStorageAsync(
            job.SessionId, job.InternalDocId, job.InternalFileHash, ct);
        if (fromStorage is { Length: > 0 })
        {
            store.SetInternalPdf(job.SessionId, fromStorage);
            return fromStorage;
        }

        var envPath = config["DUAL_VERIFY_INTERNAL_PDF_PATH"];
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return await File.ReadAllBytesAsync(envPath, ct);

        foreach (var candidate in DefaultPdfCandidates())
        {
            if (File.Exists(candidate))
                return await File.ReadAllBytesAsync(candidate, ct);
        }

        return null;
    }

    private async Task<byte[]?> ResolveFromStorageAsync(
        Guid sessionId,
        string? internalDocId,
        string? fileHash,
        CancellationToken ct)
    {
        var doc = await FindStoredDocumentAsync(
            Guid.TryParse(internalDocId, out var id) ? id : null,
            internalDocId,
            fileHash,
            ct);

        if (doc == null)
        {
            var session = await db.DualVerifySessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session != null)
            {
                doc = await FindStoredDocumentAsync(
                    Guid.TryParse(session.InternalDocId, out var sid) ? sid : null,
                    session.InternalDocId,
                    session.InternalFileHash,
                    ct);
            }
        }

        return doc == null ? null : await DownloadDocAsync(doc, ct);
    }

    private async Task<StoredDocument?> FindStoredDocumentAsync(
        Guid? storedDocumentId,
        string? internalDocIdHint,
        string? fileHash,
        CancellationToken ct)
    {
        if (storedDocumentId is Guid id)
        {
            var byId = await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, ct);
            if (byId != null) return byId;
        }

        if (Guid.TryParse(internalDocIdHint, out var parsed))
        {
            var byParsed = await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == parsed, ct);
            if (byParsed != null) return byParsed;
        }

        var hash = (fileHash ?? "").Trim();
        if (hash.Length > 0)
        {
            return await db.StoredDocuments.AsNoTracking()
                .Where(d => d.DocKind == "document" && d.FileHash == hash)
                .OrderByDescending(d => d.UpdatedAt)
                .FirstOrDefaultAsync(ct);
        }

        return null;
    }

    private async Task<byte[]?> DownloadDocAsync(StoredDocument doc, CancellationToken ct)
    {
        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(doc.StoragePath))
            return null;

        try
        {
            var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
            if (bytes.Length > 0)
            {
                logger.LogInformation(
                    "Resolved compliance PDF from storage ({File}, {Kb} KB)",
                    doc.OriginalFileName ?? doc.Title,
                    bytes.Length / 1024);
                return bytes;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not download compliance PDF at {Path}", doc.StoragePath);
        }

        return null;
    }

    private static IEnumerable<string> DefaultPdfCandidates()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "apps", "web", "public", "default-docs", "imptfs.pdf");
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", "..", "web", "public", "default-docs", "imptfs.pdf"));
        yield return Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", "..", "..", "web", "public", "default-docs", "imptfs.pdf"));
    }
}
