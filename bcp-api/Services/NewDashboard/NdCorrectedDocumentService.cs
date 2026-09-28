using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// On finalize, the reviewer is shown a corrected copy of each internal document the
/// run examined, with the gaps treated as addressed. The corrected file is stored as
/// the next version of the same document so the library shows v1 → v2 for that title.
///
/// When the run has resolved gaps whose action plans were traced back to this document (see
/// <see cref="NdActionPlanEmbedResolver"/>), the copy's actual file content is rewritten to embed
/// each one as a real note next to where its evidence was found (<see cref="NdCorrectedPdfEmbedder"/>,
/// <see cref="NdCorrectedDocxEmbedder"/>). A document with nothing resolved to embed, or a file type
/// neither embedder handles, still gets the original placeholder behavior: the copy points at the same
/// stored file as the source.
/// </summary>
public class NdCorrectedDocumentService(
    AppDbContext db,
    NdActionPlanEmbedResolver embedResolver,
    SupabaseStorageService storage,
    NdStoredDocumentUploadService uploadPrep,
    ILogger<NdCorrectedDocumentService> logger)
{
    public record CorrectedVersion(Guid DocumentId, string Title, int VersionNumber);

    /// <summary>
    /// Creates the corrected version for every internal document attached to the run.
    /// Idempotent per run: a document that already has a version generated from this
    /// run is left alone, so finalizing twice does not stack versions.
    /// </summary>
    public async Task<List<CorrectedVersion>> GenerateForRunAsync(Guid runId, Guid? actorId, CancellationToken ct)
    {
        var created = new List<CorrectedVersion>();

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return created;

        var docIds = ParseDocIds(run.SelectedInternalDocIds);
        if (docIds.Count == 0) return created;

        var sources = await db.StoredDocuments
            .Where(d => docIds.Contains(d.Id))
            .ToListAsync(ct);

        var embedJobs = await embedResolver.ResolveForRunAsync(runId, ct);
        var jobsByDocId = embedJobs.ToDictionary(j => j.StoredDocumentId);

        foreach (var source in sources)
        {
            var siblings = await db.StoredDocuments
                .Where(d => d.Title == source.Title && d.DocKind == source.DocKind && d.TenantId == source.TenantId)
                .ToListAsync(ct);

            var marker = RunMarker(runId);
            if (siblings.Any(d => d.HistoryJson.Contains(marker, StringComparison.Ordinal)))
                continue;

            var nextVersion = siblings.Max(d => d.VersionNumber) + 1;
            var embedded = jobsByDocId.TryGetValue(source.Id, out var job)
                ? await TryEmbedActionPlansAsync(source, job, ct)
                : null;

            var copy = new StoredDocument
            {
                Title = source.Title,
                OriginalFileName = VersionedFileName(embedded?.OriginalFileName ?? source.OriginalFileName, nextVersion),
                FileType = source.FileType,
                Category = source.Category,
                FilterKey = source.FilterKey,
                DocKind = source.DocKind,
                Version = $"v{nextVersion}",
                VersionNumber = nextVersion,
                Status = "review-due",
                Pages = source.Pages,
                SizeBytes = embedded?.SizeBytes ?? source.SizeBytes,
                ContentType = embedded?.ContentType ?? source.ContentType,
                StorageBucket = source.StorageBucket,
                StoragePath = embedded?.StoragePath ?? source.StoragePath,
                SourceStoragePath = source.SourceStoragePath,
                FileHash = embedded?.FileHash ?? source.FileHash,
                WorkspaceId = source.WorkspaceId,
                TenantId = source.TenantId,
                UploadedBy = actorId,
                // The corrected copy has not been through Landing AI yet.
                ParseStatus = "pending",
                SectionExtractStatus = "pending",
                HistoryJson = BuildHistory(source, runId, nextVersion, embedded?.Job),
            };

            db.StoredDocuments.Add(copy);
            created.Add(new CorrectedVersion(copy.Id, copy.Title, nextVersion));
        }

        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Generated {Count} corrected internal document version(s) for run {RunId}",
                created.Count, runId);
        }

        return created;
    }

    /// <summary>
    /// Marks a downloaded filename with its corrected-copy version, e.g. "AML Manual.pdf" becomes
    /// "AML Manual (v2).pdf" — otherwise the download name is byte-identical to the source's, and the
    /// reviewer has no way to tell v1 and v2 apart once the file lands in Downloads.
    /// </summary>
    private static string VersionedFileName(string? originalFileName, int version)
    {
        if (string.IsNullOrWhiteSpace(originalFileName)) return originalFileName ?? "";
        var ext = Path.GetExtension(originalFileName);
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        return $"{stem} (v{version}){ext}";
    }

    private sealed record EmbeddedFile(
        string StoragePath,
        string OriginalFileName,
        string ContentType,
        string FileHash,
        long SizeBytes,
        NdActionPlanEmbedJob Job);

    /// <summary>
    /// Downloads the source file, writes its resolved action-plan notes into a real copy (PDF pages
    /// inserted after the cited page; DOCX paragraphs inserted after the matched text), and uploads the
    /// result as a brand new stored file — the corrected copy no longer points at the source's own
    /// storage path once this succeeds. Returns null (falls back to the original placeholder behavior)
    /// when the file type isn't PDF/DOCX, storage isn't configured, or anything about the download,
    /// embed or upload fails — a broken embed must never block the review from finalizing.
    /// </summary>
    private async Task<EmbeddedFile?> TryEmbedActionPlansAsync(
        StoredDocument source, NdActionPlanEmbedJob job, CancellationToken ct)
    {
        if (!storage.IsConfigured || string.IsNullOrWhiteSpace(source.StoragePath)) return null;
        var fileType = (source.FileType ?? "").Trim().ToUpperInvariant();
        if (fileType is not ("PDF" or "DOCX" or "DOC")) return null;

        try
        {
            var original = await storage.DownloadAsync(source.StoragePath, ct);
            var embeddedBytes = fileType == "PDF"
                ? NdCorrectedPdfEmbedder.Embed(original, job.Targets)
                : NdCorrectedDocxEmbedder.Embed(original, job.Targets);

            var prepared = await uploadPrep.PrepareAsync(
                embeddedBytes,
                source.OriginalFileName,
                source.ContentType,
                "documents/nd/corrected",
                ct);

            return new EmbeddedFile(
                prepared.StoragePath,
                prepared.OriginalFileName,
                prepared.ContentType,
                prepared.FileHash,
                prepared.SizeBytes,
                job);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not embed resolved action plans into document {DocumentId} for run — corrected copy will point at the original file instead.",
                source.Id);
            return null;
        }
    }

    private static string RunMarker(Guid runId) => $"\"generatedFromRunId\":\"{runId}\"";

    private static string BuildHistory(StoredDocument source, Guid runId, int version, NdActionPlanEmbedJob? embeddedJob = null)
    {
        var entries = new List<JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(source.HistoryJson) ? "[]" : source.HistoryJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                entries.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
        }
        catch (JsonException)
        {
            // A malformed history should not block generating the corrected version.
        }

        var entry = JsonSerializer.SerializeToElement(new
        {
            version = $"v{version}",
            action = "corrected_copy_generated",
            note = embeddedJob is { Targets.Count: > 0 }
                ? $"Corrected copy generated on final review, with {embeddedJob.Targets.Count} resolved action plan(s) embedded at their referenced clause(s)."
                : "Corrected copy generated on final review, with identified gaps treated as addressed.",
            generatedFromRunId = runId.ToString(),
            generatedFromDocumentId = source.Id.ToString(),
            embeddedClauses = embeddedJob?.Targets.Select(t => t.ClauseNo).Distinct().ToList() ?? [],
            at = DateTimeOffset.UtcNow,
        });
        entries.Add(entry);

        return JsonSerializer.Serialize(entries);
    }

    private static List<Guid> ParseDocIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? [])
                .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
