using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Models;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/regulation-documents")]
public class RegulationDocumentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdRegulationUploadService uploadService,
    LandingAiGovExtractService govExtract,
    GovPointsService govPoints,
    SupabaseStorageService storage) : NdControllerBase
{
    public record UpdateRegulationRequest(string? DepartmentId);

    public record ManualPointRequest(
        string? PointNumber,
        string? ParentPointNumber,
        string? PointTitle,
        string PointContent,
        string? PageReference);

    public record UpdateManualPointRequest(
        string? PointNumber,
        string? PointTitle,
        string? PointContent,
        string? PageReference);

    private const int StatusActive = 1;
    private const int StatusHidden = -1;

    private static Guid? ParseDepartmentId(UpdateRegulationRequest? body)
    {
        if (body == null || string.IsNullOrWhiteSpace(body.DepartmentId)) return null;
        return Guid.TryParse(body.DepartmentId, out var id) ? id : null;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? departmentId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var deptNames = await LoadDepartmentNamesAsync(ct);

        List<NdRegulationDocument> ndDocs;
        try
        {
            ndDocs = await db.NdRegulationDocuments.AsNoTracking().ToListAsync(ct);
        }
        catch
        {
            ndDocs = [];
        }

        var pointCountMap = new Dictionary<Guid, int>();
        try
        {
            var pointCounts = await db.NdRegulationPoints.AsNoTracking()
                .GroupBy(p => p.RegulationDocumentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in pointCounts) pointCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist */ }

        var ndByStoredId = ndDocs
            .Where(d => d.Status != StatusHidden && d.StoredDocumentId.HasValue && !IsDepartmentOverlay(d))
            .GroupBy(d => d.StoredDocumentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => pointCountMap.GetValueOrDefault(d.Id))
                    .ThenByDescending(d => d.CreatedAt)
                    .First());

        var hiddenStoredIds = ndDocs
            .Where(d => d.Status == StatusHidden && d.StoredDocumentId.HasValue)
            .Select(d => d.StoredDocumentId!.Value)
            .ToHashSet();

        var legacyDocs = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.DocKind == "regulation")
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        var storedById = legacyDocs.ToDictionary(d => d.Id);

        var cachedHashes = await NdLegacyDataQueries.GetExtractCachedHashesAsync(
            db, legacyDocs.Select(d => d.FileHash), ct);

        var items = new List<object>();

        foreach (var leg in legacyDocs)
        {
            if (hiddenStoredIds.Contains(leg.Id)) continue;

            ndByStoredId.TryGetValue(leg.Id, out var overlay);
            // One row per file: skip legacy when an active ND upload exists for this stored doc.
            if (overlay != null) continue;

            var deptOverlay = ndDocs.FirstOrDefault(d =>
                d.StoredDocumentId == leg.Id && d.Status != StatusHidden && IsDepartmentOverlay(d));
            var deptId = deptOverlay?.DepartmentId;
            if (departmentId.HasValue && deptId != departmentId) continue;

            var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(leg, cachedHashes);
            var ndForLegacy = ndDocs.FirstOrDefault(d =>
                d.StoredDocumentId == leg.Id && d.Status != StatusHidden && !IsDepartmentOverlay(d) && !d.IsManual);
            var pointCount = ndForLegacy != null
                ? pointCountMap.GetValueOrDefault(ndForLegacy.Id)
                : (leg.PointCount ?? 0);
            var displayStatus = MapDisplayExtractionStatus(extractionStatus, pointCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status))
                continue;

            items.Add(new
            {
                id = leg.Id,
                source = "legacy",
                name = leg.Title,
                departmentId = deptId,
                departmentName = DeptName(deptNames, deptId),
                extractionStatus = displayStatus,
                pointCount,
                extractedAt = (DateTimeOffset?)null,
                createdAt = leg.CreatedAt,
                storedDocumentId = leg.Id,
                legacyHref = $"/nd/regulation-documents/{leg.Id}",
            });
        }

        var listedStoredIds = new HashSet<Guid>();
        foreach (var d in ndDocs)
        {
            if (d.Status == StatusHidden) continue;
            if (IsDepartmentOverlay(d)) continue;
            if (d.IsManual)
            {
                if (departmentId.HasValue && d.DepartmentId != departmentId) continue;
                var manualCount = pointCountMap.GetValueOrDefault(d.Id);
                items.Add(new
                {
                    id = d.Id,
                    source = "manual",
                    name = d.Name,
                    departmentId = d.DepartmentId,
                    departmentName = DeptName(deptNames, d.DepartmentId),
                    extractionStatus = "manual",
                    pointCount = manualCount,
                    extractedAt = d.ExtractedAt,
                    createdAt = d.CreatedAt,
                    storedDocumentId = (Guid?)null,
                    legacyHref = (string?)null,
                    isManual = true,
                });
                continue;
            }
            if (d.StoredDocumentId is Guid storedId && !listedStoredIds.Add(storedId)) continue;
            if (departmentId.HasValue && d.DepartmentId != departmentId) continue;

            var resolvedCount = pointCountMap.GetValueOrDefault(d.Id);
            var displayStatus = MapDisplayExtractionStatus(d.ExtractionStatus, resolvedCount, isManual: false);
            if (!MatchesStatusFilter(displayStatus, status))
                continue;

            items.Add(new
            {
                id = d.Id,
                source = "nd",
                name = d.Name,
                departmentId = d.DepartmentId,
                departmentName = DeptName(deptNames, d.DepartmentId),
                extractionStatus = displayStatus,
                pointCount = resolvedCount,
                extractedAt = d.ExtractedAt,
                createdAt = d.CreatedAt,
                storedDocumentId = d.StoredDocumentId,
                legacyHref = (string?)null,
            });
        }

        var manualDoc = await EnsureManualDocumentAsync(ct);
        if (!ndDocs.Any(d => d.IsManual))
        {
            var manualCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == manualDoc.Id, ct);
            if (!departmentId.HasValue || manualDoc.DepartmentId == departmentId)
            {
                items.Add(new
                {
                    id = manualDoc.Id,
                    source = "manual",
                    name = manualDoc.Name,
                    departmentId = manualDoc.DepartmentId,
                    departmentName = DeptName(deptNames, manualDoc.DepartmentId),
                    extractionStatus = "completed",
                    pointCount = manualCount,
                    extractedAt = manualDoc.ExtractedAt,
                    createdAt = manualDoc.CreatedAt,
                    storedDocumentId = (Guid?)null,
                    legacyHref = (string?)null,
                    isManual = true,
                });
            }
        }

        var sorted = items
            .OrderByDescending(i => string.Equals(
                (string?)i.GetType().GetProperty("source")!.GetValue(i), "manual", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => (DateTimeOffset)i.GetType().GetProperty("createdAt")!.GetValue(i)!)
            .ToList();

        return Ok(new { success = true, data = sorted });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var deptNames = await LoadDepartmentNamesAsync(ct);

        var doc = await db.NdRegulationDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc != null && !IsDepartmentOverlay(doc))
        {
            if (doc.Status == StatusHidden)
                return NotFound(new { success = false, message = "Not found" });

            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = doc.Id,
                    source = doc.IsManual ? "manual" : "nd",
                    name = doc.Name,
                    departmentId = doc.DepartmentId,
                    departmentName = DeptName(deptNames, doc.DepartmentId),
                    extractionStatus = doc.IsManual ? "completed" : doc.ExtractionStatus,
                    pointCount,
                    extractedAt = doc.ExtractedAt,
                    createdAt = doc.CreatedAt,
                    isManual = doc.IsManual,
                },
            });
        }

        var stored = await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null) return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (overlay?.Status == StatusHidden)
            return NotFound(new { success = false, message = "Not found" });

        var cachedHashes = await NdLegacyDataQueries.GetExtractCachedHashesAsync(db, [stored.FileHash], ct);
        var extractionStatus = NdLegacyDataQueries.LegacyRegulationExtractionStatus(stored, cachedHashes);
        var legacyPointCount = overlay != null
            ? await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == overlay.Id, ct)
            : (stored.PointCount ?? 0);

        return Ok(new
        {
            success = true,
            data = new
            {
                id = stored.Id,
                source = "legacy",
                name = stored.Title,
                departmentId = overlay?.DepartmentId,
                departmentName = DeptName(deptNames, overlay?.DepartmentId),
                extractionStatus,
                pointCount = legacyPointCount,
                extractedAt = (DateTimeOffset?)null,
                createdAt = stored.CreatedAt,
            },
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (ndDoc != null)
        {
            if (ndDoc.IsManual)
                return BadRequest(new { success = false, message = "Manual custom points cannot be hidden." });
            if (ndDoc.Status == StatusHidden)
                return Ok(new { success = true, message = "Already hidden." });

            ndDoc.Status = StatusHidden;
            ndDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Ok(new { success = true, message = "Regulation hidden from library (data kept in database)." });
        }

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null)
            return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (overlay == null)
        {
            overlay = new NdRegulationDocument
            {
                StoredDocumentId = stored.Id,
                Name = stored.Title,
                FilePath = "",
                ExtractionStatus = "completed",
                Status = StatusHidden,
                CreatedBy = profile!.Id,
            };
            db.NdRegulationDocuments.Add(overlay);
        }
        else
        {
            if (overlay.IsManual)
                return BadRequest(new { success = false, message = "Manual custom points cannot be hidden." });
            overlay.Status = StatusHidden;
            overlay.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Regulation hidden from library (data kept in database)." });
    }

    [HttpPut("{id:guid}")]
    [HttpPatch("{id:guid}")]
    public Task<IActionResult> Update(Guid id, [FromBody] UpdateRegulationRequest body, CancellationToken ct) =>
        SetDepartmentInternal(id, body, ct);

    [HttpPost("{id:guid}/department")]
    public Task<IActionResult> SetDepartment(Guid id, [FromBody] UpdateRegulationRequest body, CancellationToken ct) =>
        SetDepartmentInternal(id, body, ct);

    private async Task<IActionResult> SetDepartmentInternal(Guid id, UpdateRegulationRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var departmentId = ParseDepartmentId(body);
        var deptNames = await LoadDepartmentNamesAsync(ct);
        var ndDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (ndDoc != null)
        {
            if (!departmentId.HasValue && IsDepartmentOverlay(ndDoc) && ndDoc.Status != StatusHidden)
                db.NdRegulationDocuments.Remove(ndDoc);
            else
            {
                ndDoc.DepartmentId = departmentId;
                ndDoc.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = ndDoc.Id,
                    departmentId,
                    departmentName = DeptName(deptNames, departmentId),
                },
            });
        }

        var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
        if (stored == null) return NotFound(new { success = false, message = "Not found" });

        var overlay = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);
        if (!departmentId.HasValue)
        {
            if (overlay != null && IsDepartmentOverlay(overlay) && overlay.Status != StatusHidden)
                db.NdRegulationDocuments.Remove(overlay);
            else if (overlay != null)
            {
                overlay.DepartmentId = null;
                overlay.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        else if (overlay == null)
        {
            overlay = new NdRegulationDocument
            {
                StoredDocumentId = stored.Id,
                Name = stored.Title,
                FilePath = "",
                DepartmentId = departmentId,
                ExtractionStatus = "pending",
                CreatedBy = profile!.Id,
            };
            db.NdRegulationDocuments.Add(overlay);
        }
        else
        {
            overlay.DepartmentId = departmentId;
            overlay.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                id = stored.Id,
                departmentId,
                departmentName = DeptName(deptNames, departmentId),
            },
        });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] Guid? departmentId,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "No file provided." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        try
        {
            var doc = await uploadService.UploadAndExtractAsync(
                bytes, file.FileName, file.ContentType ?? "application/pdf",
                departmentId, profile!.Id, ct);

            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == doc.Id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = doc.Id,
                    name = doc.Name,
                    departmentId = doc.DepartmentId,
                    extractionStatus = doc.ExtractionStatus,
                    pointCount,
                },
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/extract")]
    public async Task<IActionResult> Extract(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var doc = await uploadService.ExtractByRegulationIdAsync(id, profile!.Id, ct);
            var pointCount = await db.NdRegulationPoints.CountAsync(p => p.RegulationDocumentId == doc.Id, ct);
            var responseId = doc.StoredDocumentId ?? doc.Id;
            return Ok(new
            {
                success = true,
                cached = false,
                data = new
                {
                    id = responseId,
                    extractionStatus = doc.ExtractionStatus,
                    pointCount,
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

    [HttpGet("{id:guid}/file-url")]
    public async Task<IActionResult> FileUrl(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Supabase Storage not configured." });

        var stored = await ResolveStoredDocumentAsync(id, ct);
        if (stored == null)
            return NotFound(new { success = false, message = "Regulation file not found." });

        var storagePath = stored.StoragePath;
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id || d.StoredDocumentId == id, ct);
            storagePath = ndDoc?.FilePath;
        }

        if (string.IsNullOrWhiteSpace(storagePath))
            return BadRequest(new { success = false, message = "No PDF file available for this regulation." });

        var url = await storage.CreateSignedUrlAsync(storagePath, 3600, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                url,
                expiresIn = 3600,
                fileName = stored.OriginalFileName ?? stored.Title,
            },
        });
    }

    [HttpGet("{id:guid}/points")]
    public async Task<IActionResult> Points(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);

        if (ndDoc != null)
        {
            if (ndDoc.Status == StatusHidden)
                return NotFound(new { success = false, message = "Not found" });

            var points = await db.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == ndDoc.Id)
                .OrderBy(p => p.PointNumber)
                .ToListAsync(ct);

            return Ok(new
            {
                success = true,
                data = points.Select(MapNdPoint).ToList(),
                source = "nd",
                pointCount = points.Count,
            });
        }

        var stored = ndDoc?.StoredDocumentId is Guid storedId
            ? await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId, ct)
            : await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);

        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash))
        {
            return Ok(new { success = true, data = Array.Empty<object>(), source = "none" });
        }

        var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(stored.FileHash, ct);
        var linkedBuiltin =
            string.Equals(stored.FileHash, LandingAiGovExtractService.BuiltinGovFileHash, StringComparison.OrdinalIgnoreCase)
            && loaded.Source is "db-cache" or "seed";

        if (loaded.Source != "db-cache" && !linkedBuiltin)
        {
            if (storage.IsConfigured
                && !string.IsNullOrWhiteSpace(stored.StoragePath)
                && (stored.PointCount ?? 0) > 0)
            {
                try
                {
                    var bytes = await storage.DownloadAsync(stored.StoragePath, ct);
                    var extract = await govExtract.ExtractFromUploadAsync(bytes, stored.OriginalFileName, null, ct);
                    stored.FileHash = extract.FileHash;
                    stored.PointCount = extract.PointCount;
                    stored.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    var extracted = govPoints.GetAllPoints()
                        .OrderBy(p => p.PointId, StringComparer.Ordinal)
                        .Select(p => MapLegacyPoint(id, p))
                        .ToList();

                    return Ok(new
                    {
                        success = true,
                        data = extracted,
                        source = extract.Source,
                    });
                }
                catch
                {
                    // fall through to empty
                }
            }

            return Ok(new { success = true, data = Array.Empty<object>(), source = "none" });
        }

        var legacyPoints = govPoints.GetAllPoints()
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .Select(p => MapLegacyPoint(id, p))
            .ToList();

        return Ok(new
        {
            success = true,
            data = legacyPoints,
            source = loaded.Source,
            pointCount = legacyPoints.Count,
        });
    }

    [HttpPost("{id:guid}/manual-points")]
    public async Task<IActionResult> CreateManualPoint(
        Guid id,
        [FromBody] ManualPointRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        string pointNumber;
        if (!string.IsNullOrWhiteSpace(body.PointNumber))
        {
            var validation = await ValidateManualPointAsync(doc.Id, body.PointNumber, null, ct);
            if (validation != null) return BadRequest(new { success = false, message = validation });
            pointNumber = NormalizeManualPointNumber(body.PointNumber);
        }
        else
        {
            try
            {
                pointNumber = await AllocateNextManualPointNumberAsync(doc.Id, body.ParentPointNumber, ct);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        var point = new NdRegulationPoint
        {
            RegulationDocumentId = doc.Id,
            PointNumber = pointNumber,
            PointTitle = string.IsNullOrWhiteSpace(body.PointTitle) ? null : body.PointTitle.Trim(),
            PointContent = body.PointContent.Trim(),
            PageReference = string.IsNullOrWhiteSpace(body.PageReference) ? null : body.PageReference.Trim(),
        };
        db.NdRegulationPoints.Add(point);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { success = false, message = $"Could not save point: {inner}" });
        }

        return Ok(new { success = true, data = MapNdPoint(point) });
    }

    [HttpPut("{id:guid}/manual-points/{pointId:guid}")]
    public async Task<IActionResult> UpdateManualPoint(
        Guid id,
        Guid pointId,
        [FromBody] UpdateManualPointRequest body,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        var point = await db.NdRegulationPoints.FirstOrDefaultAsync(
            p => p.Id == pointId && p.RegulationDocumentId == doc.Id, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found" });

        if (!string.IsNullOrWhiteSpace(body.PointNumber))
        {
            var validation = await ValidateManualPointAsync(doc.Id, body.PointNumber, point.Id, ct);
            if (validation != null) return BadRequest(new { success = false, message = validation });
            point.PointNumber = NormalizeManualPointNumber(body.PointNumber);
        }

        if (body.PointTitle != null)
            point.PointTitle = string.IsNullOrWhiteSpace(body.PointTitle) ? null : body.PointTitle.Trim();
        if (body.PointContent != null)
            point.PointContent = body.PointContent.Trim();
        if (body.PageReference != null)
            point.PageReference = string.IsNullOrWhiteSpace(body.PageReference) ? null : body.PageReference.Trim();

        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = MapNdPoint(point) });
    }

    [HttpDelete("{id:guid}/manual-points/{pointId:guid}")]
    public async Task<IActionResult> DeleteManualPoint(Guid id, Guid pointId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await RequireManualDocumentAsync(id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Manual regulation document not found" });

        var point = await db.NdRegulationPoints.FirstOrDefaultAsync(
            p => p.Id == pointId && p.RegulationDocumentId == doc.Id, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found" });

        var hasChildren = await db.NdRegulationPoints.AnyAsync(
            p => p.RegulationDocumentId == doc.Id
                 && p.Id != point.Id
                 && p.PointNumber.StartsWith(point.PointNumber.TrimEnd('.') + "."),
            ct);
        if (hasChildren)
            return BadRequest(new { success = false, message = "Remove child points before deleting this point." });

        db.NdRegulationPoints.Remove(point);
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    private async Task<NdRegulationDocument> EnsureManualDocumentAsync(CancellationToken ct)
    {
        var existing = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.IsManual, ct);
        if (existing != null) return existing;

        var doc = new NdRegulationDocument
        {
            Name = "Manual custom points",
            FilePath = "",
            IsManual = true,
            ExtractionStatus = "completed",
            ExtractedAt = DateTimeOffset.UtcNow,
        };
        db.NdRegulationDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    private async Task<NdRegulationDocument?> RequireManualDocumentAsync(Guid id, CancellationToken ct)
    {
        var doc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == id && d.IsManual, ct);
        return doc;
    }

    private static bool MatchesStatusFilter(string displayStatus, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return string.Equals(displayStatus, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapDisplayExtractionStatus(string rawStatus, int pointCount, bool isManual)
    {
        if (isManual) return "manual";
        if (string.Equals(rawStatus, "completed", StringComparison.OrdinalIgnoreCase) || pointCount > 0)
            return "extracted";
        return "pending";
    }

    private static int ResolvePointCount(
        NdRegulationDocument doc,
        IReadOnlyDictionary<Guid, int> pointCountMap,
        StoredDocument? stored,
        int legacySeedCount = 0)
    {
        _ = stored;
        _ = legacySeedCount;
        return pointCountMap.GetValueOrDefault(doc.Id);
    }

    private int BuiltinSeedPointCount(StoredDocument? stored)
    {
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return 0;
        if (!string.Equals(
                stored.FileHash,
                LandingAiGovExtractService.BuiltinGovFileHash,
                StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            return govPoints.GetAllPoints().Count;
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizePointNumberKey(string? pointNumber) =>
        (pointNumber ?? "").Trim().TrimEnd('.');

    private async Task<List<object>> BuildMergedPointListAsync(
        NdRegulationDocument ndDoc,
        Guid requestId,
        List<NdRegulationPoint> ndPoints,
        CancellationToken ct)
    {
        var stored = await ResolveStoredForNdDocAsync(ndDoc, requestId, ct);
        var legacyGov = await TryLoadLegacyGovPointsAsync(stored, ct);
        if (legacyGov == null || legacyGov.Count == 0)
            return ndPoints.Select(MapNdPoint).Cast<object>().ToList();

        var ndByNumber = ndPoints
            .GroupBy(p => NormalizePointNumberKey(p.PointNumber))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapDocId = ndDoc.Id;

        foreach (var gov in legacyGov)
        {
            var key = NormalizePointNumberKey(gov.PointId);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            if (ndByNumber.TryGetValue(key, out var nd))
                merged.Add(MapNdPoint(nd));
            else
                merged.Add(MapLegacyPoint(mapDocId, gov));
        }

        foreach (var nd in ndPoints.OrderBy(p => p.PointNumber))
        {
            var key = NormalizePointNumberKey(nd.PointNumber);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
            merged.Add(MapNdPoint(nd));
        }

        return merged;
    }

    private async Task<StoredDocument?> ResolveStoredForNdDocAsync(
        NdRegulationDocument ndDoc,
        Guid requestId,
        CancellationToken ct)
    {
        if (ndDoc.StoredDocumentId is Guid storedId)
        {
            return await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
        }

        return await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == requestId && d.DocKind == "regulation", ct);
    }

    private async Task<List<GovPoint>?> TryLoadLegacyGovPointsAsync(
        StoredDocument? stored,
        CancellationToken ct)
    {
        if (stored == null || string.IsNullOrWhiteSpace(stored.FileHash)) return null;

        var loaded = await govExtract.LoadFromDatabaseOrSeedAsync(stored.FileHash, ct);
        var linkedBuiltin =
            string.Equals(stored.FileHash, LandingAiGovExtractService.BuiltinGovFileHash, StringComparison.OrdinalIgnoreCase)
            && loaded.Source is "db-cache" or "seed";

        if (loaded.Source != "db-cache" && !linkedBuiltin)
            return null;

        return govPoints.GetAllPoints()
            .OrderBy(p => p.PointId, StringComparer.Ordinal)
            .ToList();
    }

    private static int CountPointsInExtractionResult(string? extractionResult)
    {
        if (string.IsNullOrWhiteSpace(extractionResult)) return 0;
        try
        {
            using var json = JsonDocument.Parse(extractionResult);
            if (json.RootElement.TryGetProperty("points", out var points)
                && points.ValueKind == JsonValueKind.Array)
                return points.GetArrayLength();
        }
        catch { /* ignore malformed json */ }
        return 0;
    }

    private async Task<string> AllocateNextManualPointNumberAsync(
        Guid docId,
        string? parentPointNumber,
        CancellationToken ct)
    {
        var numbers = await db.NdRegulationPoints.AsNoTracking()
            .Where(p => p.RegulationDocumentId == docId)
            .Select(p => p.PointNumber)
            .ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(parentPointNumber))
        {
            var nextMain = numbers
                .Select(n => int.TryParse(NormalizeManualPointNumber(n).Split('.')[0], out var v) ? v : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
            return nextMain.ToString();
        }

        var parent = NormalizeManualPointNumber(parentPointNumber);
        var parentDepth = parent.Split('.').Length;
        if (parentDepth >= 3)
            throw new InvalidOperationException("Maximum nesting depth is 3 levels (e.g. 2.1.1).");

        if (!numbers.Any(n => NormalizeManualPointNumber(n) == parent))
            throw new InvalidOperationException($"Parent point {parent} must exist before adding a sub-point.");

        var siblingNext = numbers
            .Where(n => GetManualParentNumber(n) == parent)
            .Select(n =>
            {
                var parts = NormalizeManualPointNumber(n).Split('.');
                return int.TryParse(parts[^1], out var v) ? v : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{parent}.{siblingNext}";
    }

    private static string NormalizeManualPointNumber(string pointNumber)
    {
        return pointNumber.Trim().TrimEnd('.');
    }

    private static bool IsValidManualPointNumber(string pointNumber, out string? error)
    {
        error = null;
        var normalized = NormalizeManualPointNumber(pointNumber);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Point number is required.";
            return false;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d+(\.\d+){0,2}$"))
        {
            error = "Point number must be like 1, 2.1, or 2.1.1 (up to 3 levels).";
            return false;
        }

        return true;
    }

    private static string? GetManualParentNumber(string pointNumber)
    {
        var normalized = NormalizeManualPointNumber(pointNumber);
        var parts = normalized.Split('.');
        if (parts.Length <= 1) return null;
        return string.Join('.', parts[..^1]);
    }

    private async Task<string?> ValidateManualPointAsync(
        Guid docId,
        string pointNumber,
        Guid? excludePointId,
        CancellationToken ct)
    {
        if (!IsValidManualPointNumber(pointNumber, out var formatError))
            return formatError;

        var normalized = NormalizeManualPointNumber(pointNumber);
        var duplicate = await db.NdRegulationPoints.AnyAsync(
            p => p.RegulationDocumentId == docId
                 && p.PointNumber == normalized
                 && (!excludePointId.HasValue || p.Id != excludePointId.Value),
            ct);
        if (duplicate) return $"Point number {normalized} already exists.";

        var parent = GetManualParentNumber(normalized);
        if (parent != null)
        {
            var parentExists = await db.NdRegulationPoints.AnyAsync(
                p => p.RegulationDocumentId == docId && p.PointNumber == parent, ct);
            if (!parentExists)
                return $"Parent point {parent} must exist before adding {normalized}.";
        }

        return null;
    }

    private static object MapNdPoint(NdRegulationPoint p) => new
    {
        id = p.Id,
        pointNumber = p.PointNumber,
        pointTitle = p.PointTitle,
        pointContent = p.PointContent,
        pageReference = p.PageReference,
        isIntroductionPoint = p.IsIntroductionPoint,
        isAnnexPoint = p.IsAnnexPoint,
    };

    private static object MapLegacyPoint(Guid documentId, GovPoint p)
    {
        var isAnnex = GovPointClassifier.IsAnnexPoint(p.PointId, p.Title, p.Section);
        var isIntro = GovPointClassifier.IsIntroductionPoint(
            p.PointId, p.Title, p.Text, p.Section, null);
        return new
        {
            id = LegacyPointId(documentId, p.PointId, p.Title, isAnnex),
            pointNumber = p.PointId,
            pointTitle = p.Title,
            pointContent = p.Text,
            pageReference = p.Section,
            isIntroductionPoint = isIntro,
            isAnnexPoint = isAnnex,
        };
    }

    private static Guid LegacyPointId(Guid documentId, string pointNumber, string? title = null, bool isAnnex = false) =>
        NdLibraryPointPersistence.LegacyPointId(documentId, pointNumber, title, isAnnex);

    private async Task<Data.Entities.StoredDocument?> ResolveStoredDocumentAsync(Guid id, CancellationToken ct)
    {
        var ndDoc = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, ct)
            ?? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == id, ct);

        if (ndDoc?.StoredDocumentId is Guid storedId)
        {
            return await db.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
        }

        return await db.StoredDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.DocKind == "regulation", ct);
    }

    private async Task<Dictionary<Guid, string>> LoadDepartmentNamesAsync(CancellationToken ct)
    {
        try
        {
            return await db.NdDepartments.AsNoTracking()
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        }
        catch
        {
            return [];
        }
    }

    private static string? DeptName(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id.HasValue && names.TryGetValue(id.Value, out var name) ? name : null;

    private static bool IsNdUpload(NdRegulationDocument d) =>
        !string.IsNullOrWhiteSpace(d.FilePath) &&
        d.FilePath.Contains("regulations/nd", StringComparison.OrdinalIgnoreCase);

    private static bool IsDepartmentOverlay(NdRegulationDocument d) =>
        d.StoredDocumentId.HasValue && string.IsNullOrWhiteSpace(d.FilePath);
}
