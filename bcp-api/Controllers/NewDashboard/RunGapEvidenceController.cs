using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
/// Report-level gap evidence: one upload is linked to every point in the run so the whole
/// report can re-check against a newly issued policy document.
/// </summary>
[ApiController]
[Route("nd/results/{runId:guid}/gap-evidence")]
public class RunGapEvidenceController(
    AppDbContext db,
    SupabaseStorageService storage,
    NdDemoUserDirectory demoDirectory,
    SupabaseJwtValidator jwt,
    IServiceScopeFactory scopeFactory,
    ILogger<RunGapEvidenceController> logger) : NdControllerBase
{
    [HttpPost]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> Upload(
        Guid runId,
        [FromForm] List<IFormFile> files,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Analysis run not found." });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id) return StatusCode(403);

        if (!storage.IsConfigured)
            return BadRequest(new { success = false, message = "Supabase Storage not configured." });
        if (files == null || files.Count == 0)
            return BadRequest(new { success = false, message = "No files provided." });

        var pointIds = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (pointIds.Count == 0)
            return Ok(new { success = true, data = Array.Empty<object>(), linkedPoints = 0 });

        var skipLiveParse = await NdDemoIsolationHelper.ShouldSimulateAiAsync(
            demoDirectory, profile.Id, run.CreatedBy, ct);

        var uploaded = new List<object>();
        var prepareDocIds = new List<Guid>();
        foreach (var file in files.Where(f => f.Length > 0))
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var title = Path.GetFileNameWithoutExtension(file.FileName).Trim();
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload.pdf";
            var version = await ResolveNextGapEvidenceVersionAsync(pointIds, title, ct);
            var objectPath = $"documents/nd/gap-evidence/{runId:N}/report/{Guid.NewGuid():N}/{safeName}";

            await using (var stream = new MemoryStream(bytes))
                await storage.UploadAsync(objectPath, stream, file.ContentType ?? "application/pdf", true, ct);

            var doc = new StoredDocument
            {
                Title = version.DisplayTitle,
                OriginalFileName = file.FileName,
                FileType = Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant(),
                DocKind = "gap_evidence",
                StorageBucket = storage.Bucket,
                StoragePath = objectPath,
                FileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                SizeBytes = bytes.Length,
                ContentType = file.ContentType ?? "application/pdf",
                ParseStatus = skipLiveParse ? "skipped" : "pending",
                ParseError = skipLiveParse ? "Demo mode — evidence upload stored without live AI parse." : null,
                UploadedBy = profile.Id,
                VersionNumber = version.VersionNumber,
                Version = version.VersionLabel,
            };
            db.StoredDocuments.Add(doc);
            await db.SaveChangesAsync(ct);

            var links = new List<NdAnalysisPointAttachment>(pointIds.Count);
            foreach (var pointId in pointIds)
            {
                var link = new NdAnalysisPointAttachment
                {
                    AnalysisPointId = pointId,
                    StoredDocumentId = doc.Id,
                    FileName = file.FileName,
                    UploadedBy = profile.Id,
                };
                db.NdAnalysisPointAttachments.Add(link);
                links.Add(link);
            }
            await db.SaveChangesAsync(ct);

            if (!skipLiveParse)
                prepareDocIds.Add(doc.Id);

            uploaded.Add(new
            {
                storedDocumentId = doc.Id,
                fileName = file.FileName,
                displayTitle = doc.Title,
                version = doc.Version,
                parseStatus = doc.ParseStatus,
                sizeBytes = doc.SizeBytes,
                attachments = links.Select(l => new
                {
                    id = l.Id,
                    analysisPointId = l.AnalysisPointId,
                    storedDocumentId = l.StoredDocumentId,
                    fileName = l.FileName,
                    actionIndex = l.ActionIndex,
                    createdAt = l.CreatedAt,
                    parseStatus = doc.ParseStatus,
                    sizeBytes = doc.SizeBytes,
                }),
            });

        }

        if (prepareDocIds.Count > 0)
            StartBackgroundGapEvidencePrepare(prepareDocIds, run.WorkflowEngine);

        return Ok(new { success = true, data = uploaded, linkedPoints = pointIds.Count });
    }

    private void StartBackgroundGapEvidencePrepare(IReadOnlyList<Guid> storedDocumentIds, string? workflowEngine)
    {
        var ids = storedDocumentIds.Distinct().ToList();
        var engine = workflowEngine;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var prepare = scope.ServiceProvider.GetRequiredService<NdGapEvidencePrepareService>();
                await prepare.PrepareDocumentsAsync(ids, engine, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background gap evidence prepare failed for {Count} document(s)", ids.Count);
            }
        }, CancellationToken.None);
    }

    private async Task<(int VersionNumber, string VersionLabel, string DisplayTitle)> ResolveNextGapEvidenceVersionAsync(
        List<Guid> pointIds,
        string uploadTitle,
        CancellationToken ct)
    {
        var linkedDocIds = await db.NdAnalysisPointAttachments.AsNoTracking()
            .Where(a => pointIds.Contains(a.AnalysisPointId))
            .Select(a => a.StoredDocumentId)
            .Distinct()
            .ToListAsync(ct);

        var baseKey = NormalizeGapEvidenceTitleKey(uploadTitle);
        var maxVer = 0;
        if (linkedDocIds.Count > 0)
        {
            var peers = await db.StoredDocuments.AsNoTracking()
                .Where(d => linkedDocIds.Contains(d.Id) && d.DocKind == "gap_evidence")
                .ToListAsync(ct);
            foreach (var peer in peers)
            {
                var peerKey = NormalizeGapEvidenceTitleKey(peer.Title);
                if (string.IsNullOrEmpty(peerKey)) continue;
                if (peerKey == baseKey)
                    maxVer = Math.Max(maxVer, peer.VersionNumber);
            }
        }

        var next = Math.Max(1, maxVer + 1);
        var stripped = StripTrailingCopySuffix(uploadTitle);
        var display = next <= 1
            ? (string.IsNullOrWhiteSpace(stripped) ? "document" : stripped)
            : $"{stripped} (v{next})";
        return (next, $"v{next}", display);
    }

    private static string NormalizeGapEvidenceTitleKey(string value)
    {
        var s = StripTrailingCopySuffix(value).Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"\s*\(v\d+\)\s*$", "", RegexOptions.IgnoreCase).Trim();
        return s;
    }

    private static string StripTrailingCopySuffix(string value)
    {
        var s = (value ?? "").Trim();
        s = Regex.Replace(s, @"\s*\(\d+\)\s*$", "").Trim();
        return s;
    }

    [HttpDelete("{storedDocumentId:guid}")]
    public async Task<IActionResult> Delete(Guid runId, Guid storedDocumentId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return NotFound(new { success = false, message = "Analysis run not found." });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id) return StatusCode(403);

        var pointIds = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == runId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var links = await db.NdAnalysisPointAttachments
            .Where(a => a.StoredDocumentId == storedDocumentId && pointIds.Contains(a.AnalysisPointId))
            .ToListAsync(ct);
        if (links.Count == 0)
            return NotFound(new { success = false, message = "Attachment not found." });

        db.NdAnalysisPointAttachments.RemoveRange(links);
        await db.SaveChangesAsync(ct);

        var stillLinked = await db.NdAnalysisPointAttachments.AnyAsync(a => a.StoredDocumentId == storedDocumentId, ct);
        if (!stillLinked)
        {
            var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedDocumentId && d.DocKind == "gap_evidence", ct);
            if (doc != null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(doc.StoragePath) && storage.IsConfigured)
                        await storage.DeleteAsync(doc.StoragePath, ct);
                    if (!string.IsNullOrWhiteSpace(doc.SourceStoragePath) && storage.IsConfigured)
                        await storage.DeleteAsync(doc.SourceStoragePath, ct);
                }
                catch
                {
                    /* keep going so the list stays accurate */
                }
                db.StoredDocuments.Remove(doc);
                await db.SaveChangesAsync(ct);
            }
        }

        return Ok(new { success = true });
    }
}
