using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Report-level gap evidence: one upload is linked to every open gap in the run so the whole
/// report can be re-checked against a newly issued policy document.
/// </summary>
[ApiController]
[Route("nd/results/{runId:guid}/gap-evidence")]
public class RunGapEvidenceController(
    AppDbContext db,
    SupabaseStorageService storage,
    NdInternalParseService parseService,
    NdDemoUserDirectory demoDirectory,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    [HttpPost]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> Upload(
        Guid runId,
        [FromForm] List<IFormFile> files,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Analysis run not found." });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id) return StatusCode(403);

        if (!storage.IsConfigured)
            return BadRequest(new { success = false, message = "Supabase Storage not configured." });
        if (files == null || files.Count == 0)
            return BadRequest(new { success = false, message = "No files provided." });

        var openPointIds = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == runId
                && (p.FinalStatus == "non_compliant" || p.FinalStatus == "partial_compliant"))
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (openPointIds.Count == 0)
            return Ok(new { success = true, data = Array.Empty<object>(), linkedPoints = 0 });

        var skipLiveParse = await NdDemoIsolationHelper.ShouldSimulateAiAsync(
            demoDirectory, profile.Id, run.CreatedBy, ct);

        var uploaded = new List<object>();
        foreach (var file in files.Where(f => f.Length > 0))
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload.pdf";
            var objectPath = $"documents/nd/gap-evidence/{runId:N}/report/{Guid.NewGuid():N}/{safeName}";

            await using (var stream = new MemoryStream(bytes))
                await storage.UploadAsync(objectPath, stream, file.ContentType ?? "application/pdf", true, ct);

            var doc = new StoredDocument
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
            db.StoredDocuments.Add(doc);
            await db.SaveChangesAsync(ct);

            if (skipLiveParse)
            {
                doc.ParseStatus = "skipped";
                doc.ParseError = "Demo mode — evidence upload stored without live AI parse.";
                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                try
                {
                    await parseService.EnsureParsedAsync(doc, bytes, ct, profile.Id);
                }
                catch
                {
                    /* parse can be retried at rerun */
                }
            }

            foreach (var pointId in openPointIds)
            {
                db.NdAnalysisPointAttachments.Add(new NdAnalysisPointAttachment
                {
                    AnalysisPointId = pointId,
                    StoredDocumentId = doc.Id,
                    FileName = file.FileName,
                    UploadedBy = profile.Id,
                });
            }
            await db.SaveChangesAsync(ct);

            uploaded.Add(new
            {
                storedDocumentId = doc.Id,
                fileName = file.FileName,
                parseStatus = doc.ParseStatus,
                sizeBytes = doc.SizeBytes,
            });
        }

        return Ok(new { success = true, data = uploaded, linkedPoints = openPointIds.Count });
    }
}
