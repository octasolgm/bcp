using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services;

/// <summary>
/// Uploads the default TFS + IMPTFS pair to Storage, registers stored_documents rows,
/// and links the 32-point combined compliance session to those file hashes.
/// </summary>
public class AnalysisBundleSeedService(
    AppDbContext db,
    TfsGuidelinesSeedService tfsSeed,
    SupabaseStorageService storage,
    IWebHostEnvironment env,
    ILogger<AnalysisBundleSeedService> logger)
{
    public const string InternalFileHash =
        "6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717";
    public const string InternalFileName = "I M P T F S.pdf";
    public const string WorkspaceId = TfsGuidelinesSeedService.WorkspaceId;
    public static readonly Guid CombinedSessionId =
        Guid.Parse("a339de5e-06b9-4067-bd97-e7d8086bf31e");

    public async Task<BundleSeedResult> SeedAsync(
        string? tfsLocalPath,
        string? imptfsLocalPath,
        Guid? complianceSessionId,
        CancellationToken ct = default)
    {
        var tfs = await tfsSeed.SeedAsync(tfsLocalPath, null, null, forceReupload: true, ct);

        var (imptfsBytes, imptfsName, imptfsSource) = await ResolveImptfsAsync(imptfsLocalPath, ct);
        if (imptfsBytes is null || imptfsBytes.Length == 0)
            throw new InvalidOperationException(
                "IMPTFS PDF not found. Pass localPath or place at data/seed-docs/I M P T F S.pdf");

        var hash = LandingAiCacheRepository.HashBuffer(imptfsBytes);
        if (!string.Equals(hash, InternalFileHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"IMPTFS hash {hash} does not match expected {InternalFileHash}. " +
                "Use the same I M P T F S.pdf the 32-point analysis was run against.");
        }

        var storagePath = $"documents/{WorkspaceId}/imptfs/v1/{Sanitize(imptfsName)}";
        var uploaded = false;
        if (storage.IsConfigured)
        {
            await using var stream = new MemoryStream(imptfsBytes);
            await storage.UploadAsync(storagePath, stream, "application/pdf", upsert: true, ct);
            uploaded = true;
            logger.LogInformation("Uploaded IMPTFS to Storage at {Path}", storagePath);
        }

        var internalDoc = await UpsertInternalDocAsync(
            imptfsName, imptfsBytes.Length, storagePath, uploaded, ct);

        var sessionId = complianceSessionId ?? CombinedSessionId;
        var session = await db.ComplianceSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        string sessionMessage;
        int? compared = null;

        if (session == null)
        {
            sessionMessage =
                $"Compliance session {sessionId} not found — documents seeded; open Analyse/Dual-verify and load by session id when available.";
        }
        else
        {
            session.GovFileHash = TfsGuidelinesSeedService.BuiltinFileHash;
            session.InternalFileHash = InternalFileHash;
            session.GovFileName = "TFS Guidelines.pdf";
            session.InternalFileName = InternalFileName;
            session.UpdatedAt = DateTime.UtcNow;

            // Stamp linked doc ids into summary for UI/deep links
            session.SummaryJson = MergeSummaryLinks(
                session.SummaryJson,
                tfs.DocumentId,
                internalDoc.Id,
                tfs.StoragePath,
                internalDoc.StoragePath);

            await db.SaveChangesAsync(ct);
            compared = session.ComparedPoints;
            sessionMessage =
                $"Linked session {session.Id} ({session.ComparedPoints} pts) to TFS + IMPTFS file hashes.";
            logger.LogInformation(
                "Linked compliance session {Session} to gov={Gov} internal={Internal}",
                session.Id, session.GovFileHash[..12], session.InternalFileHash[..12]);
        }

        // Also stamp dual_verify sessions that used these hashes (if any)
        var dualUpdated = await db.DualVerifySessions
            .Where(s =>
                s.GovFileHash == TfsGuidelinesSeedService.BuiltinFileHash
                || s.InternalFileHash == InternalFileHash
                || s.GovDocId == "gov-tfs-guidelines"
                || s.InternalDocId == "internal-imptfs")
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.GovFileHash, TfsGuidelinesSeedService.BuiltinFileHash)
                    .SetProperty(s => s.InternalFileHash, InternalFileHash)
                    .SetProperty(s => s.GovFileName, "TFS Guidelines.pdf")
                    .SetProperty(s => s.InternalFileName, InternalFileName)
                    .SetProperty(s => s.GovDocId, tfs.DocumentId.ToString())
                    .SetProperty(s => s.InternalDocId, internalDoc.Id.ToString())
                    .SetProperty(s => s.UpdatedAt, DateTime.UtcNow),
                ct);

        // Ensure Documents → View analysis has at least the seeded combined run.
        await EnsureAnalysisRunHistoryAsync(tfs.DocumentId, internalDoc.Id, session, ct);

        return new BundleSeedResult(
            true,
            tfs.DocumentId,
            internalDoc.Id,
            session?.Id,
            compared,
            tfs.UploadedToStorage,
            uploaded,
            storage.IsConfigured,
            tfs.FileHash,
            InternalFileHash,
            tfs.StoragePath,
            internalDoc.StoragePath,
            imptfsSource,
            dualUpdated,
            $"{tfs.Message} | IMPTFS {(uploaded ? "uploaded" : "DB only")}. {sessionMessage}");
    }

    private async Task<(byte[]? Bytes, string FileName, string? Source)> ResolveImptfsAsync(
        string? localPath,
        CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(localPath))
            candidates.Add(localPath.Trim());
        candidates.Add(Path.Combine(env.ContentRootPath, "data", "seed-docs", InternalFileName));
        candidates.Add(@"C:\Users\Hp\Downloads\bundle\I M P T F S.pdf");

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            return (await File.ReadAllBytesAsync(path, ct), Path.GetFileName(path), path);
        }

        return (null, InternalFileName, null);
    }

    private async Task<StoredDocument> UpsertInternalDocAsync(
        string fileName,
        long sizeBytes,
        string storagePath,
        bool uploaded,
        CancellationToken ct)
    {
        const string title = "I M P T F S";
        const string kind = "document";

        var existing = await db.StoredDocuments
            .Where(d => d.WorkspaceId == WorkspaceId && d.DocKind == kind)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefaultAsync(
                d => d.FileHash == InternalFileHash || d.Title.ToLower() == title.ToLower(),
                ct);

        var history = new List<string>
        {
            uploaded
                ? $"v1 seeded+uploaded {DateTime.UtcNow:u} · linked analysis {InternalFileHash[..12]}…"
                : $"v1 seeded {DateTime.UtcNow:u} · linked analysis {InternalFileHash[..12]}… (storage pending)",
        };

        if (existing != null)
        {
            existing.Title = title;
            existing.OriginalFileName = fileName;
            existing.Category = "AML/Sanctions";
            existing.FilterKey = "sanctions";
            existing.DocKind = kind;
            existing.Version = "v1";
            existing.VersionNumber = Math.Max(1, existing.VersionNumber);
            existing.Status = "gaps";
            existing.GapCount ??= 32;
            existing.Pages = Math.Max(1, (int)Math.Round(sizeBytes / 45000.0));
            existing.SizeBytes = sizeBytes;
            existing.ContentType = "application/pdf";
            existing.StorageBucket = storage.Bucket;
            existing.StoragePath = storagePath;
            existing.FileHash = InternalFileHash;
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
            Category = "AML/Sanctions",
            FilterKey = "sanctions",
            DocKind = kind,
            Version = "v1",
            VersionNumber = 1,
            Status = "gaps",
            GapCount = 32,
            Pages = Math.Max(1, (int)Math.Round(sizeBytes / 45000.0)),
            SizeBytes = sizeBytes,
            ContentType = "application/pdf",
            StorageBucket = storage.Bucket,
            StoragePath = storagePath,
            FileHash = InternalFileHash,
            WorkspaceId = WorkspaceId,
            HistoryJson = JsonSerializer.Serialize(history),
        };
        db.StoredDocuments.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    private static string? MergeSummaryLinks(
        string? summaryJson,
        Guid govDocId,
        Guid internalDocId,
        string govStoragePath,
        string internalStoragePath)
    {
        try
        {
            using var doc = string.IsNullOrWhiteSpace(summaryJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(summaryJson);
            var map = new Dictionary<string, JsonElement>();
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = p.Value.Clone();

            map["linkedGovDocumentId"] = JsonSerializer.SerializeToElement(govDocId.ToString());
            map["linkedInternalDocumentId"] = JsonSerializer.SerializeToElement(internalDocId.ToString());
            map["linkedGovStoragePath"] = JsonSerializer.SerializeToElement(govStoragePath);
            map["linkedInternalStoragePath"] = JsonSerializer.SerializeToElement(internalStoragePath);
            map["linkedAt"] = JsonSerializer.SerializeToElement(DateTime.UtcNow.ToString("o"));
            return JsonSerializer.Serialize(map);
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                linkedGovDocumentId = govDocId.ToString(),
                linkedInternalDocumentId = internalDocId.ToString(),
                linkedGovStoragePath = govStoragePath,
                linkedInternalStoragePath = internalStoragePath,
                linkedAt = DateTime.UtcNow.ToString("o"),
            });
        }
    }

    private async Task EnsureAnalysisRunHistoryAsync(
        Guid regulationDocId,
        Guid internalDocId,
        ComplianceSession? session,
        CancellationToken ct)
    {
        if (session == null) return;

        var exists = await db.DocumentAnalysisRuns.AnyAsync(
            r => r.ComplianceSessionId == session.Id
                 || (r.InternalDocumentId == internalDocId
                     && r.RegulationDocumentId == regulationDocId
                     && r.PointCount == session.ComparedPoints),
            ct);
        if (exists) return;

        var regName = session.GovFileName ?? "TFS Guidelines.pdf";
        var intName = session.InternalFileName ?? InternalFileName;
        db.DocumentAnalysisRuns.Add(new DocumentAnalysisRun
        {
            WorkspaceId = WorkspaceId,
            InternalDocumentId = internalDocId,
            RegulationDocumentId = regulationDocId,
            DualVerifySessionId = null,
            ComplianceSessionId = session.Id,
            Label = $"{regName} × {intName} · {session.ComparedPoints} pts · leaf",
            RegulationFileName = regName,
            InternalFileName = intName,
            InternalFileHash = session.InternalFileHash,
            GovFileHash = session.GovFileHash,
            Status = "completed",
            PointCount = session.ComparedPoints,
            CompletedPoints = session.ComparedPoints,
            Granularity = "leaf",
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Sanitize(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }
}

public sealed record BundleSeedResult(
    bool Success,
    Guid GovDocumentId,
    Guid InternalDocumentId,
    Guid? ComplianceSessionId,
    int? ComparedPoints,
    bool GovUploadedToStorage,
    bool InternalUploadedToStorage,
    bool StorageConfigured,
    string GovFileHash,
    string InternalFileHash,
    string GovStoragePath,
    string InternalStoragePath,
    string? InternalSourcePath,
    int DualVerifySessionsUpdated,
    string Message);
