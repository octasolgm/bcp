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

        var baseTitle = Path.GetFileNameWithoutExtension(fileName).Trim();
        var prepared = await uploadPrep.PrepareAsync(
            bytes,
            fileName,
            contentType,
            "regulations/nd",
            ct);

        var title = await AllocateRegulationDisplayNameAsync(baseTitle, prepared.FileHash, ct);

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
    /// Recompute stored PDF page references from cached parse markdown — no Landing AI calls.
    /// </summary>
    public async Task<int> RefreshPointPageReferencesAsync(Guid regulationId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        if (regDoc.StoredDocumentId is not Guid storedId)
            throw new InvalidOperationException("This regulation has no stored file.");

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct)
            ?? throw new InvalidOperationException("Stored document not found.");

        var fileHash = (stored.FileHash ?? "").Trim();
        if (string.IsNullOrEmpty(fileHash))
        {
            var bytes = await storage.DownloadAsync(stored.StoragePath, ct);
            fileHash = LandingAiCacheRepository.HashBuffer(bytes);
        }

        var cacheKey = await EnsureExtractionCacheKeyAsync(db, stored, ct);
        var parseMarkdown = (await parseCache.GetParseCacheAsync(cacheKey, ct))?.Markdown;
        if (string.IsNullOrWhiteSpace(parseMarkdown))
            parseMarkdown = regDoc.ExtractionMarkdown;
        if (string.IsNullOrWhiteSpace(parseMarkdown))
            throw new InvalidOperationException(
                "No cached document parse found. Run extract once for this regulation document.");

        int? pdfPageCount = stored.Pages is > 1 ? stored.Pages : null;
        if (pdfPageCount is null or <= 1)
        {
            try
            {
                var bytes = await storage.DownloadAsync(stored.StoragePath, ct);
                var fileName = stored.OriginalFileName ?? Path.GetFileName(stored.StoragePath);
                if (LandingAiDocumentFormats.IsPdf(fileName, bytes))
                    pdfPageCount = LandingAiDocumentParseService.GetPdfPageCount(bytes);
            }
            catch
            {
                // optional
            }
        }

        var points = await db.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == regDoc.Id)
            .ToListAsync(ct);
        if (points.Count == 0)
            return 0;

        foreach (var p in points)
        {
            int? pageHint = ParsePdfPageFromReference(p.PageReference);
            var resolved = PolicyPageResolver.ResolveGovPointPage(
                parseMarkdown,
                p.PointNumber,
                p.PointNumber,
                p.PointTitle,
                p.PointContent,
                pageHint,
                pdfPageCount);
            resolved = PolicyPageResolver.RefinePageGuess(resolved, p.PointNumber, pdfPageCount);
            p.PageReference = FormatPointPageReference(p.PointNumber, resolved);
        }

        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return points.Count;
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

    private static int? ParsePdfPageFromReference(string? pageReference)
    {
        if (string.IsNullOrWhiteSpace(pageReference)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            pageReference,
            @"\bp\.?\s*(\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var page) && page > 0 ? page : null;
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

        int? pdfPageCount = null;
        try
        {
            if (LandingAiDocumentFormats.IsPdf(fileName, bytes))
                pdfPageCount = LandingAiDocumentParseService.GetPdfPageCount(bytes);
        }
        catch
        {
            // optional
        }

        var parseMarkdown = (await cache.GetParseCacheAsync(cacheKey, ct))?.Markdown;
        if (!string.IsNullOrWhiteSpace(parseMarkdown))
            regDoc.ExtractionMarkdown = parseMarkdown;

        await ReportProgress(new ExtractionProgressUpdate($"Saving {result.Points.Count} regulation points…", 92));

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

            int? resolvedPage = pageHint;
            if (!string.IsNullOrWhiteSpace(parseMarkdown))
            {
                resolvedPage = PolicyPageResolver.ResolveGovPointPage(
                    parseMarkdown,
                    pointId,
                    section,
                    title,
                    text,
                    pageHint,
                    pdfPageCount);
            }

            resolvedPage = PolicyPageResolver.RefinePageGuess(resolvedPage, pointId, pdfPageCount);

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
                if (pdfPageCount is > 0)
                    stored.Pages = pdfPageCount.Value;
                stored.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await dbCtx.SaveChangesAsync(ct);
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
}
