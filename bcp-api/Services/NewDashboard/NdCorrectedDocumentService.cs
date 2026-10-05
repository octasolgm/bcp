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
    NdFinalizeEmbedContentService finalizeEmbedContent,
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
    /// <param name="force">Skips the idempotency check and regenerates even when this run already
    /// has a version — for re-testing the embed step on an already-finalized run without re-running
    /// the (AI-metered) analysis itself. Always adds a new version; never touches or deletes an
    /// existing one.</param>
    public async Task<List<CorrectedVersion>> GenerateForRunAsync(
        Guid runId, Guid? actorId, CancellationToken ct, bool force = false)
    {
        var created = new List<CorrectedVersion>();

        var run = await db.NdAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run == null) return created;

        var docIds = ParseDocIds(run.SelectedInternalDocIds);
        if (docIds.Count == 0) return created;

        var sources = await db.StoredDocuments
            .Where(d => docIds.Contains(d.Id))
            .ToListAsync(ct);

        var embedJobs = await EnrichEmbedJobsAsync(await embedResolver.ResolveForRunAsync(runId, ct), ct);
        var jobsByDocId = embedJobs.ToDictionary(j => j.StoredDocumentId);

        // Decide which sources actually need a new version first — cheap DB reads only, done
        // sequentially since they share this method's DbContext.
        var toGenerate = new List<(StoredDocument Source, int NextVersion)>();
        foreach (var source in sources)
        {
            var siblings = await db.StoredDocuments
                .Where(d => d.Title == source.Title && d.DocKind == source.DocKind && d.TenantId == source.TenantId)
                .ToListAsync(ct);

            var marker = RunMarker(runId);
            if (!force && siblings.Any(d => d.HistoryJson.Contains(marker, StringComparison.Ordinal)))
                continue;

            var nextVersion = siblings.Count == 0 ? 1 : siblings.Max(d => d.VersionNumber) + 1;
            toGenerate.Add((source, nextVersion));
        }

        // The slow part — downloading and (for a malformed PDF) rendering every page — runs a
        // few documents at a time instead of one after another, so a run with many internal
        // documents doesn't take proportionally longer: wall time stays close to a handful of
        // documents' worth, not the full count. None of this touches the shared DbContext, so
        // it's safe to run concurrently; only the entity creation below is sequential.
        var embedResults = new (StoredDocument Source, int NextVersion, EmbeddedFile? Embedded)[toGenerate.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, toGenerate.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, itemCt) =>
            {
                var (source, nextVersion) = toGenerate[i];
                EmbeddedFile? embedded = null;
                if (jobsByDocId.TryGetValue(source.Id, out var job))
                {
                    embedded = await TryEmbedActionPlansAsync(source, job, itemCt);
                    if (embedded == null && job.Targets.Count > 0)
                    {
                        logger.LogWarning(
                            "Finalize embed failed for document {DocumentId} ({Title}) — {TargetCount} action(s) could not be written into the file; corrected copy will match the source bytes.",
                            source.Id, source.Title, job.Targets.Count);
                    }
                }
                embedResults[i] = (source, nextVersion, embedded);
            });

        foreach (var (source, nextVersion, embedded) in embedResults)
        {
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

    private async Task<IReadOnlyList<NdActionPlanEmbedJob>> EnrichEmbedJobsAsync(
        IReadOnlyList<NdActionPlanEmbedJob> jobs,
        CancellationToken ct)
    {
        if (jobs.Count == 0) return jobs;
        var enriched = new List<NdActionPlanEmbedJob>(jobs.Count);
        foreach (var job in jobs)
        {
            var targets = new List<NdActionPlanEmbedTarget>(job.Targets.Count);
            foreach (var target in job.Targets)
            {
                var body = await finalizeEmbedContent.GenerateEmbedBodyAsync(target, ct);
                targets.Add(target with { GeneratedEmbedBody = body });
            }
            enriched.Add(new NdActionPlanEmbedJob(job.StoredDocumentId, targets));
        }
        return enriched;
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
        var fileType = ResolveEmbedFileType(source);
        if (fileType is not ("PDF" or "DOCX" or "DOC")) return null;

        try
        {
            var original = await storage.DownloadAsync(source.StoragePath, ct);
            var useDocxEmbedder = fileType is "DOCX" or "DOC";
            var embeddedBytes = fileType == "PDF"
                ? NdCorrectedPdfEmbedder.Embed(original, job.Targets)
                : useDocxEmbedder
                    ? NdCorrectedDocxEmbedder.Embed(original, job.Targets)
                    : original;
            if (!useDocxEmbedder && fileType != "PDF")
                return null;

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

    // Postgres re-serializes jsonb on every read, always inserting a space after each colon —
    // matching the raw "generatedFromRunId":"..." syntax (no space) never finds a real row.
    // The run id alone is globally unique, so matching on just that substring is safe.
    private static string ResolveEmbedFileType(StoredDocument source)
    {
        var fileType = (source.FileType ?? "").Trim().ToUpperInvariant();
        if (fileType is "PDF" or "DOCX" or "DOC") return fileType;
        var name = source.OriginalFileName ?? source.Title ?? "";
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "PDF";
        if (name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)) return "DOC";
        return fileType;
    }

    private static string RunMarker(Guid runId) => runId.ToString();

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
