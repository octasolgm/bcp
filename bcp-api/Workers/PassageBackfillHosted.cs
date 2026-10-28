using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Services.Llm;
using Reguliq.Api.Services.LocalDocs;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Workers;

/// <summary>
/// Builds the search passages (retrieval pipeline v4+) of documents indexed before passages existed, or embedded
/// with another model, in the background, so an analysis does not wait for them. Runs shortly after startup and
/// then every few minutes; does nothing while the admin pipeline version is below v4. New uploads get their
/// passages from the indexing job; an analysis still builds any that are missing itself.
/// Runs unscoped (all workspaces), like the other hosted workers: passages take the workspace of their document.
/// </summary>
public sealed class PassageBackfillHosted(
    IServiceScopeFactory scopeFactory,
    ILogger<PassageBackfillHosted> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await BuildMissingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Search passage backfill failed; retrying in {Minutes} min", Interval.TotalMinutes);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task BuildMissingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<RegulWorkflowLlmSettingsService>();
        if (await settings.GetPipelineVersionAsync(ct) < NdRegulPipelineVersions.V4Passages) return;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passages = scope.ServiceProvider.GetRequiredService<NdPassageIndexService>();
        var indexed = await db.NdLocalDocumentExtractions.AsNoTracking()
            .Where(e => e.IndexStatus == "indexed")
            .ToListAsync(ct);

        var built = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var extraction in NdPassageIndexService.PickSearchExtractions(indexed).OrderByDescending(e => e.IndexedAt))
        {
            if (await passages.EnsureCurrentAsync(extraction.Id, ct)) built++;
        }

        if (built > 0)
            logger.LogInformation("Search passage backfill: built passages for {Count} document(s) in {Ms} ms", built, timer.ElapsedMilliseconds);
    }
}
