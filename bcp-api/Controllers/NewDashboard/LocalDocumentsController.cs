using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// V2 — local parse + extract, no Landing AI, no cost, nothing leaves this server. Deliberately a
/// separate route from InternalDocumentsController / RegulationDocumentsController and a separate
/// table (nd_local_document_extractions) from StoredDocument's ParseStatus/SectionExtractStatus, so
/// none of this can affect the existing Landing AI-based pages or their data.
///
/// Every route takes an {engine} segment ("tesseract" | "rapidocr") — the same document can be parsed
/// independently by more than one OCR engine and compared, since each engine gets its own row
/// (unique on StoredDocumentId+Engine). See <see cref="OcrEngineRegistry"/>.
///
/// Parse and Extract are two independent actions, not one combined step — Parse converts the document
/// to text with page references (persisted); Extract splits that already-parsed text into clauses/
/// points (cheap, instant, re-runnable without touching the PDF again). See
/// docs/discussion/REGUL-PIPELINE-BUILD-PLAN.md.
/// </summary>
[ApiController]
[Route("nd/local-documents/{engine}")]
public class LocalDocumentsController(
    AppDbContext db,
    SupabaseStorageService storage,
    LocalDocumentExtractionService extraction,
    OcrEngineRegistry engines,
    SupabaseJwtValidator jwt) : NdControllerBase
{
    /// <summary>How long a row can sit in "processing" before it's assumed dead (server restarted/crashed
    /// mid-run) rather than just slow. Tesseract/RapidOCR reliably finish a full document in under 2
    /// minutes, so 5 minutes was a safe margin for them — but it's genuinely too short for Docling
    /// (confirmed via live testing: ~9 minutes for a full document on the "light" pipeline, and GLM-OCR
    /// mode can run for hours). A too-short threshold here doesn't just log a warning — it actively
    /// flips a document that is still correctly working to "failed" the moment any status poll happens
    /// to land past the threshold, which is exactly the bug this fixes.</summary>
    private static TimeSpan StaleProcessingAfterFor(string engine) =>
        OcrEngineNames.IsDocling(engine) ? TimeSpan.FromHours(8) : TimeSpan.FromMinutes(5);

    /// <summary>
    /// Step 1 — parse to text with page references. Persists the result so a refresh doesn't lose it.
    /// Does not detect clauses/points; call Extract afterward for that.
    /// </summary>
    [HttpPost("{id:guid}/parse")]
    public async Task<IActionResult> Parse(string engine, Guid id, CancellationToken ct)
    {
        var (profile, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!OcrEngineNames.IsValid(engine))
            return BadRequest(new { success = false, message = $"Unknown OCR engine '{engine}'." });
        var isDocling = OcrEngineNames.IsDocling(engine);
        var ocr = isDocling ? null : engines.Resolve(engine);

        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc == null) return NotFound(new { success = false, message = "Document not found." });

        if (string.IsNullOrWhiteSpace(doc.StoragePath))
            return BadRequest(new { success = false, message = "Document has no stored file." });

        var fileName = doc.OriginalFileName ?? doc.Title ?? Path.GetFileName(doc.StoragePath) ?? "document";
        if (!SupportedDocumentTypes.IsSupported(fileName))
            return BadRequest(new
            {
                success = false,
                message = $"'{Path.GetExtension(fileName)}' is not supported by local extraction. Allowed: {SupportedDocumentTypes.DescribeAllowed()}.",
            });

        if (!storage.IsConfigured)
            return StatusCode(500, new { success = false, message = "Storage is not configured." });

        var row = await GetOrCreateRowAsync(id, engine, ct);
        row.Status = "processing";
        row.Error = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        byte[] bytes;
        try
        {
            bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        }
        catch (Exception ex)
        {
            await MarkParseFailedAsync(row, $"Could not download stored file: {ex.Message}", ct);
            return StatusCode(502, new { success = false, message = row.Error });
        }

        LocalParseResult result;
        try
        {
            // CancellationToken.None, deliberately — OCR on a scanned PDF can outlast the caller's HTTP
            // timeout; a client disconnect must not throw away minutes (or, for Docling GLM mode,
            // potentially hours) of in-progress work.
            result = isDocling
                ? await extraction.ParseWithDoclingAsync(
                    bytes, fileName, engine == OcrEngineNames.DoclingGlm ? "glm" : "light", CancellationToken.None)
                : await extraction.ParseAsync(bytes, fileName, ocr!, CancellationToken.None);
        }
        catch (NotSupportedException ex)
        {
            await MarkParseFailedAsync(row, ex.Message, ct);
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            await MarkParseFailedAsync(row, $"Local parse failed: {ex.Message}", ct);
            return StatusCode(500, new { success = false, message = row.Error });
        }

        row.Status = "parsed";
        row.TotalPages = result.TotalPages;
        row.OcrPageCount = result.OcrPageCount;
        row.MarkdownText = result.Markdown;
        row.WarningsJson = JsonSerializer.Serialize(result.Warnings);
        row.Error = null;
        row.ParsedAt = DateTimeOffset.UtcNow;
        row.ParsedBy = profile?.Id;
        // A re-parse invalidates whatever was extracted from the previous markdown.
        row.ExtractStatus = "pending";
        row.SectionCount = null;
        row.SectionsJson = "[]";
        row.ExtractError = null;
        row.ExtractedAt = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        // CancellationToken.None, deliberately — OCR on a large scanned PDF can outlast the caller's
        // HTTP timeout. If the client has already disconnected by the time we get here, that must not
        // throw away minutes of completed OCR work; save it so the next status poll (or Extract) sees it.
        await db.SaveChangesAsync(CancellationToken.None);

        return Ok(new { success = true, data = ToDto(id, fileName, row) });
    }

    /// <summary>
    /// Step 2 — split the already-parsed markdown into clauses/points. Requires Parse to have run
    /// first. Cheap and instant — does not touch the stored file again.
    /// </summary>
    [HttpPost("{id:guid}/extract")]
    public async Task<IActionResult> Extract(string engine, Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!OcrEngineNames.IsValid(engine))
            return BadRequest(new { success = false, message = $"Unknown OCR engine '{engine}'." });

        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == id && x.Engine == engine, ct);
        if (row == null || row.Status != "parsed" || string.IsNullOrWhiteSpace(row.MarkdownText))
            return BadRequest(new { success = false, message = "Parse this document first." });

        var doc = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        var fileName = doc?.OriginalFileName ?? doc?.Title ?? "document";

        row.ExtractStatus = "processing";
        row.ExtractError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        LocalExtractionResult result;
        try
        {
            result = extraction.ExtractFromMarkdown(fileName, row.MarkdownText, row.TotalPages ?? 0, row.OcrPageCount ?? 0);
        }
        catch (Exception ex)
        {
            row.ExtractStatus = "failed";
            row.ExtractError = $"Local extract failed: {ex.Message}";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return StatusCode(500, new { success = false, message = row.ExtractError });
        }

        row.ExtractStatus = "extracted";
        row.SectionCount = result.Sections.Count;
        row.SectionsJson = JsonSerializer.Serialize(result.Sections);
        row.WarningsJson = JsonSerializer.Serialize(result.Warnings);
        row.ExtractError = null;
        row.ExtractedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        // See the same CancellationToken.None note in Parse() above.
        await db.SaveChangesAsync(CancellationToken.None);

        return Ok(new { success = true, data = ToDto(id, fileName, row) });
    }

    private async Task<NdLocalDocumentExtraction> GetOrCreateRowAsync(Guid storedDocumentId, string engine, CancellationToken ct)
    {
        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == storedDocumentId && x.Engine == engine, ct);
        if (row != null) return row;

        row = new NdLocalDocumentExtraction { Id = Guid.NewGuid(), StoredDocumentId = storedDocumentId, Engine = engine };
        db.NdLocalDocumentExtractions.Add(row);
        return row;
    }

    private async Task MarkParseFailedAsync(NdLocalDocumentExtraction row, string error, CancellationToken ct)
    {
        row.Status = "failed";
        row.Error = error;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        // A cancelled request must still be able to record its own failure — see the CancellationToken.None
        // note on the success path above.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// A row left in "processing" (server restarted/crashed mid-run) never recovers on its own —
    /// flip it to failed once it's been stuck longer than a real run could plausibly take.
    /// </summary>
    private async Task<bool> RecoverIfStaleAsync(NdLocalDocumentExtraction row, CancellationToken ct)
    {
        var changed = false;
        var stale = DateTimeOffset.UtcNow - row.UpdatedAt > StaleProcessingAfterFor(row.Engine);
        if (row.Status == "processing" && stale)
        {
            row.Status = "failed";
            row.Error = "Parse did not finish (server restarted or crashed mid-run). Click Parse to retry.";
            changed = true;
        }
        if (row.ExtractStatus == "processing" && stale)
        {
            row.ExtractStatus = "failed";
            row.ExtractError = "Extract did not finish. Click Extract to retry.";
            changed = true;
        }
        if (changed)
        {
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return changed;
    }

    /// <summary>Persisted result for one document under one engine, if any local parse has run against it.</summary>
    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> Status(string engine, Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == id && x.Engine == engine, ct);
        if (row == null) return Ok(new { success = true, data = (object?)null });

        await RecoverIfStaleAsync(row, ct);
        return Ok(new { success = true, data = ToDto(id, null, row) });
    }

    /// <summary>Batch status lookup for a document list under one engine — avoids one request per row.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> StatusBatch(string engine, [FromQuery] string ids, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!OcrEngineNames.IsValid(engine))
            return BadRequest(new { success = false, message = $"Unknown OCR engine '{engine}'." });

        var idList = (ids ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();
        if (idList.Count == 0) return Ok(new { success = true, data = new Dictionary<string, object>() });

        var rows = await db.NdLocalDocumentExtractions
            .Where(x => idList.Contains(x.StoredDocumentId) && x.Engine == engine)
            .ToListAsync(ct);

        foreach (var row in rows)
            await RecoverIfStaleAsync(row, ct);

        var byId = rows.ToDictionary(r => r.StoredDocumentId.ToString(), r => ToDto(r.StoredDocumentId, null, r));
        return Ok(new { success = true, data = byId });
    }

    private static object ToDto(Guid documentId, string? fileName, NdLocalDocumentExtraction row) => new
    {
        documentId,
        fileName,
        engine = row.Engine,
        status = row.Status,
        totalPages = row.TotalPages,
        ocrPageCount = row.OcrPageCount,
        error = row.Error,
        parsedAt = row.ParsedAt,
        markdownText = row.MarkdownText,
        extractStatus = row.ExtractStatus,
        sectionCount = row.SectionCount,
        warnings = JsonSerializer.Deserialize<List<string>>(row.WarningsJson) ?? [],
        sections = JsonSerializer.Deserialize<List<LocalSection>>(row.SectionsJson) ?? [],
        extractError = row.ExtractError,
        extractedAt = row.ExtractedAt,
    };

    /// <summary>Which extensions local extraction currently accepts — for the upload picker to filter on.
    /// Same for every engine, so {engine} is accepted but unused here.</summary>
    [HttpGet("supported-types")]
    public IActionResult SupportedTypes(string engine) =>
        Ok(new { success = true, data = SupportedDocumentTypes.AllowedExtensions.OrderBy(x => x) });
}
