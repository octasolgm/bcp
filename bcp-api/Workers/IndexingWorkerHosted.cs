using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Services.LocalDocs;

namespace Reguliq.Api.Workers;

/// <summary>
/// Starts the local indexing queue and processes jobs: for one internal document's extraction row,
/// embed every section and store it in <see cref="NdLocalDocumentExtractionSection"/>, per
/// docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md Step 0. Automatic — nothing calls this directly except
/// LocalDocumentsController.Extract() enqueuing right after Extract succeeds. Same
/// IHostedService/LocalJobQueue.StartAsync shape as <see cref="DualVerifyWorkerHosted"/>, but its own
/// queue instance (<see cref="IndexingJobQueue"/>) so this pipeline never shares state with dual-verify.
/// </summary>
public class IndexingWorkerHosted(
    IndexingJobQueue queue,
    IServiceScopeFactory scopeFactory,
    LocalEmbeddingService embedder,
    ILogger<IndexingWorkerHosted> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        queue.RegisterHandler((msg, ct) => ProcessJobAsync(msg, ct));
        _ = queue.StartAsync(cancellationToken);
        logger.LogInformation("Indexing worker started (local in-process queue)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProcessJobAsync(IndexingJobMessage msg, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.NdLocalDocumentExtractions.FirstOrDefaultAsync(x => x.Id == msg.ExtractionId, ct);
        if (row == null)
        {
            logger.LogWarning("Indexing job skipped — extraction {ExtractionId} no longer exists", msg.ExtractionId);
            return;
        }

        row.IndexStatus = "processing";
        row.IndexError = null;
        await db.SaveChangesAsync(ct);

        try
        {
            var sections = JsonSerializer.Deserialize<List<LocalSection>>(
                row.SectionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            // Re-extract replaces, never appends — a document parsed/extracted again must not accumulate
            // stale duplicate rows alongside the fresh ones.
            var existing = await db.NdLocalDocumentExtractionSections
                .Where(x => x.ExtractionId == msg.ExtractionId)
                .ToListAsync(ct);
            db.NdLocalDocumentExtractionSections.RemoveRange(existing);

            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var vector = embedder.Embed(section.ClauseText);
                db.NdLocalDocumentExtractionSections.Add(new NdLocalDocumentExtractionSection
                {
                    Id = Guid.NewGuid(),
                    ExtractionId = msg.ExtractionId,
                    SectionIndex = i,
                    ClauseNo = section.ClauseNo,
                    ClauseText = section.ClauseText,
                    SourcePage = section.SourcePage,
                    Embedding = new Pgvector.Vector(vector),
                });
            }

            row.IndexStatus = "indexed";
            row.IndexedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Indexed extraction {ExtractionId}: {Count} section(s) embedded", msg.ExtractionId, sections.Count);
        }
        catch (Exception ex)
        {
            row.IndexStatus = "failed";
            row.IndexError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(ex, "Indexing failed for extraction {ExtractionId}", msg.ExtractionId);
        }
    }
}
