using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public class NdRegulationUploadService(
    AppDbContext db,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    LandingAiGovExtractService govExtract,
    LandingAiCacheRepository parseCache,
    IOptions<LandingAiOptions> landingAiOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<NdRegulationUploadService> logger)
{
    private static readonly ConcurrentDictionary<Guid, byte> RunningExtracts = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ExtractCancellation = new();
    /// <summary>No in-memory job and status still processing — likely API restart or aborted run.</summary>
    private static readonly TimeSpan StaleProcessingAfter = TimeSpan.FromMinutes(3);
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

        var title = Path.GetFileNameWithoutExtension(fileName).Trim();
        var prepared = await uploadPrep.PrepareAsync(
            bytes,
            fileName,
            contentType,
            "regulations/nd",
            ct);

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
            Pages = Math.Max(1, (int)Math.Round(prepared.SizeBytes / 45000.0)),
            UploadedBy = userId,
        };
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

    /// <summary>Resolve ND or legacy stored-document id and queue Landing AI extraction (returns immediately).</summary>
    public async Task<NdRegulationDocument> ExtractByRegulationIdAsync(
        Guid regulationId,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await ResolveOrCreateRegulationRowAsync(regulationId, userId, ct);
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
        if (DateTimeOffset.UtcNow - regDoc.UpdatedAt < StaleProcessingAfter)
            return;

        regDoc.ExtractionStatus = "failed";
        regDoc.ExtractionProgressLabel = "Previous extraction did not finish. Run Extract again.";
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
        RegulationParseCheckpoint? checkpoint = null;
        if (regDoc.ExtractionParseChunkCompleted is int completed && completed >= 0)
        {
            var cached = await cache.GetParseCacheAsync(fileHash, ct);
            checkpoint = new RegulationParseCheckpoint
            {
                ResumeFromChunkIndex = completed + 1,
                PartialMarkdown = cached?.Markdown,
                OnChunkParsedAsync = async (chunkIndex, mergedMarkdown) =>
                {
                    regDoc.ExtractionParseChunkCompleted = chunkIndex;
                    regDoc.UpdatedAt = DateTimeOffset.UtcNow;
                    await cache.SaveParseCacheAsync(fileHash, fileName, mergedMarkdown, landingOpts.ParseModel, ct);
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
                    await cache.SaveParseCacheAsync(fileHash, fileName, mergedMarkdown, landingOpts.ParseModel, ct);
                    await dbCtx.SaveChangesAsync(ct);
                },
            };
        }

        var result = await gov.ExtractFromUploadAsync(
            bytes, fileName, null, ReportProgress, checkpoint, ct);

        await ReportProgress(new ExtractionProgressUpdate($"Saving {result.Points.Count} regulation points…", 92));

        var existingPoints = await dbCtx.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == regDoc.Id)
            .ToListAsync(ct);
        if (existingPoints.Count > 0)
            dbCtx.NdRegulationPoints.RemoveRange(existingPoints);

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

            var isAnnex = GovPointClassifier.IsAnnexPoint(pointId, title, section);
            var isIntro = GovPointClassifier.IsIntroductionPoint(pointId, title, text, section, pointType);

            dbCtx.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = regDoc.Id,
                PointNumber = pointId,
                PointTitle = title,
                PointContent = text,
                PageReference = FormatPointPageReference(section, pageHint),
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
                stored.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await dbCtx.SaveChangesAsync(ct);
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
}
