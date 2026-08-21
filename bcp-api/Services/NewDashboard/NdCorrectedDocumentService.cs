using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// On finalize, the reviewer is shown a corrected copy of each internal document the
/// run examined, with the gaps treated as addressed. The corrected file is stored as
/// the next version of the same document so the library shows v1 → v2 for that title.
///
/// The generation itself is simulated: the new version points at the same stored file
/// as the original, and carries a note recording which run produced it.
/// </summary>
public class NdCorrectedDocumentService(AppDbContext db, ILogger<NdCorrectedDocumentService> logger)
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

        foreach (var source in sources)
        {
            var siblings = await db.StoredDocuments
                .Where(d => d.Title == source.Title && d.DocKind == source.DocKind)
                .ToListAsync(ct);

            var marker = RunMarker(runId);
            if (siblings.Any(d => d.HistoryJson.Contains(marker, StringComparison.Ordinal)))
                continue;

            var nextVersion = siblings.Max(d => d.VersionNumber) + 1;
            var copy = new StoredDocument
            {
                Title = source.Title,
                OriginalFileName = source.OriginalFileName,
                FileType = source.FileType,
                Category = source.Category,
                FilterKey = source.FilterKey,
                DocKind = source.DocKind,
                Version = $"v{nextVersion}",
                VersionNumber = nextVersion,
                Status = "review-due",
                Pages = source.Pages,
                SizeBytes = source.SizeBytes,
                ContentType = source.ContentType,
                StorageBucket = source.StorageBucket,
                StoragePath = source.StoragePath,
                SourceStoragePath = source.SourceStoragePath,
                FileHash = source.FileHash,
                WorkspaceId = source.WorkspaceId,
                UploadedBy = actorId,
                // The corrected copy has not been through Landing AI yet.
                ParseStatus = "pending",
                SectionExtractStatus = "pending",
                HistoryJson = BuildHistory(source, runId, nextVersion),
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

    private static string RunMarker(Guid runId) => $"\"generatedFromRunId\":\"{runId}\"";

    private static string BuildHistory(StoredDocument source, Guid runId, int version)
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
            note = "Corrected copy generated on final review, with identified gaps treated as addressed.",
            generatedFromRunId = runId.ToString(),
            generatedFromDocumentId = source.Id.ToString(),
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
