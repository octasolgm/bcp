using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services;

/// <summary>
/// Registers TFS Guidelines.pdf in stored_documents, links Landing extract cache by file hash,
/// and uploads the PDF to private Supabase Storage when configured.
/// </summary>
public class TfsGuidelinesSeedService(
    AppDbContext db,
    LandingAiCacheRepository cache,
    GovPointsService govPoints,
    SupabaseStorageService storage,
    IWebHostEnvironment env,
    IOptions<LandingAiOptions> landingOpts,
    ILogger<TfsGuidelinesSeedService> logger)
{
    public const string BuiltinFileHash = LandingAiGovExtractService.BuiltinGovFileHash;
    public const string DefaultFileName = "TFS Guidelines.pdf";
    public const string WorkspaceId = "snb-uae-difc";

    public async Task<TfsSeedResult> SeedAsync(
        string? localPdfPath,
        byte[]? uploadedBytes,
        string? uploadedFileName,
        bool forceReupload,
        CancellationToken ct = default)
    {
        var pointsJson = await EnsureExtractCacheAsync(ct);
        using var pointsDoc = JsonDocument.Parse(pointsJson);
        var pointCount = pointsDoc.RootElement.TryGetProperty("points", out var arr)
            && arr.ValueKind == JsonValueKind.Array
            ? arr.GetArrayLength()
            : govPoints.GetAllPoints().Count;

        govPoints.ReloadFromSeed();
        var activeCount = govPoints.GetAllPoints().Count;

        var (bytes, fileName, sourcePath) = await ResolvePdfBytesAsync(
            localPdfPath, uploadedBytes, uploadedFileName, ct);

        string? verifiedHash = null;
        string? storagePath = null;
        var uploadedToStorage = false;

        if (bytes is { Length: > 0 })
        {
            verifiedHash = LandingAiCacheRepository.HashBuffer(bytes);
            if (!string.Equals(verifiedHash, BuiltinFileHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PDF hash {verifiedHash} does not match the built-in extract hash {BuiltinFileHash}. " +
                    "Use the same TFS Guidelines.pdf that the seed points were extracted from.");
            }

            storagePath = $"regulations/{WorkspaceId}/tfs-guidelines/v1/{Sanitize(fileName)}";
            if (storage.IsConfigured)
            {
                await using var stream = new MemoryStream(bytes);
                await storage.UploadAsync(storagePath, stream, "application/pdf", upsert: true, ct);
                uploadedToStorage = true;
                logger.LogInformation("Uploaded TFS Guidelines to Storage at {Path}", storagePath);
            }
        }
        else
        {
            storagePath = $"regulations/{WorkspaceId}/tfs-guidelines/v1/{Sanitize(DefaultFileName)}";
        }

        var row = await UpsertStoredDocumentAsync(
            fileName,
            bytes?.Length ?? 0,
            storagePath,
            pointCount > 0 ? pointCount : activeCount,
            uploadedToStorage,
            ct);

        return new TfsSeedResult(
            true,
            row.Id,
            BuiltinFileHash,
            row.PointCount ?? activeCount,
            uploadedToStorage,
            storage.IsConfigured,
            row.StoragePath,
            sourcePath,
            uploadedToStorage
                ? $"Linked {row.PointCount} extract points to {fileName} and uploaded to Storage."
                : storage.IsConfigured
                    ? $"Linked {row.PointCount} extract points to DB for {fileName} (no local PDF found to upload)."
                    : $"Linked {row.PointCount} extract points to DB for {fileName}. " +
                      "Storage upload skipped — set Supabase ServiceRoleKey, then call this endpoint again.");
    }

    private async Task<string> EnsureExtractCacheAsync(CancellationToken ct)
    {
        var existing = await cache.GetExtractPointsJsonAsync(
            BuiltinFileHash, LandingAiGovExtractService.GovSchemaKey, ct);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var seedPath = Path.Combine(env.ContentRootPath, "SeedData", "gov-tfs-guidelines.extract.json");
        if (!File.Exists(seedPath))
            throw new InvalidOperationException($"Seed extract missing at {seedPath}");

        var raw = await File.ReadAllTextAsync(seedPath, ct);
        using var doc = JsonDocument.Parse(raw);
        string pointsJson;
        if (doc.RootElement.TryGetProperty("points", out var points))
            pointsJson = JsonSerializer.Serialize(new { points });
        else
            pointsJson = raw;

        await cache.SaveExtractPointsCacheAsync(
            BuiltinFileHash,
            LandingAiGovExtractService.GovSchemaKey,
            pointsJson,
            landingOpts.Value.ExtractModel,
            ct);

        logger.LogInformation("Seeded landing_ai_extract_cache for TFS hash {Hash}", BuiltinFileHash);
        return pointsJson;
    }

    private async Task<(byte[]? Bytes, string FileName, string? SourcePath)> ResolvePdfBytesAsync(
        string? localPdfPath,
        byte[]? uploadedBytes,
        string? uploadedFileName,
        CancellationToken ct)
    {
        if (uploadedBytes is { Length: > 0 })
            return (uploadedBytes, string.IsNullOrWhiteSpace(uploadedFileName) ? DefaultFileName : uploadedFileName, "upload");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(localPdfPath))
            candidates.Add(localPdfPath.Trim());

        candidates.Add(Path.Combine(env.ContentRootPath, "SeedData", DefaultFileName));
        candidates.Add(Path.Combine(env.ContentRootPath, "data", "seed-docs", DefaultFileName));
        candidates.Add(@"C:\Users\Hp\Downloads\bundle\TFS Guidelines.pdf");

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return (bytes, Path.GetFileName(path), path);
        }

        return (null, DefaultFileName, null);
    }

    private async Task<StoredDocument> UpsertStoredDocumentAsync(
        string fileName,
        long sizeBytes,
        string storagePath,
        int pointCount,
        bool uploadedToStorage,
        CancellationToken ct)
    {
        const string title = "TFS Guidelines";
        const string kind = "regulation";

        var existing = await db.StoredDocuments
            .Where(d => d.WorkspaceId == WorkspaceId && d.DocKind == kind)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefaultAsync(d => d.Title.ToLower() == title.ToLower() || d.FileHash == BuiltinFileHash, ct);

        var history = new List<string>
        {
            uploadedToStorage
                ? $"v1 seeded+uploaded {DateTime.UtcNow:u} · linked extract {BuiltinFileHash[..12]}…"
                : $"v1 seeded {DateTime.UtcNow:u} · linked extract {BuiltinFileHash[..12]}… (storage pending)",
        };

        if (existing != null)
        {
            existing.OriginalFileName = fileName;
            existing.Title = title;
            existing.Category = "Sanctions";
            existing.FilterKey = "sanctions";
            existing.DocKind = kind;
            existing.Version = "v1";
            existing.VersionNumber = Math.Max(1, existing.VersionNumber);
            existing.Status = "reviewed";
            existing.Pages = sizeBytes > 0 ? Math.Max(1, (int)Math.Round(sizeBytes / 45000.0)) : existing.Pages;
            if (sizeBytes > 0) existing.SizeBytes = sizeBytes;
            existing.ContentType = "application/pdf";
            existing.StorageBucket = storage.Bucket;
            if (!string.IsNullOrWhiteSpace(storagePath))
                existing.StoragePath = storagePath;
            existing.FileHash = BuiltinFileHash;
            existing.PointCount = pointCount;
            existing.HistoryJson = JsonSerializer.Serialize(history);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var row = new StoredDocument
        {
            Title = title,
            OriginalFileName = fileName,
            FileType = "PDF",
            Category = "Sanctions",
            FilterKey = "sanctions",
            DocKind = kind,
            Version = "v1",
            VersionNumber = 1,
            Status = "reviewed",
            Pages = sizeBytes > 0 ? Math.Max(1, (int)Math.Round(sizeBytes / 45000.0)) : 48,
            SizeBytes = sizeBytes,
            ContentType = "application/pdf",
            StorageBucket = storage.Bucket,
            StoragePath = storagePath,
            FileHash = BuiltinFileHash,
            PointCount = pointCount,
            WorkspaceId = WorkspaceId,
            HistoryJson = JsonSerializer.Serialize(history),
        };
        db.StoredDocuments.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static string Sanitize(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }
}

public sealed record TfsSeedResult(
    bool Success,
    Guid DocumentId,
    string FileHash,
    int PointCount,
    bool UploadedToStorage,
    bool StorageConfigured,
    string StoragePath,
    string? SourcePdfPath,
    string Message);
