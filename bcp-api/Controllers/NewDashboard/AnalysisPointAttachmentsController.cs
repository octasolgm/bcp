using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/results/{runId:guid}/points/{pointId:guid}/attachments")]
public class AnalysisPointAttachmentsController(
    AppDbContext db,
    SupabaseStorageService storage,
    NdInternalParseService parseService,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid runId, Guid pointId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var rows = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => a.AnalysisPointId == pointId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var docIds = rows.Select(r => r.StoredDocumentId).Distinct().ToList();
        var docs = await db.StoredDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        var data = rows.Select(r =>
        {
            docs.TryGetValue(r.StoredDocumentId, out var doc);
            return new
            {
                id = r.Id,
                storedDocumentId = r.StoredDocumentId,
                fileName = r.FileName,
                actionIndex = r.ActionIndex,
                parseStatus = doc?.ParseStatus,
                sizeBytes = doc?.SizeBytes,
                createdAt = r.CreatedAt,
            };
        }).ToList();

        return Ok(new { success = true, data });
    }

    [HttpPost]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> Upload(
        Guid runId,
        Guid pointId,
        [FromForm] List<IFormFile> files,
        [FromForm] int? actionIndex,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        if (!storage.IsConfigured)
            return BadRequest(new { success = false, message = "Supabase Storage not configured." });
        if (files == null || files.Count == 0)
            return BadRequest(new { success = false, message = "No files provided." });

        var uploaded = new List<object>();
        foreach (var file in files.Where(f => f.Length > 0))
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
            var safeName = SanitizeFileName(file.FileName);
            var objectPath =
                $"documents/nd/gap-evidence/{runId:N}/{pointId:N}/{Guid.NewGuid():N}/{safeName}";

            await using (var stream = new MemoryStream(bytes))
                await storage.UploadAsync(objectPath, stream, file.ContentType ?? "application/pdf", true, ct);

            var row = new StoredDocument
            {
                Title = string.IsNullOrWhiteSpace(title) ? safeName : title,
                OriginalFileName = file.FileName,
                FileType = Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant(),
                DocKind = "gap_evidence",
                StorageBucket = storage.Bucket,
                StoragePath = objectPath,
                FileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                SizeBytes = bytes.Length,
                ContentType = file.ContentType ?? "application/pdf",
                ParseStatus = "pending",
            };
            db.StoredDocuments.Add(row);
            await db.SaveChangesAsync(ct);

            try
            {
                await parseService.EnsureParsedAsync(row, bytes, ct);
            }
            catch
            {
                /* parse can be retried at rerun */
            }

            var link = new NdAnalysisPointAttachment
            {
                AnalysisPointId = pointId,
                StoredDocumentId = row.Id,
                FileName = file.FileName,
                ActionIndex = actionIndex,
                UploadedBy = profile!.Id,
            };
            db.NdAnalysisPointAttachments.Add(link);
            await db.SaveChangesAsync(ct);

            uploaded.Add(new
            {
                id = link.Id,
                storedDocumentId = row.Id,
                fileName = link.FileName,
                actionIndex = link.ActionIndex,
                parseStatus = row.ParseStatus,
                sizeBytes = row.SizeBytes,
                createdAt = link.CreatedAt,
            });
        }

        return Ok(new { success = true, data = uploaded });
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<IActionResult> Delete(
        Guid runId,
        Guid pointId,
        Guid attachmentId,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var point = await RequirePointAsync(runId, pointId, profile!, ct);
        if (point == null) return NotFound(new { success = false, message = "Point not found." });

        var link = await db.NdAnalysisPointAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.AnalysisPointId == pointId, ct);
        if (link == null) return NotFound(new { success = false, message = "Attachment not found." });

        db.NdAnalysisPointAttachments.Remove(link);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    private async Task<NdAnalysisPoint?> RequirePointAsync(
        Guid runId,
        Guid pointId,
        NdProfile profile,
        CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return null;
        if (profile.Role == "maker" && run.CreatedBy != profile.Id) return null;

        return await db.NdAnalysisPoints.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pointId && p.AnalysisRunId == runId, ct);
    }

    private static string SanitizeFileName(string name)
    {
        var file = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(file) ? "upload.pdf" : file;
    }
}
