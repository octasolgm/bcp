using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/internal-documents")]
public class InternalDocumentsController(
    AppDbContext appDb,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    NdInternalParseService parseService,
    NdInternalDocumentSectionService sectionService,
    NdInternalDocumentSectionPageService sectionPageService,
    LandingAiCacheRepository landingCache,
    NdDemoUserDirectory demoDirectory,
    NdDemoInterceptionService demoInterception,
    SupabaseJwtValidator jwt,
    NdDashboardCacheService dashboardCache) : NdControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool hiddenOnly = false, CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        if (hiddenOnly && profile!.Role != "super_admin")
            return StatusCode(403, new { success = false, message = "Forbidden" });

        await sectionService.RecoverAllStaleSectionExtractsAsync(ct);
        await parseService.RecoverAllStaleParsesAsync(ct);

        var docs = await NdDemoDataFilters.ApplyToStoredDocuments(
                appDb.StoredDocuments.AsNoTracking()
                    .Where(d => (d.DocKind == "document" || d.DocKind == "internal") && d.IsHidden == hiddenOnly),
                demoCtx)
            .OrderByDescending(d => hiddenOnly ? d.HiddenAt ?? d.UpdatedAt : d.CreatedAt)
            .ToListAsync(ct);

        var profileNames = await LoadProfileNamesAsync(
            appDb,
            docs.SelectMany(d => new Guid?[] { d.UploadedBy, d.ParsedBy, d.HiddenBy, d.SectionExtractedBy }),
            ct);

        var items = new List<object>();
        foreach (var d in docs)
        {
            var recovered = await parseService.RecoverStaleParseIfNeededAsync(d.Id, ct);
            var live = recovered ?? d;
            var parseStatus = await parseService.ResolveDisplayParseStatusAsync(live, ct);
            items.Add(new
            {
                id = d.Id,
                source = "legacy",
                title = d.Title,
                name = d.Title,
                originalFileName = d.OriginalFileName,
                version = d.VersionNumber,
                uploaded = d.CreatedAt,
                uploadedAt = d.CreatedAt,
                sizeBytes = d.SizeBytes,
                department = d.Category,
                parseStatus,
                parsedAt = live.ParsedAt,
                parseError = live.ParseError,
                uploadedBy = d.UploadedBy,
                uploadedByName = ProfileName(profileNames, d.UploadedBy),
                parsedBy = d.ParsedBy,
                parsedByName = ProfileName(profileNames, d.ParsedBy),
                sectionExtractStatus = d.SectionExtractStatus,
                sectionCount = d.SectionCount,
                sectionExtractedAt = d.SectionExtractedAt,
                sectionExtractError = d.SectionExtractError,
                sectionExtractProgressLabel = ShowsSectionExtractProgress(d.SectionExtractStatus)
                    ? d.SectionExtractProgressLabel
                    : null,
                sectionExtractProgressPct = ShowsSectionExtractProgress(d.SectionExtractStatus)
                    ? d.SectionExtractProgressPct
                    : null,
                sectionExtractedBy = d.SectionExtractedBy,
                sectionExtractedByName = ProfileName(profileNames, d.SectionExtractedBy),
                isHidden = d.IsHidden,
                hiddenAt = d.HiddenAt,
                convertedFromWord = !string.IsNullOrWhiteSpace(d.SourceStoragePath),
                sourceOriginalFileName = !string.IsNullOrWhiteSpace(d.SourceStoragePath)
                    ? d.OriginalFileName
                    : null,
                landingAiFileName = Path.GetFileName(d.StoragePath),
            });
        }

        return Ok(new { success = true, data = items });
    }

    [HttpGet("{id:guid}/analysis-runs")]
    public async Task<IActionResult> ListAnalysisRuns(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var doc = await appDb.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        var items = new List<object>();
        var docIdStr = id.ToString();

        List<NdAnalysisRun> ndRuns;
        try
        {
            ndRuns = await appDb.NdAnalysisRuns.AsNoTracking()
                .Where(r => r.Status != "deleted")
                .OrderByDescending(r => r.CreatedAt)
                .Take(200)
                .ToListAsync(ct);
        }
        catch
        {
            ndRuns = [];
        }

        foreach (var run in ndRuns)
        {
            var internalIds = JsonSerializer.Deserialize<List<string>>(run.SelectedInternalDocIds) ?? [];
            if (!internalIds.Contains(docIdStr, StringComparer.OrdinalIgnoreCase)) continue;

            items.Add(new
            {
                id = run.Id,
                source = "nd_analysis",
                name = run.Name,
                regulationFileName = (string?)null,
                internalFileName = doc.Title,
                status = run.Status,
                pointCount = run.TotalPointsCount,
                completedPoints = run.ProcessedPointsCount,
                failedPoints = run.DualVerifyFailedCount,
                runningPoints = 0,
                isActive = run.Status is "draft" or "running",
                sessionAvailable = true,
                dualVerifySessionId = (string?)null,
                complianceSessionId = (string?)null,
                createdAt = run.CreatedAt.ToString("o"),
                updatedAt = run.UpdatedAt.ToString("o"),
            });
        }

        var legacyRuns = await appDb.DocumentAnalysisRuns.AsNoTracking()
            .Where(r =>
                r.InternalDocumentId == id
                || (!string.IsNullOrWhiteSpace(doc.FileHash)
                    && r.InternalFileHash == doc.FileHash))
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        var sessionIds = legacyRuns
            .Where(r => r.DualVerifySessionId.HasValue)
            .Select(r => r.DualVerifySessionId!.Value)
            .Distinct()
            .ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, DualVerifySession>()
            : await appDb.DualVerifySessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        foreach (var r in legacyRuns)
        {
            DualVerifySession? s = null;
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
                if (isActive) status = "in_progress";
                else if (total > 0 && completed + failed >= total
                    && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                    status = failed > 0 && completed == 0 ? "failed" : "completed";
                sessionAvailable = !r.DualVerifySessionId.HasValue || hasSession;
            }

            items.Add(new
            {
                id = r.Id,
                source = "legacy_analysis",
                name = string.IsNullOrWhiteSpace(r.Label)
                    ? $"{r.RegulationFileName ?? "Regulation"} × {r.InternalFileName ?? doc.Title}"
                    : r.Label,
                regulationFileName = r.RegulationFileName,
                internalFileName = r.InternalFileName ?? doc.Title,
                status,
                pointCount = total,
                completedPoints = completed,
                failedPoints = failed,
                runningPoints = running,
                isActive,
                sessionAvailable,
                dualVerifySessionId = r.DualVerifySessionId?.ToString(),
                complianceSessionId = r.ComplianceSessionId?.ToString(),
                createdAt = r.CreatedAt.ToString("o"),
                updatedAt = (s?.UpdatedAt ?? r.UpdatedAt.UtcDateTime).ToString("o"),
            });
        }

        var sorted = items
            .OrderByDescending(i =>
            {
                var activeProp = i.GetType().GetProperty("isActive");
                if (activeProp?.GetValue(i) is true) return DateTimeOffset.MaxValue;
                var createdProp = i.GetType().GetProperty("createdAt");
                var created = createdProp?.GetValue(i)?.ToString();
                return DateTimeOffset.TryParse(created, out var dt) ? dt : DateTimeOffset.MinValue;
            })
            .ToList();

        return Ok(new { success = true, data = sorted });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return BadRequest(new { success = false, message = "Supabase Storage not configured." });
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "No file provided." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();

        try
        {
            var prepared = await uploadPrep.PrepareAsync(
                bytes,
                file.FileName,
                file.ContentType,
                "documents/nd",
                ct);

            var row = new StoredDocument
            {
                Title = title,
                OriginalFileName = prepared.OriginalFileName,
                FileType = prepared.FileType,
                DocKind = "document",
                StorageBucket = storage.Bucket,
                StoragePath = prepared.StoragePath,
                FileHash = prepared.FileHash,
                SizeBytes = prepared.SizeBytes,
                ContentType = prepared.ContentType,
                ParseStatus = "pending",
                UploadedBy = profile!.Id,
            };
            row.ExtractionCacheKey = NdRegulationCacheKeys.ForStoredDocument(row.Id);
            appDb.StoredDocuments.Add(row);
            await appDb.SaveChangesAsync(ct);
            dashboardCache.Invalidate();

            return Ok(new
            {
                success = true,
                data = new
                {
                    id = row.Id,
                    title = row.Title,
                    originalFileName = row.OriginalFileName,
                    parseStatus = row.ParseStatus,
                    fileType = row.FileType,
                },
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/file-url")]
    public async Task<IActionResult> FileUrl(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var doc = await appDb.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath))
            return NotFound(new { success = false, message = "Document file not found." });

        var url = await storage.CreateSignedUrlAsync(doc.StoragePath, 3600, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                url,
                fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath),
                expiresIn = 3600,
            },
        });
    }

    [HttpGet("{id:guid}/export/markdown")]
    public async Task<IActionResult> ExportMarkdown(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var doc = await appDb.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null)
            return NotFound(new { success = false, message = "Document not found." });

        var markdown = await LoadInternalParsedMarkdownAsync(doc, ct);
        if (string.IsNullOrWhiteSpace(markdown))
            return BadRequest(new { success = false, message = "Document has no parsed markdown. Run Parse first." });

        byte[]? fileBytes = null;
        if (!string.IsNullOrWhiteSpace(doc.StoragePath) && storage.IsConfigured)
        {
            try
            {
                fileBytes = await storage.DownloadAsync(doc.StoragePath, ct);
            }
            catch
            {
                // Fall back to raw parse cache markdown.
            }
        }

        var fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath) ?? doc.Title ?? "internal-document.pdf";
        markdown = NdDocumentExportHelper.ResolveInternalMarkdownForV4(markdown, fileBytes, fileName);

        var label = doc.Title ?? doc.OriginalFileName ?? "internal-document.pdf";
        var wrapped = NdDocumentExportHelper.WrapInternalMarkdownForV4(label, markdown);
        var baseName = NdDocumentExportHelper.SafeExportBaseName(label, "internal-document");
        return File(
            NdDocumentExportHelper.Utf8Bytes(wrapped),
            "text/markdown; charset=utf-8",
            $"{baseName}.md");
    }

    [HttpGet("{id:guid}/export/file")]
    public async Task<IActionResult> ExportFile(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var doc = await appDb.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath))
            return NotFound(new { success = false, message = "Document file not found." });

        var bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        var fileName = doc.OriginalFileName ?? Path.GetFileName(doc.StoragePath);
        var contentType = ResolveInternalContentType(doc.FileType, fileName);
        return File(bytes, contentType, fileName);
    }

    [HttpGet("{id:guid}/source-file-url")]
    public async Task<IActionResult> SourceFileUrl(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var doc = await appDb.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null || string.IsNullOrWhiteSpace(doc.SourceStoragePath))
            return NotFound(new { success = false, message = "Legacy converted upload has no separate source file." });

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var url = await storage.CreateSignedUrlAsync(doc.SourceStoragePath, 3600, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                url,
                fileName = doc.OriginalFileName ?? Path.GetFileName(doc.SourceStoragePath),
                expiresIn = 3600,
            },
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await appDb.StoredDocuments.FirstOrDefaultAsync(
            d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null)
            return NotFound(new { success = false, message = "Document not found." });
        if (doc.IsHidden)
            return Ok(new { success = true, message = "Already deleted." });

        doc.IsHidden = true;
        doc.HiddenAt = DateTimeOffset.UtcNow;
        doc.HiddenBy = profile!.Id;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await appDb.SaveChangesAsync(ct);
        dashboardCache.Invalidate();

        return Ok(new { success = true, message = "Document removed from library (data kept in database)." });
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin");
        if (error != null) return error;

        var doc = await appDb.StoredDocuments.FirstOrDefaultAsync(
            d => d.Id == id && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (doc == null)
            return NotFound(new { success = false, message = "Document not found." });
        if (!doc.IsHidden)
            return Ok(new { success = true, message = "Document is already active." });

        doc.IsHidden = false;
        doc.HiddenAt = null;
        doc.HiddenBy = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await appDb.SaveChangesAsync(ct);
        dashboardCache.Invalidate();

        return Ok(new { success = true, message = "Document restored." });
    }

    [HttpPost("{id:guid}/parse")]
    public async Task<IActionResult> Parse(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        try
        {
            var doc = await appDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc == null)
                return NotFound(new { success = false, message = "Internal document not found." });
            if (!NdDemoDataFilters.CanAccessCreatedBy(doc.UploadedBy, demoCtx))
                return NotFound(new { success = false, message = "Internal document not found." });

            await parseService.RecoverStaleParseIfNeededAsync(id, ct);

            if (NdDemoIsolationHelper.ShouldSimulateAi(demoCtx, doc.UploadedBy))
            {
                var parseStatus = (doc.ParseStatus ?? "").Trim().ToLowerInvariant();
                if (parseStatus is "processing")
                {
                    return Ok(new
                    {
                        success = true,
                        data = new { id, parseStatus = "processing" },
                    });
                }

                if (parseStatus is "parsed")
                {
                    return Ok(new
                    {
                        success = true,
                        data = new
                        {
                            id,
                            parseStatus = "parsed",
                            parsedAt = doc.ParsedAt,
                            parsedByName = doc.ParsedBy != null
                                ? ProfileName(
                                    await LoadProfileNamesAsync(appDb, [doc.ParsedBy], ct),
                                    doc.ParsedBy)
                                : null,
                        },
                    });
                }

                doc.ParseStatus = "processing";
                doc.ParseError = null;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await appDb.SaveChangesAsync(ct);

                if (!demoInterception.TryQueueInternalParse(id, profile!.Id))
                    return BadRequest(new { success = false, message = "Parse is already running for this document." });
            }
            else
            {
                await parseService.ParseByIdAsync(id, profile!.Id, ct);
            }

            var refreshed = await appDb.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id,
                    parseStatus = refreshed?.ParseStatus ?? "parsed",
                    parsedAt = refreshed?.ParsedAt,
                    parsedByName = refreshed?.ParsedBy != null
                        ? ProfileName(
                            await LoadProfileNamesAsync(appDb, [refreshed.ParsedBy], ct),
                            refreshed.ParsedBy)
                        : null,
                },
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/sections")]
    public async Task<IActionResult> ListSections(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var doc = await sectionService.RecoverStaleSectionExtractIfNeededAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        var sections = await sectionService.ListSectionsAsync(id, ct);
        var repair = sectionPageService.GetRepairJob(id);
        return Ok(new
        {
            success = true,
            data = new
            {
                documentId = id,
                sectionExtractStatus = doc.SectionExtractStatus,
                sectionExtractError = doc.SectionExtractError,
                sectionExtractProgressLabel = ShowsSectionExtractProgress(doc.SectionExtractStatus)
                    ? doc.SectionExtractProgressLabel
                    : null,
                sectionExtractProgressPct = ShowsSectionExtractProgress(doc.SectionExtractStatus)
                    ? doc.SectionExtractProgressPct
                    : null,
                sectionPageRepairStatus = repair?.Status,
                sectionPageRepairProgressLabel = repair?.Label,
                sectionPageRepairProgressPct = repair?.Percent,
                sectionPageRepairPagesRefreshed = repair?.PagesRefreshed,
                sectionPageRepairSectionCount = repair?.SectionCount,
                sectionPageRepairError = repair?.Error,
                sectionCount = doc.SectionCount ?? sections.Count,
                sections = sections.Select(s => new
                {
                    id = s.Id,
                    sectionRef = s.SectionRef,
                    sectionText = s.SectionText,
                    sourcePage = s.SourcePage,
                    displayOrder = s.DisplayOrder,
                }),
            },
        });
    }

    [HttpPost("{id:guid}/extract-sections")]
    public async Task<IActionResult> ExtractSections(
        Guid id,
        [FromQuery] bool force = false,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        try
        {
            var trackedDoc = await appDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (trackedDoc == null)
                return NotFound(new { success = false, message = "Document not found." });
            if (!NdDemoDataFilters.CanAccessCreatedBy(trackedDoc.UploadedBy, demoCtx))
                return NotFound(new { success = false, message = "Document not found." });

            IReadOnlyList<NdInternalDocumentSection> sections;
            if (NdDemoIsolationHelper.ShouldSimulateAi(demoCtx, trackedDoc.UploadedBy))
            {
                var extractStatus = (trackedDoc.SectionExtractStatus ?? "").Trim().ToLowerInvariant();
                if (extractStatus is "processing")
                {
                    return Ok(new
                    {
                        success = true,
                        message = "Section extract in progress.",
                        data = new
                        {
                            id,
                            sectionExtractStatus = "processing",
                            sectionCount = trackedDoc.SectionCount,
                            sectionExtractProgressLabel = trackedDoc.SectionExtractProgressLabel,
                            sectionExtractProgressPct = trackedDoc.SectionExtractProgressPct,
                        },
                    });
                }

                if (!force && extractStatus is "extracted")
                {
                    sections = await sectionService.ListSectionsAsync(id, ct);
                    return Ok(new
                    {
                        success = true,
                        message = $"Using {sections.Count} saved policy sections (no new Landing AI call).",
                        data = new
                        {
                            id,
                            sectionExtractStatus = "extracted",
                            sectionCount = sections.Count,
                            sectionExtractedAt = trackedDoc.SectionExtractedAt,
                            reusedSaved = true,
                        },
                    });
                }

                trackedDoc.SectionExtractStatus = "processing";
                trackedDoc.SectionExtractError = null;
                trackedDoc.SectionExtractProgressLabel = "Starting section extract…";
                trackedDoc.SectionExtractProgressPct = 5;
                trackedDoc.UpdatedAt = DateTimeOffset.UtcNow;
                await appDb.SaveChangesAsync(ct);

                if (!demoInterception.TryQueueInternalSectionExtract(id, profile!.Id, force))
                    return BadRequest(new { success = false, message = "Section extract is already running." });

                return Ok(new
                {
                    success = true,
                    message = "Section extract started.",
                    data = new
                    {
                        id,
                        sectionExtractStatus = "processing",
                        sectionCount = trackedDoc.SectionCount,
                        sectionExtractProgressLabel = trackedDoc.SectionExtractProgressLabel,
                        sectionExtractProgressPct = trackedDoc.SectionExtractProgressPct,
                        reusedSaved = false,
                    },
                });
            }

            sections = await sectionService.ExtractAndSaveSectionsAsync(id, profile!.Id, force, ct);

            var doc = await appDb.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            return Ok(new
            {
                success = true,
                message = force
                    ? $"Re-extracted {sections.Count} policy sections."
                    : sections.Count > 0 && doc?.SectionExtractStatus == "extracted"
                        ? $"Using {sections.Count} saved policy sections (no new Landing AI call)."
                        : $"Extracted {sections.Count} policy sections.",
                data = new
                {
                    id,
                    sectionExtractStatus = doc?.SectionExtractStatus ?? "extracted",
                    sectionCount = sections.Count,
                    sectionExtractedAt = doc?.SectionExtractedAt,
                    sectionExtractProgressLabel = ShowsSectionExtractProgress(doc?.SectionExtractStatus)
                        ? doc?.SectionExtractProgressLabel
                        : null,
                    sectionExtractProgressPct = ShowsSectionExtractProgress(doc?.SectionExtractStatus)
                        ? doc?.SectionExtractProgressPct
                        : null,
                    reusedSaved = !force && doc?.SectionExtractStatus == "extracted",
                },
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>Recompute section PDF page numbers from native PDF text (preferred) or parse cache — no Landing AI credits.</summary>
    [HttpPost("{id:guid}/repair-section-pages")]
    public async Task<IActionResult> RepairSectionPages(Guid id, CancellationToken ct = default)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            if (!sectionPageService.TryQueueRefreshSectionPages(id, out var queueError))
                return BadRequest(new { success = false, message = queueError });

            return Ok(new
            {
                success = true,
                message = "Page repair started — large manuals may take several minutes.",
                data = new
                {
                    repairStatus = "processing",
                },
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { success = false, message = "Failed to repair section page references" });
        }
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

    private static string DetectFileType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "PDF",
            ".doc" or ".docx" => "DOC",
            _ => "DOC",
        };
    }

    private static string DefaultContentType(string fileType) =>
        fileType switch
        {
            "PDF" => "application/pdf",
            "DOC" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };

    private static bool ShowsSectionExtractProgress(string? status) =>
        string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> LoadInternalParsedMarkdownAsync(StoredDocument doc, CancellationToken ct)
    {
        var hash = doc.FileHash?.Trim();
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var cacheKey = await NdStoredDocumentExtractionCache.EnsureKeyAsync(appDb, doc, ct);
        var row = await landingCache.GetParseCacheAsync(cacheKey, ct);
        if (string.IsNullOrWhiteSpace(row?.Markdown) && !string.Equals(cacheKey, hash, StringComparison.OrdinalIgnoreCase))
            row = await landingCache.GetParseCacheAsync(hash, ct);
        return row?.Markdown;
    }

    private static string ResolveInternalContentType(string? fileType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileType))
            return DefaultContentType(fileType.Trim().ToUpperInvariant());

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };
    }
}
