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
    Reguliq.Api.Workers.IndexingJobQueue indexingQueue,
    Reguliq.Api.Services.LocalDocs.DictionaryExpansionService dictionary,
    SupabaseJwtValidator jwt,
    ILogger<LocalDocumentsController> logger) : NdControllerBase
{
    /// <summary>How long a row can sit in "processing" before it's assumed dead (server restarted/crashed
    /// mid-run) rather than just slow. Tesseract/RapidOCR reliably finish a full document in under 2
    /// minutes, so 5 minutes was a safe margin for them — but it's genuinely too short for Docling
    /// (confirmed via live testing: ~9 minutes for a full document on the "light" pipeline, and GLM-OCR
    /// mode can run for hours). A too-short threshold here doesn't just log a warning — it actively
    /// flips a document that is still correctly working to "failed" the moment any status poll happens
    /// to land past the threshold, which is exactly the bug this fixes.</summary>
    private static TimeSpan StaleProcessingAfterFor(string engine) =>
        OcrEngineNames.IsDocling(engine) ? TimeSpan.FromHours(8)
        : OcrEngineNames.IsAzureDocIntelligence(engine) ? TimeSpan.FromMinutes(15)
        : TimeSpan.FromMinutes(5);

    /// <summary>
    /// One real cancellation token per in-flight Parse call, keyed by document+engine — lets Stop
    /// actually abort the OCR/Docling work instead of just leaving it running in the background.
    /// Deliberately NOT tied to the HTTP request's own CancellationToken (a client disconnect must not
    /// kill minutes/hours of in-progress work — see the CancellationToken.None notes below); only an
    /// explicit call to the stop endpoint cancels it.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Id, string Engine), CancellationTokenSource>
        RunningParses = new();

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
        var isAzureDi = OcrEngineNames.IsAzureDocIntelligence(engine);
        var ocr = isDocling || isAzureDi ? null : engines.Resolve(engine);

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

        byte[]? bytes = null;
        string? signedUrl = null;
        try
        {
            // Azure Document Intelligence fetches the file itself via a signed URL rather than us
            // posting the bytes in the request body — the direct-body upload path is capped at 4 MB by
            // Azure regardless of pricing tier, which real scanned regulation/internal PDFs regularly
            // exceed. Every other engine still needs the bytes downloaded locally to run OCR/parsing.
            if (isAzureDi)
                signedUrl = await storage.CreateSignedUrlAsync(doc.StoragePath, expiresInSeconds: 900, ct);
            else
                bytes = await storage.DownloadAsync(doc.StoragePath, ct);
        }
        catch (Exception ex)
        {
            await MarkParseFailedAsync(row, $"Could not access stored file: {ex.Message}", ct);
            return StatusCode(502, new { success = false, message = row.Error });
        }

        var cancelKey = (id, engine);
        var cts = new CancellationTokenSource();
        RunningParses[cancelKey] = cts;

        LocalParseResult result;
        try
        {
            // cts.Token, deliberately NOT the caller's HTTP ct — OCR on a scanned PDF can outlast the
            // caller's HTTP timeout; a client disconnect must not throw away minutes (or, for Docling GLM
            // mode, potentially hours) of in-progress work. Only the Stop endpoint cancels this token.
            result = isDocling
                ? await extraction.ParseWithDoclingAsync(
                    bytes!, fileName, engine == OcrEngineNames.DoclingGlm ? "glm" : "light", cts.Token)
                : isAzureDi
                    ? await extraction.ParseWithAzureDocIntelligenceAsync(signedUrl!, fileName, cts.Token)
                    : await extraction.ParseAsync(bytes!, fileName, ocr!, cts.Token);
        }
        catch (OperationCanceledException)
        {
            await MarkParseFailedAsync(row, "Extraction stopped by user. Click Parse to retry.", CancellationToken.None);
            return Ok(new { success = true, data = ToDto(id, fileName, row) });
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
        finally
        {
            RunningParses.TryRemove(cancelKey, out _);
            cts.Dispose();
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

        // Only internal documents get indexed — gov clauses (regulation documents) are the query side
        // of the hybrid pipeline, never the indexed side. See docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md
        // Step 0.
        var isRegulationDocument = await db.NdRegulationDocuments.AnyAsync(d => d.StoredDocumentId == id, ct);
        if (!isRegulationDocument)
        {
            row.IndexStatus = "pending";
            row.IndexError = null;
        }

        // See the same CancellationToken.None note in Parse() above.
        await db.SaveChangesAsync(CancellationToken.None);

        if (!isRegulationDocument)
            indexingQueue.Enqueue(new Reguliq.Api.Workers.IndexingJobMessage(row.Id));

        // Query expansion (hybrid pipeline Step 1) — runs for every document, regulation and
        // internal both, unlike indexing above. A single regex pass over in-memory text, not a
        // background job — see docs/roadmap/QUERY-EXPANSION-PLAN.md. Never allowed to fail the
        // Extract call itself.
        try
        {
            await dictionary.HarvestFromDocumentAsync(row.MarkdownText, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Acronym harvest failed for {DocId} — extract itself still succeeds", id);
        }

        // Synonym candidate harvesting — embedding-similarity based, not text-shape, so it needs
        // the already-split sections (result.Sections) rather than raw markdown. Suggestions only
        // (Source="auto", IsActive=false) — never used in analysis until an admin approves them.
        try
        {
            await dictionary.HarvestSynonymCandidatesAsync(result.Sections, id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Synonym candidate harvest failed for {DocId} — extract itself still succeeds", id);
        }

        return Ok(new { success = true, data = ToDto(id, fileName, row) });
    }

    /// <summary>
    /// Manual re-trigger for Step 0's indexing — for the V5 pipeline panel's per-document "Index"
    /// action when a document was extracted before indexing existed, or its IndexStatus is
    /// "failed". Mirrors the auto-enqueue Extract already does on success; internal documents
    /// only, same as Extract's own gate.
    /// </summary>
    [HttpPost("{id:guid}/reindex")]
    public async Task<IActionResult> Reindex(string engine, Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        if (!OcrEngineNames.IsValid(engine))
            return BadRequest(new { success = false, message = $"Unknown OCR engine '{engine}'." });

        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == id && x.Engine == engine, ct);
        if (row == null || row.ExtractStatus != "extracted")
            return BadRequest(new { success = false, message = "Extract this document first." });

        var isRegulationDocument = await db.NdRegulationDocuments.AnyAsync(d => d.StoredDocumentId == id, ct);
        if (isRegulationDocument)
            return BadRequest(new { success = false, message = "Regulation documents are not indexed." });

        row.IndexStatus = "pending";
        row.IndexError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        indexingQueue.Enqueue(new Reguliq.Api.Workers.IndexingJobMessage(row.Id));

        return Ok(new { success = true, data = ToDto(id, null, row) });
    }

    /// <summary>
    /// Step 2, alternative method — semantic extraction (embedding-based, via Azure OpenAI). Runs off
    /// the same already-parsed <see cref="NdLocalDocumentExtraction.MarkdownText"/> as the regular
    /// Extract above, into its own separate Semantic* fields — never touches or requires the structural
    /// result. Does not re-Parse, so no Azure Document Intelligence credit is spent running this.
    /// </summary>
    [HttpPost("{id:guid}/extract-semantic")]
    public async Task<IActionResult> ExtractSemantic(string engine, Guid id, CancellationToken ct)
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

        row.SemanticExtractStatus = "processing";
        row.SemanticExtractError = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        LocalExtractionResult result;
        try
        {
            // CancellationToken.None, deliberately — same reasoning as Parse() above: many Azure OpenAI
            // embedding calls can genuinely outlast the caller's HTTP timeout on a long document. A
            // client giving up must not (a) waste the calls already made, or (b) leave this row stuck at
            // "processing" forever because the failure-path save below used the same cancelled token.
            result = await extraction.ExtractSemanticAsync(
                fileName, row.MarkdownText, row.TotalPages ?? 0, row.OcrPageCount ?? 0, CancellationToken.None);
        }
        catch (Exception ex)
        {
            row.SemanticExtractStatus = "failed";
            row.SemanticExtractError = $"Semantic extract failed: {ex.Message}";
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return StatusCode(500, new { success = false, message = row.SemanticExtractError });
        }

        row.SemanticExtractStatus = "extracted";
        row.SemanticSectionCount = result.Sections.Count;
        row.SemanticSectionsJson = JsonSerializer.Serialize(result.Sections);
        row.SemanticWarningsJson = JsonSerializer.Serialize(result.Warnings);
        row.SemanticExtractError = null;
        row.SemanticExtractedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

        return Ok(new { success = true, data = ToDto(id, fileName, row) });
    }

    /// <summary>
    /// Stops an in-flight Parse. Cancels the real token if the extraction is still running in this
    /// process; if no in-memory token is found (e.g. the API restarted after the call started, so the
    /// original request is orphaned server-side and will never come back), still flips the row to
    /// failed so the UI is never stuck waiting on it.
    /// </summary>
    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(string engine, Guid id, CancellationToken ct)
    {
        var (_, _, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (!OcrEngineNames.IsValid(engine))
            return BadRequest(new { success = false, message = $"Unknown OCR engine '{engine}'." });

        var row = await db.NdLocalDocumentExtractions
            .FirstOrDefaultAsync(x => x.StoredDocumentId == id && x.Engine == engine, ct);
        if (row == null)
            return BadRequest(new { success = false, message = "No extraction found for this document." });

        var wasProcessing = row.Status == "processing" || row.ExtractStatus == "processing";
        if (!wasProcessing)
            return BadRequest(new { success = false, message = "No extraction is running for this document." });

        if (RunningParses.TryRemove((id, engine), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (row.Status == "processing")
        {
            row.Status = "failed";
            row.Error = "Extraction stopped by user. Click Parse to retry.";
        }
        if (row.ExtractStatus == "processing")
        {
            row.ExtractStatus = "failed";
            row.ExtractError = "Extraction stopped by user. Click Extract to retry.";
        }
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, message = "Extraction stopped.", data = ToDto(id, null, row) });
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
        // Semantic extraction now runs with CancellationToken.None server-side (see ExtractSemantic),
        // so a client timeout alone can't leave this stuck anymore — but a real server crash/restart
        // mid-run still needs the same safety net the structural fields already have.
        if (row.SemanticExtractStatus == "processing"
            && DateTimeOffset.UtcNow - row.UpdatedAt > TimeSpan.FromMinutes(15))
        {
            row.SemanticExtractStatus = "failed";
            row.SemanticExtractError = "Semantic extract did not finish (server restarted or crashed mid-run). Click Extract (semantic) to retry.";
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

    /// <summary>Batch status lookup for a document list under one engine — avoids one request per row.
    /// <c>lite=true</c> is for readiness checks (picker rows, the pipeline panel): the database is asked for
    /// the status columns only, never the parsed markdown or the section JSON, which are hundreds of KB per
    /// document — a full lookup of a dozen documents took 11-19 s and 1.5 MB per call, on a page that polls.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> StatusBatch(
        string engine,
        [FromQuery] string ids,
        [FromQuery] bool lite,
        CancellationToken ct)
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

        if (lite)
            return Ok(new { success = true, data = await LoadLiteStatusAsync(engine, idList, ct) });

        var rows = await db.NdLocalDocumentExtractions
            .Where(x => idList.Contains(x.StoredDocumentId) && x.Engine == engine)
            .ToListAsync(ct);

        foreach (var row in rows)
            await RecoverIfStaleAsync(row, ct);

        var byId = rows.ToDictionary(r => r.StoredDocumentId.ToString(), r => ToDto(r.StoredDocumentId, null, r));
        return Ok(new { success = true, data = byId });
    }

    private sealed record LiteStatusRow(
        Guid Id,
        Guid StoredDocumentId,
        string Engine,
        string Status,
        int? TotalPages,
        int? OcrPageCount,
        string? Error,
        DateTimeOffset? ParsedAt,
        string ExtractStatus,
        int? SectionCount,
        string? ExtractError,
        DateTimeOffset? ExtractedAt,
        string SemanticExtractStatus,
        int? SemanticSectionCount,
        string? SemanticExtractError,
        DateTimeOffset? SemanticExtractedAt,
        string IndexStatus,
        string? IndexError,
        DateTimeOffset? IndexedAt)
    {
        public static LiteStatusRow From(NdLocalDocumentExtraction x) => new(
            x.Id, x.StoredDocumentId, x.Engine, x.Status, x.TotalPages, x.OcrPageCount, x.Error, x.ParsedAt,
            x.ExtractStatus, x.SectionCount, x.ExtractError, x.ExtractedAt,
            x.SemanticExtractStatus, x.SemanticSectionCount, x.SemanticExtractError, x.SemanticExtractedAt,
            x.IndexStatus, x.IndexError, x.IndexedAt);

        public bool InFlight =>
            Status == "processing" || ExtractStatus == "processing" || SemanticExtractStatus == "processing";
    }

    /// <summary>Status columns only (see <see cref="StatusBatch"/>). Rows that are mid-run still go through the
    /// same stale-run recovery as a full lookup — those are loaded as tracked entities, but there are few.</summary>
    private async Task<Dictionary<string, object>> LoadLiteStatusAsync(
        string engine,
        List<Guid> idList,
        CancellationToken ct)
    {
        var rows = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(x => idList.Contains(x.StoredDocumentId) && x.Engine == engine)
            .Select(x => new LiteStatusRow(
                x.Id, x.StoredDocumentId, x.Engine, x.Status, x.TotalPages, x.OcrPageCount, x.Error, x.ParsedAt,
                x.ExtractStatus, x.SectionCount, x.ExtractError, x.ExtractedAt,
                x.SemanticExtractStatus, x.SemanticSectionCount, x.SemanticExtractError, x.SemanticExtractedAt,
                x.IndexStatus, x.IndexError, x.IndexedAt))
            .ToListAsync(ct);

        var inFlightIds = rows.Where(r => r.InFlight).Select(r => r.Id).ToList();
        if (inFlightIds.Count > 0)
        {
            var tracked = await db.NdLocalDocumentExtractions
                .Where(x => inFlightIds.Contains(x.Id))
                .ToListAsync(ct);
            foreach (var row in tracked)
                await RecoverIfStaleAsync(row, ct);
            var recovered = tracked.ToDictionary(t => t.Id, LiteStatusRow.From);
            rows = rows.Select(r => recovered.TryGetValue(r.Id, out var fresh) ? fresh : r).ToList();
        }

        return rows.ToDictionary(r => r.StoredDocumentId.ToString(), r => (object)ToLiteDto(r));
    }

    /// <summary>Same shape as <see cref="ToDto"/> so the client type is unchanged, with the heavy fields empty
    /// and <c>lite: true</c> so a caller can tell they are not the real (empty) sections.</summary>
    private static object ToLiteDto(LiteStatusRow r) => new
    {
        documentId = r.StoredDocumentId,
        fileName = (string?)null,
        engine = r.Engine,
        status = r.Status,
        totalPages = r.TotalPages,
        ocrPageCount = r.OcrPageCount,
        error = r.Error,
        parsedAt = r.ParsedAt,
        markdownText = (string?)null,
        extractStatus = r.ExtractStatus,
        sectionCount = r.SectionCount,
        warnings = Array.Empty<string>(),
        sections = Array.Empty<LocalSection>(),
        extractError = r.ExtractError,
        extractedAt = r.ExtractedAt,
        semanticExtractStatus = r.SemanticExtractStatus,
        semanticSectionCount = r.SemanticSectionCount,
        semanticWarnings = Array.Empty<string>(),
        semanticSections = Array.Empty<LocalSection>(),
        semanticExtractError = r.SemanticExtractError,
        semanticExtractedAt = r.SemanticExtractedAt,
        indexStatus = r.IndexStatus,
        indexError = r.IndexError,
        indexedAt = r.IndexedAt,
        lite = true,
    };

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
        semanticExtractStatus = row.SemanticExtractStatus,
        semanticSectionCount = row.SemanticSectionCount,
        semanticWarnings = JsonSerializer.Deserialize<List<string>>(row.SemanticWarningsJson) ?? [],
        semanticSections = JsonSerializer.Deserialize<List<LocalSection>>(row.SemanticSectionsJson) ?? [],
        semanticExtractError = row.SemanticExtractError,
        semanticExtractedAt = row.SemanticExtractedAt,
        indexStatus = row.IndexStatus,
        indexError = row.IndexError,
        indexedAt = row.IndexedAt,
    };

    /// <summary>Which extensions local extraction currently accepts — for the upload picker to filter on.
    /// Same for every engine, so {engine} is accepted but unused here.</summary>
    [HttpGet("supported-types")]
    public IActionResult SupportedTypes(string engine) =>
        Ok(new { success = true, data = SupportedDocumentTypes.AllowedExtensions.OrderBy(x => x) });
}
