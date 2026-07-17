using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("documents")]
public class DocumentsController(
    AppDbContext db,
    SupabaseStorageService storage,
    LandingAiGovExtractService govExtract,
    GovPointsService govPoints,
    TfsGuidelinesSeedService tfsSeed,
    AnalysisBundleSeedService bundleSeed,
    DualVerifyService dualVerify,
    ILogger<DocumentsController> logger) : ControllerBase
{
    private static readonly Regex SanctionRx = new("sanction|tfs", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KycRx = new("kyc|cdd|pep", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [HttpGet("health")]
    public ActionResult<object> Health() => Ok(new
    {
        success = true,
        storageConfigured = storage.IsConfigured,
        bucket = storage.Bucket,
        hint = storage.IsConfigured
            ? "Ready — uploads go to private Supabase bucket."
            : "Replace PASTE_SERVICE_ROLE_KEY_HERE with your Supabase service_role key, then restart the API.",
    });

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DocumentDto>>>> List(
        [FromQuery] string? kind = null,
        [FromQuery] string? workspaceId = null,
        CancellationToken ct = default)
    {
        var q = db.StoredDocuments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
            q = q.Where(d => d.DocKind == kind);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            q = q.Where(d => d.WorkspaceId == workspaceId);

        var rows = await q.OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
        var activeCounts = await GetActiveAnalysisCountsAsync(rows, ct);
        return Ok(new ApiResponse<List<DocumentDto>>(true, rows.Select(d => ToDto(d, activeCounts.GetValueOrDefault(d.Id))).ToList()));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<ActionResult<object>> Upload(
        IFormFile file,
        [FromForm] string? category,
        [FromForm] string? filter,
        [FromForm] string? docKind,
        [FromForm] string? workspaceId,
        [FromForm] bool confirmVersionBump = false,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await ReadFileBytesAsync(file, ct);
            var row = await PersistUploadAsync(
                file, bytes, category, filter, docKind, workspaceId, confirmVersionBump, ct);
            return Ok(new ApiResponse<DocumentDto>(
                true,
                ToDto(row),
                $"Uploaded {row.Version} to Supabase Storage"));
        }
        catch (DuplicateDocumentException ex)
        {
            return Conflict(new
            {
                success = false,
                duplicate = true,
                message = ex.Message,
                existing = ToDto(ex.Existing),
                nextVersion = $"v{ex.Existing.VersionNumber + 1}",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Document upload failed");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Upload regulation PDF to private Storage, extract gov points via Landing AI,
    /// and persist file_hash so later selects load from DB cache.
    /// </summary>
    [HttpPost("upload-regulation")]
    [RequestSizeLimit(52_428_800)]
    public async Task<ActionResult<object>> UploadRegulation(
        IFormFile file,
        [FromForm] string? workspaceId,
        [FromForm] bool confirmVersionBump = false,
        CancellationToken ct = default)
    {
        try
        {
            var bytes = await ReadFileBytesAsync(file, ct);
            var row = await PersistUploadAsync(
                file, bytes, "Regulation", "regulation", "regulation", workspaceId, confirmVersionBump, ct);

            var extract = await govExtract.ExtractFromUploadAsync(bytes, file.FileName, null, ct);
            row.FileHash = extract.FileHash;
            row.PointCount = extract.PointCount;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Ok(new
            {
                success = true,
                message = extract.Cached
                    ? $"Loaded {extract.PointCount} points from DB cache (file was previously extracted)."
                    : $"Extracted {extract.PointCount} points and saved to DB.",
                document = ToDto(row),
                cached = extract.Cached,
                fileHash = extract.FileHash,
                pointCount = extract.PointCount,
                points = extract.Points,
                source = extract.Source,
            });
        }
        catch (DuplicateDocumentException ex)
        {
            return Conflict(new
            {
                success = false,
                duplicate = true,
                message = ex.Message,
                existing = ToDto(ex.Existing),
                nextVersion = $"v{ex.Existing.VersionNumber + 1}",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Regulation upload+extract failed");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>Load gov points for a stored regulation — DB extract cache first, else re-extract from Storage.</summary>
    [HttpPost("{id:guid}/load-points")]
    public async Task<ActionResult<object>> LoadPoints(Guid id, CancellationToken ct)
    {
        try
        {
            var row = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (row == null) return NotFound(new { success = false, message = "Document not found." });

            if (!string.Equals(row.DocKind, "regulation", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Landing extract is only for regulation documents. Compliance/internal uploads are stored without point extraction.",
                });
            }

            // Prefer extract cache keyed by this file's hash.
            // Built-in TFS hash may also resolve from embedded seed when DB cache is empty.
            if (!string.IsNullOrWhiteSpace(row.FileHash))
            {
                var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(row.FileHash, ct);
                var linkedBuiltin =
                    string.Equals(
                        row.FileHash,
                        LandingAiGovExtractService.BuiltinGovFileHash,
                        StringComparison.OrdinalIgnoreCase)
                    && loaded.Source is "db-cache" or "seed";

                if (loaded.Source == "db-cache" || linkedBuiltin)
                {
                    row.PointCount = loaded.PointCount;
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return Ok(new
                    {
                        success = true,
                        source = loaded.Source,
                        fileHash = row.FileHash,
                        pointCount = loaded.PointCount,
                        document = ToDto(row),
                        points = ToApiPoints(govPoints.GetAllPoints()),
                        message = loaded.Source == "db-cache"
                            ? $"Loaded {loaded.PointCount} points from DB for {row.OriginalFileName}."
                            : $"Loaded {loaded.PointCount} built-in TFS points linked to {row.OriginalFileName}.",
                    });
                }
            }

            if (!storage.IsConfigured || string.IsNullOrWhiteSpace(row.StoragePath))
                return BadRequest(new
                {
                    success = false,
                    message = string.IsNullOrWhiteSpace(row.FileHash)
                        ? "No extract cache and Storage is not configured. Upload the regulation again once Storage is ready."
                        : "No extract cache for this file hash and Storage is not configured to re-extract.",
                });

            var bytes = await storage.DownloadAsync(row.StoragePath, ct);
            var extract = await govExtract.ExtractFromUploadAsync(bytes, row.OriginalFileName, null, ct);
            row.FileHash = extract.FileHash;
            row.PointCount = extract.PointCount;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            return Ok(new
            {
                success = true,
                source = extract.Source,
                cached = extract.Cached,
                fileHash = extract.FileHash,
                pointCount = extract.PointCount,
                document = ToDto(row),
                points = extract.Points,
                message = extract.Cached
                    ? $"Cache hit after download — {extract.PointCount} points."
                    : $"Extracted {extract.PointCount} points from Storage file.",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Load points failed for {Id}", id);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/signed-url")]
    public async Task<ActionResult<object>> GetSignedUrl(Guid id, CancellationToken ct)
    {
        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null)
        {
            // ND regulation documents reference a stored document (or carry their
            // own storage path) — resolve so ND pages can open PDFs by their id.
            var ndReg = await db.NdRegulationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            if (ndReg?.StoredDocumentId is Guid storedId)
                doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId, ct);
            if (doc == null && !string.IsNullOrWhiteSpace(ndReg?.FilePath))
            {
                var ndUrl = await storage.CreateSignedUrlAsync(ndReg.FilePath, 3600, ct);
                return Ok(new { success = true, url = ndUrl, expiresIn = 3600, path = ndReg.FilePath });
            }
        }
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        var url = await storage.CreateSignedUrlAsync(doc.StoragePath, 3600, ct);
        return Ok(new { success = true, url, expiresIn = 3600, path = doc.StoragePath });
    }

    /// <summary>
    /// List Analyse/dual-verify runs linked to this compliance (or regulation) document.
    /// One document can have many runs (e.g. reg1×IMPTFS then reg2×IMPTFS).
    /// </summary>
    [HttpGet("{id:guid}/analysis-runs")]
    public async Task<ActionResult<object>> ListAnalysisRuns(Guid id, CancellationToken ct)
    {
        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        var runs = await db.DocumentAnalysisRuns.AsNoTracking()
            .Where(r =>
                r.InternalDocumentId == id
                || r.RegulationDocumentId == id
                || (!string.IsNullOrWhiteSpace(doc.FileHash)
                    && (r.InternalFileHash == doc.FileHash || r.GovFileHash == doc.FileHash)))
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        // Refresh status from dual-verify sessions when possible
        var sessionIds = runs
            .Where(r => r.DualVerifySessionId.HasValue)
            .Select(r => r.DualVerifySessionId!.Value)
            .Distinct()
            .ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, Data.Entities.DualVerifySession>()
            : await db.DualVerifySessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        var data = runs.Select(r =>
        {
            Data.Entities.DualVerifySession? s = null;
            var hasSession = r.DualVerifySessionId is Guid dvId && sessions.TryGetValue(dvId, out s);
            string status;
            int completed;
            int total;
            bool isActive;
            bool sessionAvailable;
            int failed = 0;
            int running = 0;
            if (r.DualVerifySessionId is Guid && !hasSession)
            {
                // Session removed from store — do not pretend the run finished successfully.
                status = "unavailable";
                completed = r.CompletedPoints;
                total = r.PointCount;
                isActive = false;
                sessionAvailable = false;
            }
            else
            {
                status = s?.Status ?? r.Status;
                completed = s?.CompletedPoints ?? r.CompletedPoints;
                total = s?.TotalPoints ?? r.PointCount;
                failed = s?.FailedPoints ?? 0;
                running = s?.RunningPoints ?? 0;
                var updatedAt = s != null
                    ? new DateTimeOffset(DateTime.SpecifyKind(s.UpdatedAt, DateTimeKind.Utc))
                    : r.UpdatedAt;
                isActive = AnalysisActivityHelper.IsStillActive(
                    status, completed, failed, total, updatedAt, running);
                status = AnalysisActivityHelper.NormalizeDisplayStatus(
                    status, completed, failed, total, updatedAt, running);
                if (isActive)
                    status = "in_progress";
                else if (total > 0 && completed + failed >= total
                    && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                    status = failed > 0 && completed == 0 ? "failed" : "completed";
                sessionAvailable = !r.DualVerifySessionId.HasValue || hasSession;
            }
            string openUrl;
            if (r.DualVerifySessionId is Guid kafkaId)
                openUrl = $"/dual-verify?session={kafkaId}";
            else if (r.ComplianceSessionId is Guid compId)
                openUrl = $"/dual-verify?saved=compliance:{compId}";
            else
                openUrl = "/dual-verify";
            return new
            {
                id = r.Id.ToString(),
                dualVerifySessionId = r.DualVerifySessionId?.ToString(),
                complianceSessionId = r.ComplianceSessionId?.ToString(),
                label = r.Label,
                regulationFileName = r.RegulationFileName,
                internalFileName = r.InternalFileName,
                status,
                pointCount = total,
                completedPoints = completed,
                failedPoints = failed,
                runningPoints = running,
                isActive,
                sessionAvailable,
                granularity = r.Granularity,
                createdAt = r.CreatedAt.ToString("o"),
                updatedAt = (s?.UpdatedAt ?? r.UpdatedAt.UtcDateTime).ToString("o"),
                openUrl,
            };
        }).ToList();

        return Ok(new
        {
            success = true,
            documentId = id,
            count = data.Count,
            runs = data,
        });
    }

    /// <summary>
    /// Permanently delete an analysis history entry and its linked session (when present).
    /// </summary>
    [HttpDelete("{id:guid}/analysis-runs/{runId:guid}")]
    public async Task<ActionResult<object>> DeleteAnalysisRun(Guid id, Guid runId, CancellationToken ct)
    {
        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null)
            return NotFound(new { success = false, message = "Document not found." });

        var run = await db.DocumentAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null)
            return NotFound(new { success = false, message = "Analysis run not found." });

        if (!IsRunLinkedToDocument(run, doc))
            return NotFound(new { success = false, message = "Analysis run not found for this document." });

        if (run.DualVerifySessionId is Guid dvId)
        {
            try
            {
                await dualVerify.DeleteSessionAsync(dvId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Dual-verify session delete failed during analysis run delete {RunId}", runId);
            }
        }

        if (run.ComplianceSessionId is Guid compId)
        {
            var comp = await db.ComplianceSessions.FirstOrDefaultAsync(s => s.Id == compId, ct);
            if (comp != null)
                db.ComplianceSessions.Remove(comp);
        }

        var stillThere = await db.DocumentAnalysisRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (stillThere != null)
            db.DocumentAnalysisRuns.Remove(stillThere);

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true, id = runId.ToString() });
    }

    /// <summary>
    /// Register TFS Guidelines.pdf in stored_documents, link existing gov extract cache by file hash,
    /// and upload the PDF to private Storage when ServiceRoleKey is configured.
    /// </summary>
    [HttpPost("seed-tfs-guidelines")]
    [RequestSizeLimit(52_428_800)]
    public async Task<ActionResult<object>> SeedTfsGuidelines(
        IFormFile? file,
        [FromForm] string? localPath,
        [FromForm] bool forceReupload = true,
        CancellationToken ct = default)
    {
        try
        {
            byte[]? uploaded = null;
            string? uploadedName = null;
            if (file is { Length: > 0 })
            {
                uploaded = await ReadFileBytesAsync(file, ct);
                uploadedName = file.FileName;
            }

            var result = await tfsSeed.SeedAsync(localPath, uploaded, uploadedName, forceReupload, ct);
            return Ok(new
            {
                success = result.Success,
                message = result.Message,
                documentId = result.DocumentId,
                fileHash = result.FileHash,
                pointCount = result.PointCount,
                uploadedToStorage = result.UploadedToStorage,
                storageConfigured = result.StorageConfigured,
                storagePath = result.StoragePath,
                sourcePdfPath = result.SourcePdfPath,
                document = await db.StoredDocuments.AsNoTracking()
                    .Where(d => d.Id == result.DocumentId)
                    .Select(d => new DocumentDto(
                        d.Id.ToString(),
                        d.Title,
                        d.Category,
                        d.Pages,
                        d.UpdatedAt.ToString("MMM d, yyyy"),
                        d.Version,
                        d.Status,
                        d.GapCount,
                        d.FilterKey,
                        d.FileType,
                        d.DocKind,
                        d.StoragePath,
                        DeserializeHistory(d.HistoryJson),
                        d.OriginalFileName,
                        d.SizeBytes,
                        d.FileHash,
                        d.PointCount,
                        0))
                    .FirstOrDefaultAsync(ct),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TFS guidelines seed failed");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Upload TFS + IMPTFS to Storage, register both stored_documents rows,
    /// and link the 32-point combined compliance session to those file hashes.
    /// </summary>
    [HttpPost("seed-analysis-bundle")]
    [RequestSizeLimit(52_428_800)]
    public async Task<ActionResult<object>> SeedAnalysisBundle(
        [FromForm] string? tfsLocalPath,
        [FromForm] string? imptfsLocalPath,
        [FromForm] Guid? complianceSessionId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await bundleSeed.SeedAsync(
                tfsLocalPath,
                imptfsLocalPath,
                complianceSessionId,
                ct);

            return Ok(new
            {
                success = result.Success,
                message = result.Message,
                govDocumentId = result.GovDocumentId,
                internalDocumentId = result.InternalDocumentId,
                complianceSessionId = result.ComplianceSessionId,
                comparedPoints = result.ComparedPoints,
                govUploadedToStorage = result.GovUploadedToStorage,
                internalUploadedToStorage = result.InternalUploadedToStorage,
                storageConfigured = result.StorageConfigured,
                govFileHash = result.GovFileHash,
                internalFileHash = result.InternalFileHash,
                govStoragePath = result.GovStoragePath,
                internalStoragePath = result.InternalStoragePath,
                dualVerifySessionsUpdated = result.DualVerifySessionsUpdated,
                openAnalysisUrl = result.ComplianceSessionId is Guid sid
                    ? $"/dual-verify?saved=compliance:{sid}"
                    : null,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis bundle seed failed");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private async Task<StoredDocument> PersistUploadAsync(
        IFormFile file,
        byte[] bytes,
        string? category,
        string? filter,
        string? docKind,
        string? workspaceId,
        bool confirmVersionBump,
        CancellationToken ct)
    {
        if (!storage.IsConfigured)
            throw new InvalidOperationException(
                "Supabase Storage not configured. Replace PASTE_SERVICE_ROLE_KEY_HERE with your service_role key and restart the API.");

        if (bytes.Length == 0)
            throw new InvalidOperationException("No file provided.");

        var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Invalid file name.");

        var kind = string.IsNullOrWhiteSpace(docKind) ? "document" : docKind.Trim().ToLowerInvariant();
        var ws = string.IsNullOrWhiteSpace(workspaceId) ? "snb-uae-difc" : workspaceId.Trim();
        var titleKey = NormalizeKey(title);
        var titleLower = title.ToLowerInvariant();

        var existing = await db.StoredDocuments
            .Where(d => d.WorkspaceId == ws && d.DocKind == kind)
            .OrderByDescending(d => d.VersionNumber)
            .FirstOrDefaultAsync(d => d.Title.ToLower() == titleLower, ct);

        if (existing != null && !confirmVersionBump)
            throw new DuplicateDocumentException(
                $"File already exists as {existing.Version}. Confirm to upload as next version.",
                existing);

        var versionNumber = existing == null ? 1 : existing.VersionNumber + 1;
        var version = $"v{versionNumber}";
        var fileType = DetectFileType(file.FileName);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        var safeName = SanitizeFileName(file.FileName);
        var objectPath = $"{kind}s/{ws}/{titleKey}/{version}/{safeName}";
        var fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using (var stream = new MemoryStream(bytes))
        {
            await storage.UploadAsync(objectPath, stream, contentType, upsert: true, ct);
        }

        var history = existing != null
            ? DeserializeHistory(existing.HistoryJson)
            : [];
        history.Insert(0, $"{version} uploaded {DateTime.UtcNow:u} · {file.FileName}");

        StoredDocument row;
        if (existing != null)
        {
            existing.OriginalFileName = file.FileName;
            existing.FileType = fileType;
            existing.Category = string.IsNullOrWhiteSpace(category) ? existing.Category : category!;
            existing.FilterKey = string.IsNullOrWhiteSpace(filter) ? existing.FilterKey : filter!;
            existing.Version = version;
            existing.VersionNumber = versionNumber;
            existing.Status = "review-due";
            existing.Pages = EstimatePages(bytes.Length);
            existing.SizeBytes = bytes.Length;
            existing.ContentType = contentType;
            existing.StorageBucket = storage.Bucket;
            existing.StoragePath = objectPath;
            existing.FileHash = fileHash;
            existing.HistoryJson = JsonSerializer.Serialize(history);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            row = existing;
        }
        else
        {
            var meta = InferMeta(file.FileName, category, filter);
            row = new StoredDocument
            {
                Title = title,
                OriginalFileName = file.FileName,
                FileType = fileType,
                Category = meta.Category,
                FilterKey = meta.Filter,
                DocKind = kind,
                Version = version,
                VersionNumber = versionNumber,
                Status = "review-due",
                Pages = EstimatePages(bytes.Length),
                SizeBytes = bytes.Length,
                ContentType = contentType,
                StorageBucket = storage.Bucket,
                StoragePath = objectPath,
                FileHash = fileHash,
                WorkspaceId = ws,
                HistoryJson = JsonSerializer.Serialize(history),
            };
            db.StoredDocuments.Add(row);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Uploaded {Path} as {Version}", objectPath, version);
        return row;
    }

    private static IReadOnlyList<object> ToApiPoints(IReadOnlyList<Models.GovPoint> points) =>
        points.Select(p => new
        {
            point_id = p.PointId,
            title = p.Title,
            text = p.Text,
            section = p.Section,
            point_type = "mandatory",
        }).ToList();

    private static async Task<byte[]> ReadFileBytesAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return [];
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static DocumentDto ToDto(StoredDocument d, int activeAnalysisCount = 0) => new(
        d.Id.ToString(),
        d.Title,
        d.Category,
        d.Pages,
        d.UpdatedAt.ToString("MMM d, yyyy"),
        d.Version,
        d.Status,
        d.GapCount,
        d.FilterKey,
        d.FileType,
        d.DocKind,
        d.StoragePath,
        DeserializeHistory(d.HistoryJson),
        d.OriginalFileName,
        d.SizeBytes,
        d.FileHash,
        d.PointCount,
        activeAnalysisCount);

    private async Task<Dictionary<Guid, int>> GetActiveAnalysisCountsAsync(
        List<StoredDocument> docs,
        CancellationToken ct)
    {
        if (docs.Count == 0) return [];

        var docIds = docs.Select(d => d.Id).ToHashSet();
        var runs = await db.DocumentAnalysisRuns.AsNoTracking()
            .Where(r =>
                (r.InternalDocumentId != null && docIds.Contains(r.InternalDocumentId.Value))
                || (r.RegulationDocumentId != null && docIds.Contains(r.RegulationDocumentId.Value)))
            .ToListAsync(ct);

        if (runs.Count == 0) return docIds.ToDictionary(id => id, _ => 0);

        var sessionIds = runs
            .Where(r => r.DualVerifySessionId.HasValue)
            .Select(r => r.DualVerifySessionId!.Value)
            .Distinct()
            .ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, DualVerifySession>()
            : await db.DualVerifySessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        var counts = docIds.ToDictionary(id => id, _ => 0);
        foreach (var doc in docs)
        {
            foreach (var run in runs)
            {
                if (!IsRunLinkedToDocument(run, doc)) continue;
                if (!IsAnalysisRunActive(run, sessions)) continue;
                counts[doc.Id]++;
            }
        }

        return counts;
    }

    private static bool IsRunLinkedToDocument(DocumentAnalysisRun run, StoredDocument doc) =>
        run.InternalDocumentId == doc.Id
        || run.RegulationDocumentId == doc.Id
        || (!string.IsNullOrWhiteSpace(doc.FileHash)
            && (run.InternalFileHash == doc.FileHash || run.GovFileHash == doc.FileHash));

    private static bool IsAnalysisRunActive(
        DocumentAnalysisRun run,
        IReadOnlyDictionary<Guid, DualVerifySession> sessions)
    {
        if (run.DualVerifySessionId is Guid sessionId)
        {
            if (!sessions.TryGetValue(sessionId, out var session)) return false;
            var updatedAt = new DateTimeOffset(DateTime.SpecifyKind(session.UpdatedAt, DateTimeKind.Utc));
            return AnalysisActivityHelper.IsStillActive(
                session.Status,
                session.CompletedPoints,
                session.FailedPoints,
                session.TotalPoints,
                updatedAt,
                session.RunningPoints);
        }

        return AnalysisActivityHelper.IsStillActive(
            run.Status,
            run.CompletedPoints,
            0,
            run.PointCount,
            run.UpdatedAt);
    }

    private static List<string> DeserializeHistory(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string NormalizeKey(string title)
    {
        var chars = title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }

    private static string DetectFileType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "PDF",
            ".doc" or ".docx" => "DOC",
            ".xls" or ".xlsx" => "XLS",
            _ => "DOC",
        };
    }

    private static int EstimatePages(long bytes) => Math.Max(1, (int)Math.Round(bytes / 45000.0));

    private static (string Category, string Filter) InferMeta(string fileName, string? category, string? filter)
    {
        if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(filter))
            return (category!, filter!);

        var f = SanctionRx.IsMatch(fileName)
            ? "sanctions"
            : KycRx.IsMatch(fileName)
                ? "kyc"
                : "aml";
        var c = f switch
        {
            "sanctions" => "Sanctions",
            "kyc" => "KYC/CDD",
            _ => "AML/CFT",
        };
        return (string.IsNullOrWhiteSpace(category) ? c : category!, string.IsNullOrWhiteSpace(filter) ? f : filter!);
    }

    private sealed class DuplicateDocumentException(string message, StoredDocument existing) : Exception(message)
    {
        public StoredDocument Existing { get; } = existing;
    }
}

public record DocumentDto(
    string Id,
    string Title,
    string Category,
    int Pages,
    string Uploaded,
    string Version,
    string Status,
    int? GapCount,
    string Filter,
    string FileType,
    string DocKind,
    string StoragePath,
    List<string> History,
    string OriginalFileName,
    long SizeBytes,
    string? FileHash,
    int? PointCount,
    int ActiveAnalysisCount = 0);
