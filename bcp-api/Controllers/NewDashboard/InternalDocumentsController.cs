using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/internal-documents")]
public class InternalDocumentsController(
    AppDbContext appDb,
    SupabaseStorageService storage,
    NdInternalParseService parseService,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var docs = await appDb.StoredDocuments.AsNoTracking()
            .Where(d => d.DocKind == "document" || d.DocKind == "internal")
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);

        var items = new List<object>();
        foreach (var d in docs)
        {
            var parseStatus = await parseService.ResolveDisplayParseStatusAsync(d, ct);
            items.Add(new
            {
                id = d.Id,
                source = "legacy",
                title = d.Title,
                name = d.Title,
                originalFileName = d.OriginalFileName,
                version = d.VersionNumber,
                uploaded = d.UpdatedAt,
                uploadedAt = d.UpdatedAt,
                sizeBytes = d.SizeBytes,
                department = d.Category,
                parseStatus,
                parsedAt = d.ParsedAt,
                parseError = d.ParseError,
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
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return BadRequest(new { success = false, message = "Supabase Storage not configured." });
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "No file provided." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
        var safeName = SanitizeFileName(file.FileName);
        var objectPath = $"documents/nd/{NormalizeKey(title)}/{Guid.NewGuid():N}/{safeName}";

        await using (var stream = new MemoryStream(bytes))
            await storage.UploadAsync(objectPath, stream, file.ContentType ?? "application/pdf", true, ct);

        var row = new StoredDocument
        {
            Title = title,
            OriginalFileName = file.FileName,
            FileType = "PDF",
            DocKind = "document",
            StorageBucket = storage.Bucket,
            StoragePath = objectPath,
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            SizeBytes = bytes.Length,
            ContentType = file.ContentType ?? "application/pdf",
            ParseStatus = "pending",
        };
        appDb.StoredDocuments.Add(row);
        await appDb.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                id = row.Id,
                title = row.Title,
                originalFileName = row.OriginalFileName,
                parseStatus = row.ParseStatus,
            },
        });
    }

    [HttpPost("{id:guid}/parse")]
    public async Task<IActionResult> Parse(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(appDb, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            await parseService.ParseByIdAsync(id, ct);
            var doc = await appDb.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id,
                    parseStatus = doc?.ParseStatus ?? "parsed",
                    parsedAt = doc?.ParsedAt,
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
