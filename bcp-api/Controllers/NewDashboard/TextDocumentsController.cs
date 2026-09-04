using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// A simple third document library — like Internal Documents / Regulation Documents but with no
/// department, no analysis-run linkage, nothing regulation-specific. Just: upload a PDF/Word file,
/// Parse &amp; Extract locally (PdfPig + Tesseract, no Landing AI), and see the result as a
/// Point / Sub-point tree instead of a flat clause list. Reuses the same StoredDocument table (new
/// DocKind "text") and the existing local parse/extract endpoints under nd/local-documents, which
/// already work against any document regardless of kind — no changes needed there.
/// </summary>
[ApiController]
[Route("nd/text-documents")]
public class TextDocumentsController(
    AppDbContext db,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    private const string Kind = "text";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var docs = await db.StoredDocuments.AsNoTracking()
            .Where(d => d.DocKind == Kind && !d.IsHidden)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = docs.Select(d => new
            {
                id = d.Id,
                title = d.Title,
                originalFileName = d.OriginalFileName,
                sizeBytes = d.SizeBytes,
                uploadedAt = d.CreatedAt,
                pageCount = d.Pages,
            }),
        });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(52_428_800)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        var (profile, _, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
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
            var prepared = await uploadPrep.PrepareAsync(bytes, file.FileName, file.ContentType, "documents/nd-text", ct);

            var row = new StoredDocument
            {
                Title = title,
                OriginalFileName = prepared.OriginalFileName,
                FileType = prepared.FileType,
                DocKind = Kind,
                StorageBucket = storage.Bucket,
                StoragePath = prepared.StoragePath,
                FileHash = prepared.FileHash,
                SizeBytes = prepared.SizeBytes,
                ContentType = prepared.ContentType,
                ParseStatus = "pending",
                UploadedBy = profile!.Id,
            };
            db.StoredDocuments.Add(row);
            await db.SaveChangesAsync(ct);

            return Ok(new
            {
                success = true,
                data = new { id = row.Id, title = row.Title, originalFileName = row.OriginalFileName },
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Hide(Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var doc = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == id && d.DocKind == Kind, ct);
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        doc.IsHidden = true;
        doc.HiddenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, message = "Document removed." });
    }

    [HttpGet("{id:guid}/file-url")]
    public async Task<IActionResult> FileUrl(Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!storage.IsConfigured)
            return StatusCode(503, new { success = false, message = "Storage not configured." });

        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.DocKind == Kind, ct);
        if (doc == null || string.IsNullOrWhiteSpace(doc.StoragePath))
            return NotFound(new { success = false, message = "Document not found." });

        var url = await storage.CreateSignedUrlAsync(doc.StoragePath, expiresInSeconds: 1800, ct);
        return Ok(new { success = true, data = new { url, fileName = doc.OriginalFileName } });
    }
}
