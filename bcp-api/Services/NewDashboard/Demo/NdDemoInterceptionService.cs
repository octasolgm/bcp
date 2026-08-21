using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard.Demo;

/// <summary>Demo-only interceptors that clone production data instead of calling AI. Never used for production users.</summary>
public sealed class NdDemoInterceptionService(
    AppDbContext db,
    LandingAiCacheRepository cache,
    NdDemoUserDirectory directory,
    DemoAnalysisSeedService demoSeed,
    NdDemoWorkspaceService demoWorkspace,
    NdDashboardCacheService dashboardCache,
    IServiceScopeFactory scopeFactory,
    IOptions<LandingAiOptions> landingAiOptions,
    IOptions<NdDemoIsolationOptions> demoOptions,
    ILogger<NdDemoInterceptionService> logger)
{
    private static readonly Random Rng = Random.Shared;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RegulationSimulateLocks = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningInternalParseJobs = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningInternalExtractJobs = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningRegulationParseJobs = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningRegulationExtractJobs = new();

    /// <summary>
    /// Thresholds for picking a *production* clone source (~397 canonical points). The demo copy
    /// itself is narrowed to the demo admin template clause list, so it holds far fewer points.
    /// </summary>
    private const int MinDemoRegulationClonePoints = 200;
    private const int ExpectedCbuaeRegulationPointCount = 397;
    private const int CbuaeRegulationPointCountTolerance = 25;

    public bool IsActive => NdDemoIsolationHelper.ResolveEnabled(demoOptions.Value);

    public bool CanMutateRegulationDocument(NdRegulationDocument regDoc, NdDemoIsolationContext ctx) =>
        NdDemoDataFilters.CanDemoMutateRegulationDocument(regDoc, ctx, demoOptions.Value);

    public static bool IsRegulationDemoJobRunning(Guid regDocId) =>
        RunningRegulationExtractJobs.ContainsKey(regDocId)
        || RunningRegulationParseJobs.ContainsKey(regDocId);

    private static readonly TimeSpan StaleDemoParseRequeueAfter = TimeSpan.FromSeconds(45);

    /// <summary>
    /// GET/poll heal: if a demo doc is stuck at the initial parse stamp with no in-memory job, re-queue once.
    /// </summary>
    public bool TryRecoverStaleRegulationParse(
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx)
    {
        if (!CanMutateRegulationDocument(regDoc, demoCtx))
            return false;
        if (!string.Equals(regDoc.ExtractionStatus, "processing", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsRegulationDemoJobRunning(regDoc.Id))
            return false;
        if (DateTimeOffset.UtcNow - regDoc.UpdatedAt < StaleDemoParseRequeueAfter)
            return false;

        var label = regDoc.ExtractionProgressLabel ?? "";
        var looksLikeParse = label.Contains("Parsing", StringComparison.OrdinalIgnoreCase)
            || label.Contains("markdown", StringComparison.OrdinalIgnoreCase)
            || (regDoc.ExtractionProgressPct is null or <= 15);
        if (!looksLikeParse)
            return false;

        logger.LogWarning(
            "Re-queueing stale demo regulation parse for {DocId} (last update {UpdatedAt}, label {Label})",
            regDoc.Id,
            regDoc.UpdatedAt,
            label);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var row = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDoc.Id);
                if (row != null)
                    await demo.CompleteDemoRegulationParseAsync(innerDb, row, userId, demoCtx, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stale demo regulation parse heal failed for {DocId}", regDoc.Id);
            }
        });
        return true;
    }

    private static int DemoDelayMs(int minMs, int maxMs) => Rng.Next(minMs, maxMs);

    /// <summary>Visible demo steps — brief but noticeable (~6–10s total).</summary>
    private static int DemoStepDelayMs() => Rng.Next(350, 550);

    private static readonly (string Label, int Pct)[] DemoParseProgressSteps =
    [
        ("Reading document…", 20),
        ("Building markdown…", 55),
        ("Saving parse results…", 88),
    ];

    private static readonly (string Label, int Pct)[] DemoExtractProgressSteps =
    [
        ("Analyzing document…", 25),
        ("Extracting clauses…", 60),
        ("Saving sections…", 92),
    ];

    private static readonly (string Label, int Pct)[] DemoRegulationExtractProgressSteps =
    [
        ("Analyzing regulation…", 22),
        ("Extracting clauses…", 58),
        ("Saving regulation points…", 92),
    ];

    private static int DemoDelayMs(int minMs, int maxMs, int totalPoints)
    {
        if (totalPoints >= 200) return Rng.Next(2, 6);
        if (totalPoints >= 80) return Rng.Next(4, 10);
        return DemoDelayMs(minMs, maxMs);
    }

    /// <summary>Delays for demo Regul forward simulation (~55 clauses) so the UI can show queued/running/done.</summary>
    private static int DemoRegulForwardDelayMs(int minMs, int maxMs, int totalPoints)
    {
        if (totalPoints >= 200) return Rng.Next(2, 6);
        if (totalPoints >= 80) return Rng.Next(4, 10);
        return Rng.Next(minMs, maxMs);
    }

    public bool TryQueueInternalParse(Guid docId, Guid userId)
    {
        if (!RunningInternalParseJobs.TryAdd(docId, 0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var doc = await innerDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId);
                if (doc != null)
                    await demo.SimulateInternalParseAsync(innerDb, doc, userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo internal parse failed for {DocId}", docId);
                await MarkInternalParseFailedAsync(docId, ex.Message);
            }
            finally
            {
                RunningInternalParseJobs.TryRemove(docId, out _);
            }
        });
        return true;
    }

    public bool TryQueueInternalSectionExtract(Guid docId, Guid userId, bool force)
    {
        if (!RunningInternalExtractJobs.TryAdd(docId, 0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var doc = await innerDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId);
                if (doc != null)
                    await demo.SimulateSectionExtractAsync(innerDb, doc, userId, force, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo internal section extract failed for {DocId}", docId);
                await MarkInternalSectionExtractFailedAsync(docId, ex.Message);
            }
            finally
            {
                RunningInternalExtractJobs.TryRemove(docId, out _);
            }
        });
        return true;
    }

    public bool TryQueueRegulationParse(Guid regDocId, Guid userId, bool forceIfStale = false)
    {
        if (!RunningRegulationParseJobs.TryAdd(regDocId, 0))
        {
            if (!forceIfStale || IsRegulationDemoJobRunning(regDocId))
                return false;
            RunningRegulationParseJobs.TryRemove(regDocId, out _);
            if (!RunningRegulationParseJobs.TryAdd(regDocId, 0))
                return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId);
                if (regDoc == null)
                {
                    await MarkRegulationJobFailedAsync(regDocId, "Regulation document not found.");
                    return;
                }
                await demo.SimulateRegulationParseAsync(innerDb, regDoc, userId, CancellationToken.None);
                dashboardCache.Invalidate();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo regulation parse failed for {DocId}", regDocId);
                await MarkRegulationJobFailedAsync(regDocId, ex.Message);
            }
            finally
            {
                RunningRegulationParseJobs.TryRemove(regDocId, out _);
            }
        });
        return true;
    }

    public bool TryQueueRegulationExtract(Guid regDocId, Guid userId)
    {
        if (!RunningRegulationExtractJobs.TryAdd(regDocId, 0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId);
                if (regDoc != null)
                    await demo.SimulateRegulationExtractAsync(innerDb, regDoc, userId, CancellationToken.None);
                dashboardCache.Invalidate();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo regulation extract failed for {DocId}", regDocId);
                await MarkRegulationJobFailedAsync(regDocId, ex.Message);
            }
            finally
            {
                RunningRegulationExtractJobs.TryRemove(regDocId, out _);
            }
        });
        return true;
    }

    /// <summary>Background parse then extract — demo Extract must not block the HTTP request.</summary>
    public bool TryQueueRegulationParseAndExtract(Guid regDocId, Guid userId)
    {
        if (!RunningRegulationExtractJobs.TryAdd(regDocId, 0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var demo = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId);
                if (regDoc == null) return;

                var demoCtx = await demo.ResolveDemoContextForProfileAsync(userId, CancellationToken.None);
                var status = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
                if (status is not "parsed" and not "completed")
                {
                    RunningRegulationParseJobs.TryAdd(regDocId, 0);
                    try
                    {
                        await demo.SimulateRegulationParseAsync(
                            innerDb, regDoc, userId, demoCtx, CancellationToken.None);
                    }
                    finally
                    {
                        RunningRegulationParseJobs.TryRemove(regDocId, out _);
                    }

                    regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId);
                    if (regDoc == null) return;
                }

                await demo.SimulateRegulationExtractAsync(innerDb, regDoc, userId, demoCtx, CancellationToken.None);
                dashboardCache.Invalidate();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo regulation parse+extract failed for {DocId}", regDocId);
                await MarkRegulationJobFailedAsync(regDocId, ex.Message);
            }
            finally
            {
                RunningRegulationExtractJobs.TryRemove(regDocId, out _);
            }
        });
        return true;
    }

    public async Task SimulateInternalParseAsync(
        AppDbContext dbCtx,
        StoredDocument doc,
        Guid parsedBy,
        CancellationToken ct)
    {
        doc.ParseStatus = "processing";
        doc.ParseError = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await dbCtx.SaveChangesAsync(ct);

        foreach (var (_, _) in DemoParseProgressSteps)
            await Task.Delay(DemoStepDelayMs(), ct);

        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        var fileName = NormalizeFileName(doc.OriginalFileName, doc.Title, doc.StoragePath);

        var source = await ResolveInternalTemplateSourceAsync(dbCtx, demoIds, fileName, doc.Title, ct)
            ?? throw new InvalidOperationException(
                "No template document available for demo clone. Upload Internal AML Manual 290626 or I M P T F S.pdf.");

        await CopyParseCacheAsync(source, doc, ct);

        if (source.Pages is > 0)
            doc.Pages = source.Pages;
        else if (IsAmlManualSeedDocument(fileName, doc.Title))
            doc.Pages = NdDemoInternalAmlManualSeed.TotalPages;

        doc.ParseStatus = "parsed";
        doc.FileHash = source.FileHash ?? doc.FileHash;
        doc.ParseError = null;
        doc.ParsedAt ??= DateTimeOffset.UtcNow;
        doc.ParsedBy = parsedBy;
        doc.SectionExtractStatus ??= "pending";
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await dbCtx.SaveChangesAsync(ct);

        logger.LogInformation(
            "Demo parse cloned from {SourceId} to {DestId} ({File})",
            source.Id,
            doc.Id,
            fileName);
    }

    private async Task<NdDemoIsolationContext> ResolveDemoContextForProfileAsync(
        Guid profileId,
        CancellationToken ct) =>
        await directory.ResolveContextAsync(new JwtUser(profileId, null), ct);

    public async Task SimulateRegulationParseAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        CancellationToken ct)
    {
        var demoCtx = await ResolveDemoContextForProfileAsync(userId, ct);
        await SimulateRegulationParseAsync(dbCtx, regDoc, userId, demoCtx, ct);
    }

    public Task SimulateRegulationParseAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct) =>
        CompleteDemoRegulationParseAsync(dbCtx, regDoc, userId, demoCtx, ct);

    /// <summary>
    /// Demo parse: count real PDF pages from the uploaded file and mark parsed — no Landing AI, no cache clone, no fake progress.
    /// </summary>
    public async Task CompleteDemoRegulationParseAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct)
    {
        if (!CanMutateRegulationDocument(regDoc, demoCtx))
            throw new InvalidOperationException("Parse could not run for this document.");

        var sem = RegulationSimulateLocks.GetOrAdd(regDoc.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        RunningRegulationParseJobs.TryAdd(regDoc.Id, 0);
        try
        {
            await dbCtx.Entry(regDoc).ReloadAsync(ct);
            var status = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
            if (status is "parsed" or "completed")
                return;

            var pages = 0;
            if (regDoc.StoredDocumentId is Guid storedId)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var upload = scope.ServiceProvider.GetRequiredService<NdRegulationUploadService>();
                pages = await upload.SyncStoredPdfPageCountAsync(regDoc.Id, ct);

                var stored = await dbCtx.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct);
                if (stored != null)
                {
                    stored.ParseStatus = "parsed";
                    stored.ParseError = null;
                    stored.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            regDoc.ExtractionStatus = "parsed";
            regDoc.ExtractionProgressLabel = null;
            regDoc.ExtractionProgressPct = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);

            logger.LogInformation(
                "Demo regulation parse complete for {DocId} ({Pages} pages)",
                regDoc.Id,
                pages);
        }
        finally
        {
            RunningRegulationParseJobs.TryRemove(regDoc.Id, out _);
            sem.Release();
        }
    }

    /// <summary>Seed demo regulation points when empty — no processing status flicker (points recovery).</summary>
    public async Task<int> TryEnsureRegulationPointsSeededAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct)
    {
        // Only touch demo-owned regulation rows (never production template / production extracts).
        if (!NdDemoDataFilters.IsDemoOwned(regDoc.CreatedBy, demoCtx))
        {
            return await CountCanonicalActivePointsAsync(dbCtx, regDoc.Id, ct);
        }

        var sem = RegulationSimulateLocks.GetOrAdd(regDoc.Id, _ => new SemaphoreSlim(1, 1));
        if (!await sem.WaitAsync(0, ct))
        {
            return await CountCanonicalActivePointsAsync(dbCtx, regDoc.Id, ct);
        }

        try
        {
            var activeCount = await CountCanonicalActivePointsAsync(dbCtx, regDoc.Id, ct);

            // View/list must stay fast: never wipe+reclone here. Bloated CBUAE counts are fixed on Extract only.
            if (activeCount > 0)
                return activeCount;

            var status = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
            if (status is "processing")
                return 0;

            if (status is "pending" or "failed")
            {
                await SimulateRegulationParseAsync(dbCtx, regDoc, userId, demoCtx, ct);
                await dbCtx.Entry(regDoc).ReloadAsync(ct);
                status = (regDoc.ExtractionStatus ?? "").Trim().ToLowerInvariant();
            }

            if (status is not ("parsed" or "completed"))
                return 0;

            var populated = await PopulateDemoRegulationPointsAsync(dbCtx, regDoc, ct);
            if (populated == 0)
                return 0;

            regDoc.ExtractionStatus = "completed";
            regDoc.ExtractionProgressLabel = null;
            regDoc.ExtractionProgressPct = null;
            regDoc.ExtractedAt ??= DateTimeOffset.UtcNow;
            regDoc.ExtractedBy = userId;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);
            return populated;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<NdInternalDocumentSection>> SimulateSectionExtractAsync(
        AppDbContext dbCtx,
        StoredDocument doc,
        Guid extractedBy,
        bool force,
        CancellationToken ct)
    {
        if (!force && string.Equals(doc.SectionExtractStatus, "extracted", StringComparison.OrdinalIgnoreCase))
        {
            return await dbCtx.NdInternalDocumentSections
                .Where(s => s.StoredDocumentId == doc.Id)
                .OrderBy(s => s.DisplayOrder)
                .ToListAsync(ct);
        }

        doc.SectionExtractStatus = "processing";
        doc.SectionExtractError = null;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await dbCtx.SaveChangesAsync(ct);

        foreach (var (label, pct) in DemoExtractProgressSteps)
        {
            doc.SectionExtractProgressLabel = label;
            doc.SectionExtractProgressPct = pct;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);
            await Task.Delay(DemoStepDelayMs(), ct);
        }

        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        var fileName = NormalizeFileName(doc.OriginalFileName, doc.Title, doc.StoragePath);

        var existing = await dbCtx.NdInternalDocumentSections
            .Where(s => s.StoredDocumentId == doc.Id)
            .ToListAsync(ct);
        dbCtx.NdInternalDocumentSections.RemoveRange(existing);

        int sectionCount;
        if (IsAmlManualSeedDocument(fileName, doc.Title))
        {
            sectionCount = ApplyAmlManualSeedSections(dbCtx, doc.Id);
            if (doc.Pages <= 0)
                doc.Pages = NdDemoInternalAmlManualSeed.TotalPages;
        }
        else
        {
            var source = await ResolveInternalTemplateSourceAsync(dbCtx, demoIds, fileName, doc.Title, ct)
                ?? throw new InvalidOperationException(
                    "No template section extract available. Upload Internal AML Manual 290626 or I M P T F S.pdf.");

            var sourceSections = await dbCtx.NdInternalDocumentSections.AsNoTracking()
                .Where(s => s.StoredDocumentId == source.Id)
                .OrderBy(s => s.DisplayOrder)
                .ToListAsync(ct);
            if (sourceSections.Count == 0)
                throw new InvalidOperationException("Template document has no extracted sections.");

            foreach (var section in sourceSections)
            {
                dbCtx.NdInternalDocumentSections.Add(new NdInternalDocumentSection
                {
                    StoredDocumentId = doc.Id,
                    SectionRef = section.SectionRef,
                    SectionText = section.SectionText,
                    SourcePage = section.SourcePage,
                    DisplayOrder = section.DisplayOrder,
                });
            }

            sectionCount = sourceSections.Count;
        }

        doc.SectionExtractStatus = "extracted";
        doc.SectionCount = sectionCount;
        doc.SectionExtractError = null;
        doc.SectionExtractProgressLabel = null;
        doc.SectionExtractProgressPct = null;
        doc.SectionExtractedAt = DateTimeOffset.UtcNow;
        doc.SectionExtractedBy = extractedBy;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await dbCtx.SaveChangesAsync(ct);

        return await dbCtx.NdInternalDocumentSections
            .Where(s => s.StoredDocumentId == doc.Id)
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync(ct);
    }

    public async Task SimulateRegulationExtractAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        CancellationToken ct)
    {
        var demoCtx = await ResolveDemoContextForProfileAsync(userId, ct);
        await SimulateRegulationExtractAsync(dbCtx, regDoc, userId, demoCtx, ct);
    }

    public Task SimulateRegulationExtractAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct) =>
        CompleteDemoRegulationExtractAsync(dbCtx, regDoc, userId, demoCtx, ct);

    /// <summary>
    /// Demo extract: clone canonical points from the configured admin demo template — no Landing AI, no progress theatre.
    /// </summary>
    public async Task CompleteDemoRegulationExtractAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        Guid userId,
        NdDemoIsolationContext demoCtx,
        CancellationToken ct)
    {
        if (!CanMutateRegulationDocument(regDoc, demoCtx))
            throw new InvalidOperationException("Extract could not run for this document.");

        var sem = RegulationSimulateLocks.GetOrAdd(regDoc.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        RunningRegulationExtractJobs.TryAdd(regDoc.Id, 0);
        try
        {
            await dbCtx.Entry(regDoc).ReloadAsync(ct);

            var existingCount = await CountCanonicalActivePointsAsync(dbCtx, regDoc.Id, ct);
            var alreadyCompleted = string.Equals(regDoc.ExtractionStatus, "completed", StringComparison.OrdinalIgnoreCase);
            if (existingCount > 0 && alreadyCompleted)
                return;

            regDoc.ExtractionParseChunkCompleted = null;
            await MarkExistingRegulationPointsRemovedAsync(dbCtx, regDoc.Id, ct);
            await dbCtx.SaveChangesAsync(ct);

            var (populated, source) = await PopulateDemoRegulationPointsWithSourceAsync(dbCtx, regDoc, ct);
            if (populated == 0)
            {
                var templateId = demoOptions.Value.DemoRegulationTemplateDocumentId;
                throw new InvalidOperationException(
                    "No demo regulation points to clone. " +
                    $"Check admin demo template {templateId} has points, then retry Extract.");
            }

            regDoc.ExtractionResult = null;
            regDoc.ExtractionMarkdown = null;
            regDoc.ExtractionStatus = "completed";
            regDoc.ExtractionProgressLabel = null;
            regDoc.ExtractionProgressPct = null;
            regDoc.ExtractedAt = DateTimeOffset.UtcNow;
            regDoc.ExtractedBy = userId;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);

            var savedCount = await CountCanonicalActivePointsAsync(dbCtx, regDoc.Id, ct);
            if (savedCount == 0)
                throw new InvalidOperationException("Demo extract finished without saving regulation points.");

            logger.LogInformation(
                "Demo regulation extract saved {Count} points for {DestId} (source {SourceId})",
                savedCount,
                regDoc.Id,
                source?.Id);
        }
        finally
        {
            RunningRegulationExtractJobs.TryRemove(regDoc.Id, out _);
            sem.Release();
        }
    }

    private static async Task MarkExistingRegulationPointsRemovedAsync(
        AppDbContext dbCtx,
        Guid regulationDocumentId,
        CancellationToken ct)
    {
        var existingPoints = await dbCtx.NdRegulationPoints
            .IgnoreQueryFilters()
            .Where(p => p.RegulationDocumentId == regulationDocumentId && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);
        foreach (var existing in existingPoints)
            existing.Status = NdRegulationPointStatus.Removed;
    }

    private async Task<int> PopulateDemoRegulationPointsAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        CancellationToken ct)
    {
        var (count, _) = await PopulateDemoRegulationPointsWithSourceAsync(dbCtx, regDoc, ct);
        return count;
    }

    /// <summary>
    /// Demo clone: CBUAE always copies canonical points from the configured production template
    /// (id or stored-document id), then falls back to the best production CBUAE extract (~397).
    /// </summary>
    private async Task<(int Count, NdRegulationDocument? Source)> PopulateDemoRegulationPointsWithSourceAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        CancellationToken ct)
    {
        var fileName = await ResolveRegulationFileNameAsync(dbCtx, regDoc, ct);
        var displayName = regDoc.Name;

        if (IsCbuaeDemoRegulation(displayName, fileName))
        {
            // CBUAE demo: ONLY the configured production template (never name-match bloated extracts).
            NdRegulationDocument? source = null;
            var templateId = demoOptions.Value.DemoRegulationTemplateDocumentId;
            if (templateId != Guid.Empty)
            {
                source = await LoadRegulationTemplateDocumentAsync(
                    dbCtx, templateId, regDoc.Id, minPoints: 1, ct);
                if (source != null)
                {
                    var templateCount = await CountCanonicalActivePointsAsync(dbCtx, source.Id, ct);
                    if (!IsExpectedCbuaePointCount(templateCount))
                    {
                        logger.LogWarning(
                            "Demo CBUAE template {TemplateId} (doc {DocId}) has {Count} canonical points (expected ~{Expected}); trying production fallback",
                            templateId,
                            source.Id,
                            templateCount,
                            ExpectedCbuaeRegulationPointCount);
                        var cbuaeFallback = await FindBestProductionCbuaeTemplateAsync(dbCtx, regDoc.Id, ct);
                        if (cbuaeFallback != null)
                            source = cbuaeFallback;
                    }
                }
            }

            if (source == null)
            {
                logger.LogWarning(
                    "Demo CBUAE clone failed for {DestId}: template {TemplateId} not found or has no points",
                    regDoc.Id,
                    templateId);
                return (0, null);
            }

            var cloned = await CloneCbuaeTemplateScopedPointsAsync(dbCtx, regDoc.Id, source, ct);
            if (cloned == 0)
            {
                logger.LogWarning(
                    "Demo CBUAE clone from {SourceId} produced 0 canonical points for {DestId}",
                    source.Id,
                    regDoc.Id);
            }
            else
            {
                logger.LogInformation(
                    "Demo CBUAE cloned {Count} canonical points from template {SourceId} → {DestId}",
                    cloned,
                    source.Id,
                    regDoc.Id);
            }

            return (cloned, source);
        }

        if (IsTfsDemoRegulation(displayName, fileName))
        {
            var templateId = demoOptions.Value.DemoTfsRegulationTemplateDocumentId;
            if (templateId != Guid.Empty)
            {
                var source = await LoadRegulationTemplateDocumentAsync(
                    dbCtx, templateId, regDoc.Id, minPoints: 1, ct);
                if (source != null)
                {
                    var cloned = await CloneRegulationPointsFromSourceAsync(dbCtx, regDoc.Id, source, ct);
                    return (cloned, source);
                }
            }
        }

        // Non-CBUAE/TFS demo regs: best-effort name match on production extracts only.
        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        var fallback = await FindSourceRegulationDocumentAsync(
            dbCtx, demoIds, fileName, destFileHash: null, regDoc, ct);
        if (fallback == null)
            return (0, null);

        var fallbackCloned = await CloneRegulationPointsFromSourceAsync(dbCtx, regDoc.Id, fallback, ct);
        return (fallbackCloned, fallback);
    }

    private async Task<int> PopulateDemoRegulationPointsAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        NdRegulationDocument? source,
        CancellationToken ct)
    {
        if (source != null)
        {
            var cloned = await CloneRegulationPointsFromSourceAsync(dbCtx, regDoc.Id, source, ct);
            if (cloned > 0)
                return cloned;
        }

        var (count, _) = await PopulateDemoRegulationPointsWithSourceAsync(dbCtx, regDoc, ct);
        return count;
    }

    private bool IsTrustedProductionCloneSource(NdRegulationDocument source, HashSet<Guid> demoIds) =>
        IsTrustedProductionCloneSource(
            source,
            demoIds,
            demoOptions.Value.DemoRegulationTemplateDocumentId,
            demoOptions.Value.DemoTfsRegulationTemplateDocumentId);

    private static bool IsTrustedProductionCloneSource(
        NdRegulationDocument source,
        HashSet<Guid> demoIds,
        Guid cbuaeTemplateId,
        Guid tfsTemplateId)
    {
        if (source.Id == cbuaeTemplateId || source.Id == tfsTemplateId)
            return true;

        return source.CreatedBy == null || !demoIds.Contains(source.CreatedBy.Value);
    }

    private static bool IsExpectedCbuaePointCount(int count) =>
        count >= ExpectedCbuaeRegulationPointCount - CbuaeRegulationPointCountTolerance
        && count <= ExpectedCbuaeRegulationPointCount + CbuaeRegulationPointCountTolerance;

    private static bool UsesFullRegulationExtractTemplate(string? displayName, string? fileName) =>
        IsCbuaeDemoRegulation(displayName, fileName);

    private static bool IsCbuaeDemoRegulation(string? displayName, string? fileName) =>
        DemoAnalysisSeedService.IsCbuaeRegulationName(displayName)
        || DemoAnalysisSeedService.IsCbuaeRegulationName(fileName);

    private static bool IsTfsDemoRegulation(string? displayName, string? fileName) =>
        DemoAnalysisSeedService.IsTfsRegulationName(displayName)
        || DemoAnalysisSeedService.IsTfsRegulationName(fileName);

    private static async Task<string> ResolveRegulationFileNameAsync(
        AppDbContext dbCtx,
        NdRegulationDocument regDoc,
        CancellationToken ct)
    {
        var fileName = regDoc.Name;
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await dbCtx.StoredDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == storedId, ct);
            if (stored != null)
                fileName = NormalizeFileName(stored.OriginalFileName, stored.Title, stored.StoragePath);
        }

        return fileName;
    }

    /// <summary>
    /// CBUAE demo clone. The demo admin template (Admin → Demo) decides which clauses exist,
    /// so the library, analysis and exports all report the same number. Clause text comes from
    /// the production extract where a matching clause exists, and falls back to the template's
    /// own interpretation text otherwise.
    /// </summary>
    private async Task<int> CloneCbuaeTemplateScopedPointsAsync(
        AppDbContext dbCtx,
        Guid destRegulationId,
        NdRegulationDocument source,
        CancellationToken ct)
    {
        var clauses = await demoWorkspace.LoadCbuaeTemplateClausesAsync(ct);
        if (clauses.Count == 0)
            return await CloneRegulationPointsFromSourceAsync(dbCtx, destRegulationId, source, ct);

        var sourcePoints = NdRegulationPointCanonicalFilter.FilterCanonical(
            await dbCtx.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == source.Id && p.Status == NdRegulationPointStatus.Active)
                .ToListAsync(ct));

        var byKey = new Dictionary<string, NdRegulationPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sourcePoints)
        {
            if (!string.IsNullOrWhiteSpace(p.PointNumber))
            {
                var numKey = DemoAnalysisSeedService.NormalizeClauseKey(p.PointNumber);
                if (numKey.Length > 0) byKey.TryAdd(numKey, p);
            }
            if (!string.IsNullOrWhiteSpace(p.PointTitle))
            {
                var titleKey = DemoAnalysisSeedService.NormalizeClauseKey(p.PointTitle);
                if (titleKey.Length > 0) byKey.TryAdd(titleKey, p);
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var clause in clauses)
        {
            if (string.IsNullOrWhiteSpace(clause.ClauseNo)) continue;
            var key = DemoAnalysisSeedService.NormalizeClauseKey(clause.ClauseNo);
            if (key.Length == 0 || !used.Add(key)) continue;

            var match = byKey.GetValueOrDefault(key);
            if (match == null && !string.IsNullOrWhiteSpace(clause.ClauseTitle))
                match = byKey.GetValueOrDefault(DemoAnalysisSeedService.NormalizeClauseKey(clause.ClauseTitle));
            if (match == null && TryGetParentClauseKey(key, out var parentKey))
                match = byKey.GetValueOrDefault(parentKey);

            var content = match?.PointContent;
            if (string.IsNullOrWhiteSpace(content))
                content = clause.Interpretation ?? clause.ClauseTitle ?? clause.ClauseNo ?? "";

            dbCtx.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = destRegulationId,
                PointNumber = clause.ClauseNo!.Trim(),
                PointTitle = string.IsNullOrWhiteSpace(clause.ClauseTitle) ? match?.PointTitle : clause.ClauseTitle,
                PointContent = content,
                PageReference = match?.PageReference,
                IsIntroductionPoint = match?.IsIntroductionPoint ?? false,
                IsAnnexPoint = match?.IsAnnexPoint ?? false,
                Status = NdRegulationPointStatus.Active,
            });
            added++;
        }

        logger.LogInformation(
            "Demo CBUAE template-scoped clone: {Added} of {Clauses} template clauses written for {DestId} (source {SourceId})",
            added,
            clauses.Count,
            destRegulationId,
            source.Id);

        return added;
    }

    /// <summary>
    /// Re-clone every demo-owned CBUAE regulation document after the demo admin edits the
    /// template, so the point count stays identical across library, analysis and exports.
    /// Returns the number of documents refreshed.
    /// </summary>
    public async Task<int> ResyncDemoCbuaeRegulationDocumentsAsync(CancellationToken ct = default)
    {
        if (!IsActive) return 0;

        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        if (demoIds.Count == 0) return 0;

        var docs = await db.NdRegulationDocuments
            .Where(d => !d.IsManual
                && d.CreatedBy != null
                && demoIds.Contains(d.CreatedBy.Value)
                && d.ExtractionStatus == "completed")
            .ToListAsync(ct);

        var refreshed = 0;
        foreach (var doc in docs)
        {
            var fileName = await ResolveRegulationFileNameAsync(db, doc, ct);
            if (!IsCbuaeDemoRegulation(doc.Name, fileName)) continue;

            var sem = RegulationSimulateLocks.GetOrAdd(doc.Id, _ => new SemaphoreSlim(1, 1));
            if (!await sem.WaitAsync(0, ct)) continue;
            try
            {
                await MarkExistingRegulationPointsRemovedAsync(db, doc.Id, ct);
                var (populated, _) = await PopulateDemoRegulationPointsWithSourceAsync(db, doc, ct);
                if (populated == 0)
                {
                    logger.LogWarning("Demo CBUAE resync produced 0 points for {DocId}; leaving as-is", doc.Id);
                    continue;
                }

                doc.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                refreshed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Demo CBUAE resync failed for {DocId}", doc.Id);
            }
            finally
            {
                sem.Release();
            }
        }

        return refreshed;
    }

    /// <summary>"3.2-a" → "3.2", so sub-clauses can inherit the parent clause text.</summary>
    private static bool TryGetParentClauseKey(string clauseKey, out string parentKey)
    {
        parentKey = "";
        var dash = clauseKey.LastIndexOf('-');
        if (dash <= 0 || dash >= clauseKey.Length - 1) return false;
        parentKey = clauseKey[..dash];
        return parentKey.Length > 0;
    }

    private static async Task<int> CloneRegulationPointsFromSourceAsync(
        AppDbContext dbCtx,
        Guid destRegulationId,
        NdRegulationDocument source,
        CancellationToken ct)
    {
        var sourcePoints = NdRegulationPointCanonicalFilter.FilterCanonical(
            await dbCtx.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == source.Id && p.Status == NdRegulationPointStatus.Active)
                .OrderBy(p => p.PointNumber)
                .ToListAsync(ct));

        foreach (var p in sourcePoints)
        {
            dbCtx.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = destRegulationId,
                PointNumber = p.PointNumber,
                PointTitle = p.PointTitle,
                PointContent = p.PointContent,
                PageReference = p.PageReference,
                IsIntroductionPoint = p.IsIntroductionPoint,
                IsAnnexPoint = p.IsAnnexPoint,
                Status = NdRegulationPointStatus.Active,
            });
        }

        return sourcePoints.Count;
    }

    private static async Task<Dictionary<Guid, int>> LoadActivePointCountsAsync(
        AppDbContext dbCtx,
        IReadOnlyList<Guid> regulationDocumentIds,
        CancellationToken ct)
    {
        if (regulationDocumentIds.Count == 0)
            return new Dictionary<Guid, int>();

        var rows = await dbCtx.NdRegulationPoints.AsNoTracking()
            .Where(p => regulationDocumentIds.Contains(p.RegulationDocumentId)
                && p.Status == NdRegulationPointStatus.Active)
            .ToListAsync(ct);

        return rows
            .GroupBy(p => p.RegulationDocumentId)
            .ToDictionary(
                g => g.Key,
                g => NdRegulationPointCanonicalFilter.CountCanonical(g.ToList()));
    }

    private static async Task<int> CountCanonicalActivePointsAsync(
        AppDbContext dbCtx,
        Guid regulationDocumentId,
        CancellationToken ct) =>
        await NdRegulationPointCanonicalFilter.CountCanonicalForDocumentAsync(
            dbCtx, regulationDocumentId, isManual: false, ct);

    private static readonly Regex VersionSuffixRegex =
        new(@"\s*\(v\d+\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string NormalizeRegulationMatchKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var baseName = Path.GetFileNameWithoutExtension(name.Trim());
        baseName = VersionSuffixRegex.Replace(baseName, string.Empty);
        return string.Concat(baseName.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    }

    private static HashSet<string> BuildRegulationMatchKeys(string? fileName, string? displayName)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { fileName, displayName })
        {
            var key = NormalizeRegulationMatchKey(raw);
            if (!string.IsNullOrEmpty(key))
                keys.Add(key);
        }
        return keys;
    }

    private static bool RegulationNamesMatch(IReadOnlySet<string> destKeys, string? fileName, string? displayName)
    {
        var candidateKeys = BuildRegulationMatchKeys(fileName, displayName);
        if (candidateKeys.Count == 0 || destKeys.Count == 0)
            return false;

        foreach (var dest in destKeys)
        {
            foreach (var cand in candidateKeys)
            {
                if (string.Equals(dest, cand, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (dest.Length >= 8 && cand.Length >= 8
                    && (dest.Contains(cand, StringComparison.OrdinalIgnoreCase)
                        || cand.Contains(dest, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    public async Task SimulateAnalysisRunAsync(Guid runId, Guid userId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        var sourceRun = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .Where(r => r.Status == "completed"
                && r.CreatedBy != null
                && !demoIds.Contains(r.CreatedBy.Value)
                && r.Points.Count > 0)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (sourceRun == null)
        {
            if (await demoSeed.IsCbuaeAmlDemoRunAsync(run, ct))
            {
                await SimulateRegulCbuaeRunAsync(runId, userId, ct);
                return;
            }

            logger.LogWarning(
                "No production analysis to clone for demo run {RunId}; completing without AI",
                runId);
            run.Status = "completed";
            run.ProcessedPointsCount = run.Points.Count;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        var sourcePointIds = sourceRun.Points.Select(p => p.Id).ToList();
        var sourceHistories = await db.NdActionPlanHistories.AsNoTracking()
            .Where(h => sourcePointIds.Contains(h.AnalysisPointId) && h.IsCurrent)
            .ToListAsync(ct);

        run.Status = "running";
        run.ProcessedPointsCount = 0;
        run.LandingAiCompletedCount = 0;
        run.DualVerifyCompletedCount = 0;
        run.DualVerifyFailedCount = 0;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var targetPoints = run.Points.OrderBy(p => p.CreatedAt).ToList();
        var sourcePoints = sourceRun.Points.OrderBy(p => p.CreatedAt).ToList();
        var total = targetPoints.Count;
        const int batchTicks = 6;

        for (var i = 0; i < targetPoints.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = targetPoints[i];
            var source = MatchSourcePoint(target, sourcePoints, i);
            if (source == null)
                continue;

            CopyAnalysisPointFields(target, source);

            var sourceHistory = sourceHistories.FirstOrDefault(h => h.AnalysisPointId == source.Id);
            if (sourceHistory != null)
            {
                var existingHistories = await db.NdActionPlanHistories
                    .Where(h => h.AnalysisPointId == target.Id)
                    .ToListAsync(ct);
                foreach (var h in existingHistories)
                    h.IsCurrent = false;

                db.NdActionPlanHistories.Add(new NdActionPlanHistory
                {
                    AnalysisPointId = target.Id,
                    VersionNumber = 1,
                    ActionPlanContent = sourceHistory.ActionPlanContent,
                    ChangeType = sourceHistory.ChangeType,
                    IsCurrent = true,
                    ChangedBy = userId,
                });
            }

            var tickBoundary = (total * (i + 1) + batchTicks - 1) / batchTicks;
            if (i + 1 == total || i + 1 >= tickBoundary)
            {
                run.ProcessedPointsCount = i + 1;
                run.LandingAiCompletedCount = targetPoints.Take(i + 1).Count(p =>
                    p.LandingAiStatus is "compliant" or "partial" or "non_compliant" or "passed" or "completed");
                run.DualVerifyCompletedCount = targetPoints.Take(i + 1).Count(p =>
                    p.DualVerifyStatus is "passed" or "completed");
                run.DualVerifyFailedCount = targetPoints.Take(i + 1).Count(p => p.DualVerifyStatus == "failed");
                run.UpdatedAt = DateTimeOffset.UtcNow;
                target.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await Task.Delay(DemoDelayMs(50, 120), ct);
            }
        }

        run.Status = "completed";
        run.ProcessedPointsCount = targetPoints.Count;
        run.LandingAiCompletedCount = targetPoints.Count(p =>
            p.LandingAiStatus is "compliant" or "partial" or "non_compliant" or "passed");
        run.DualVerifyCompletedCount = targetPoints.Count(p => p.DualVerifyStatus == "passed");
        run.DualVerifyFailedCount = targetPoints.Count(p => p.DualVerifyStatus == "failed");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Demo analysis cloned from {SourceId} to {DestId} ({Points} points)",
            sourceRun.Id,
            run.Id,
            targetPoints.Count);
    }

    /// <summary>
    /// Demo re-run of a gap against a freshly uploaded evidence document. No AI: the clause is
    /// re-judged one compliance step better and its policy extract / reference are rewritten to
    /// cite the uploaded file, so the demonstration mirrors what the real pipeline produces.
    /// </summary>
    /// <param name="pointId">Null to re-run every gap in the run (whole report demonstration).</param>
    /// <returns>Number of points updated.</returns>
    public async Task<int> SimulateEvidenceRerunAsync(
        Guid runId,
        Guid? pointId,
        string evidenceLabel,
        CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        var targets = run.Points
            .Where(p => pointId == null || p.Id == pointId.Value)
            .Where(p => pointId != null || p.FinalStatus is "non_compliant" or "partial_compliant")
            .OrderBy(p => p.CreatedAt)
            .ToList();
        if (targets.Count == 0)
            return 0;

        var regPointIds = targets.Where(p => p.RegulationPointId.HasValue)
            .Select(p => p.RegulationPointId!.Value).Distinct().ToList();
        var regPointsById = regPointIds.Count == 0
            ? new Dictionary<Guid, NdRegulationPoint>()
            : await db.NdRegulationPoints.AsNoTracking()
                .Where(p => regPointIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

        var label = string.IsNullOrWhiteSpace(evidenceLabel) ? "uploaded evidence document" : evidenceLabel.Trim();

        foreach (var point in targets)
        {
            ct.ThrowIfCancellationRequested();
            var (clauseNo, clauseText) = DemoAnalysisSeedService.ResolveClauseFromAnalysisPoint(point, regPointsById);
            var judgment = BuildEvidenceRerunJudgment(point, clauseNo, label);
            var message = NdRegulJudgmentFormatter.FormatLandingMessage(clauseNo, clauseText, judgment);
            NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, message);
            point.LandingAiRerunCount += 1;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await Task.Delay(DemoDelayMs(180, 340), ct);
        }

        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();
        return targets.Count;
    }

    /// <summary>One compliance step better, evidenced by the newly uploaded document.</summary>
    private static RegulJudgmentResult BuildEvidenceRerunJudgment(
        NdAnalysisPoint point,
        string clauseNo,
        string evidenceLabel)
    {
        var upgraded = point.FinalStatus switch
        {
            "non_compliant" => "partial",
            "partial_compliant" => "compliant",
            _ => "compliant",
        };
        var reference = $"{evidenceLabel} — clause {clauseNo}";
        var extract = upgraded == "compliant"
            ? $"The uploaded document \"{evidenceLabel}\" sets out the full procedure required by clause {clauseNo}, including ownership, frequency and escalation."
            : $"The uploaded document \"{evidenceLabel}\" partially addresses clause {clauseNo}: the control is described, but ownership and review frequency are still missing.";

        return new RegulJudgmentResult
        {
            OverallStatus = upgraded,
            DesignStatus = upgraded,
            Confidence = upgraded == "compliant" ? 0.94 : 0.62,
            DocumentReference = reference,
            PolicyExtract = [extract],
            GapDescription = upgraded == "compliant"
                ? ""
                : $"Clause {clauseNo} is now partially covered by the uploaded evidence; assign an owner and a review cycle to close it fully.",
            SuggestedAction = upgraded == "compliant"
                ? "N/A"
                : "Assign a named owner and a documented review frequency for this control, then re-upload the updated procedure.",
        };
    }

    public async Task SimulateDemoAnalysisRunAsync(
        Guid runId,
        Guid userId,
        bool useRegul,
        CancellationToken ct)
    {
        if (useRegul)
        {
            await SimulateRegulCbuaeRunAsync(runId, userId, ct);
            return;
        }

        await SimulateAnalysisRunAsync(runId, userId, ct);
    }

    /// <summary>Demo Regul forward run — applies saved seed in batches with brief queued/running/done UI ticks (no AI).</summary>
    public async Task SimulateRegulCbuaeRunAsync(Guid runId, Guid userId, CancellationToken ct)
    {
        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException("Analysis run not found.");

        run.Status = "running";
        run.RegulPipelinePhase = "queued";
        run.ProcessedPointsCount = 0;
        run.LandingAiCompletedCount = 0;
        run.RegulClausesConfirmedAt ??= DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        var points = run.Points.OrderBy(p => p.CreatedAt).ToList();
        var total = points.Count;
        foreach (var point in points)
        {
            point.LandingAiStatus = "pending";
            point.DualVerifyStatus = "pending";
            point.GoogleAiStatus = "pending";
            point.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await Task.Delay(DemoRegulForwardDelayMs(1200, 2000, total), ct);

        run.Status = "running";
        run.RegulPipelinePhase = "forward";
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await Task.Delay(DemoRegulForwardDelayMs(800, 1400, total), ct);

        var judgments = await demoWorkspace.LoadJudgmentsForRunAsync(run, ct);
        var judgmentMap = DemoAnalysisSeedService.BuildJudgmentLookup(judgments);
        var regPointIds = points.Where(p => p.RegulationPointId.HasValue).Select(p => p.RegulationPointId!.Value).Distinct().ToList();
        var regPointsById = regPointIds.Count == 0
            ? new Dictionary<Guid, NdRegulationPoint>()
            : await db.NdRegulationPoints.AsNoTracking()
                .Where(p => regPointIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

        var existingFindings = await db.NdRegulForwardFindings
            .Where(f => f.AnalysisRunId == run.Id)
            .ToDictionaryAsync(f => f.AnalysisPointId, ct);

        var batchSize = total >= 120 ? 6 : 5;
        var progressTicks = (total + batchSize - 1) / batchSize;
        var applied = 0;

        for (var tick = 0; tick < progressTicks; tick++)
        {
            ct.ThrowIfCancellationRequested();
            var start = tick * batchSize;
            var end = Math.Min(total, start + batchSize);
            if (end <= start) continue;

            for (var i = start; i < end; i++)
            {
                points[i].LandingAiStatus = "running";
                points[i].DualVerifyStatus = "running";
                points[i].UpdatedAt = DateTimeOffset.UtcNow;
            }
            run.ProcessedPointsCount = start;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await Task.Delay(DemoRegulForwardDelayMs(450, 750, total), ct);

            for (var i = start; i < end; i++)
            {
                var point = points[i];
                var row = DemoAnalysisSeedService.ResolveJudgmentForAnalysisPoint(point, judgmentMap, regPointsById);
                if (row != null)
                {
                    var (clauseNo, clauseText) = DemoAnalysisSeedService.ResolveClauseFromAnalysisPoint(point, regPointsById);
                    var judgment = row.ToJudgmentResult();
                    var landingMessage = NdRegulJudgmentFormatter.FormatLandingMessage(clauseNo, clauseText, judgment);
                    NdRegulAnalysisPointSync.ApplyForwardJudgment(point, judgment, landingMessage);
                    if (!string.IsNullOrWhiteSpace(point.LandingAiActionPlan))
                        point.OriginalAiActionPlan = point.LandingAiActionPlan;

                    if (!existingFindings.TryGetValue(point.Id, out var finding))
                    {
                        finding = new NdRegulForwardFinding
                        {
                            AnalysisRunId = run.Id,
                            AnalysisPointId = point.Id,
                            ClauseNo = clauseNo,
                            ClauseText = clauseText,
                            Status = "completed",
                            ResultJson = JsonSerializer.Serialize(judgment),
                        };
                        db.NdRegulForwardFindings.Add(finding);
                        existingFindings[point.Id] = finding;
                    }
                    else
                    {
                        finding.ClauseNo = clauseNo;
                        finding.ClauseText = clauseText;
                        finding.Status = "completed";
                        finding.ResultJson = JsonSerializer.Serialize(judgment);
                        finding.ErrorMessage = null;
                        finding.UpdatedAt = DateTimeOffset.UtcNow;
                    }

                    applied++;
                }
                else
                {
                    MarkDemoPointWithoutSeed(point);
                }
            }

            run.TotalPointsCount = total;
            run.LandingAiCompletedCount = applied;
            run.ProcessedPointsCount = end;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await Task.Delay(DemoRegulForwardDelayMs(300, 550, total), ct);
        }

        run.Status = "completed";
        run.RegulPipelinePhase = "done";
        run.ProcessedPointsCount = total;
        run.DualVerifyCompletedCount = points.Count(p => p.DualVerifyStatus == "completed");
        run.DualVerifyFailedCount = points.Count(p => p.DualVerifyStatus == "failed");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await demoSeed.SyncRegulDemoRunFromTemplateAsync(run.Id, userId, preserveWorkflowStatus: true, ct);
        dashboardCache.Invalidate();

        logger.LogInformation(
            "Demo Regul CBUAE simulation completed for run {RunId} ({Applied}/{Total} seeded points)",
            run.Id,
            applied,
            points.Count);
    }

    private static void MarkDemoPointWithoutSeed(NdAnalysisPoint point)
    {
        point.LandingAiStatus = "completed";
        point.LandingAiResult = JsonSerializer.Serialize(new
        {
            message = "No judgment result available for this clause.",
        });
        point.LandingAiError = null;
        point.LandingAiRunAt = DateTimeOffset.UtcNow;
        point.GoogleAiStatus = "skipped";
        point.GoogleAiResult = null;
        point.DualVerifyStatus = "completed";
        point.DualVerifyRunAt = DateTimeOffset.UtcNow;
        point.FinalStatus = "partial_compliant";
        point.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task CopyParseCacheAsync(StoredDocument source, StoredDocument dest, CancellationToken ct)
    {
        var parseModel = landingAiOptions.Value.ParseModel;
        var srcKey = source.ExtractionCacheKey ?? NdRegulationCacheKeys.ForStoredDocument(source.Id);
        var destKey = dest.ExtractionCacheKey ?? NdRegulationCacheKeys.ForStoredDocument(dest.Id);
        if (string.IsNullOrWhiteSpace(dest.ExtractionCacheKey))
            dest.ExtractionCacheKey = destKey;

        var cached = await cache.ResolveParseCacheAsync(srcKey, source.FileHash, parseModel, ct);
        if (string.IsNullOrWhiteSpace(cached?.Markdown))
            return;

        var fileName = NormalizeFileName(dest.OriginalFileName, dest.Title, dest.StoragePath);
        await cache.SaveParseCacheAsync(destKey, fileName, cached.Markdown, parseModel, ct);
    }

    private async Task<StoredDocument?> ResolveInternalTemplateSourceAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        string fileName,
        string? title,
        CancellationToken ct)
    {
        if (DemoAnalysisSeedService.IsImptfsInternalName(fileName)
            || DemoAnalysisSeedService.IsImptfsInternalName(title))
        {
            var imptfs = await FindStoredDocumentByFileHashAsync(
                dbCtx,
                demoOptions.Value.DemoImptfsInternalFileHash,
                ct);
            if (imptfs != null)
                return imptfs;
        }

        if (IsAmlManualDemoTemplateName(fileName, title))
        {
            var templateId = demoOptions.Value.DemoInternalTemplateDocumentId;
            if (templateId != Guid.Empty)
            {
                var byId = await dbCtx.StoredDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == templateId, ct);
                if (byId != null)
                    return byId;
            }
        }

        return await FindSourceStoredDocumentAsync(dbCtx, demoIds, fileName, ct)
            ?? await FindLatestSectionExtractedDocumentAsync(dbCtx, demoIds, ct)
            ?? await FindLatestParsedStoredDocumentAsync(dbCtx, demoIds, ct);
    }

    private static async Task<StoredDocument?> FindStoredDocumentByFileHashAsync(
        AppDbContext dbCtx,
        string? fileHash,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileHash))
            return null;

        var hash = fileHash.Trim();
        return await dbCtx.StoredDocuments.AsNoTracking()
            .Where(d => d.FileHash == hash)
            .OrderByDescending(d => d.SectionExtractedAt ?? d.ParsedAt ?? d.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static int ApplyAmlManualSeedSections(AppDbContext dbCtx, Guid storedDocumentId)
    {
        var clauses = NdDemoInternalAmlManualSeed.Sections;
        var order = 0;
        foreach (var clause in clauses)
        {
            dbCtx.NdInternalDocumentSections.Add(new NdInternalDocumentSection
            {
                StoredDocumentId = storedDocumentId,
                SectionRef = clause.ClauseNo,
                SectionText = clause.ClauseText,
                SourcePage = clause.SourcePage > 0 ? clause.SourcePage : null,
                DisplayOrder = order++,
            });
        }

        return clauses.Count;
    }

    private static bool IsAmlManualSeedDocument(string? fileName, string? title)
    {
        foreach (var raw in new[] { fileName, title })
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var normalized = DemoAnalysisSeedService.NormalizeDocNameForMatch(raw);
            if (normalized.Contains("290626", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("aml", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("manual", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsAmlManualDemoTemplateName(string? fileName, string? title)
    {
        foreach (var raw in new[] { fileName, title })
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var normalized = DemoAnalysisSeedService.NormalizeDocNameForMatch(raw);
            if (normalized.Contains("290626", StringComparison.OrdinalIgnoreCase))
                return true;

            if (normalized.Contains("aml", StringComparison.OrdinalIgnoreCase)
                && (normalized.Contains("manual", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("internal", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (normalized.Contains("internal", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("doc", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private Guid? ResolveDemoRegulationTemplateId(string? docName, string? fileName)
    {
        if (DemoAnalysisSeedService.IsCbuaeRegulationName(docName)
            || DemoAnalysisSeedService.IsCbuaeRegulationName(fileName))
        {
            var id = demoOptions.Value.DemoRegulationTemplateDocumentId;
            return id == Guid.Empty ? null : id;
        }

        if (DemoAnalysisSeedService.IsTfsRegulationName(docName)
            || DemoAnalysisSeedService.IsTfsRegulationName(fileName))
        {
            var id = demoOptions.Value.DemoTfsRegulationTemplateDocumentId;
            return id == Guid.Empty ? null : id;
        }

        return null;
    }

    private async Task<NdRegulationDocument?> LoadRegulationTemplateDocumentAsync(
        AppDbContext dbCtx,
        Guid templateId,
        Guid excludeId,
        int minPoints,
        CancellationToken ct)
    {
        if (templateId == Guid.Empty || templateId == excludeId)
            return null;

        // Config id may be either NdRegulationDocuments.id or StoredDocuments.id.
        var doc = await dbCtx.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == templateId, ct)
            ?? await dbCtx.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == templateId, ct);
        if (doc == null || doc.Id == excludeId)
            return null;

        var pts = NdRegulationPointCanonicalFilter.CountCanonical(
            await dbCtx.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == doc.Id && p.Status == NdRegulationPointStatus.Active)
                .ToListAsync(ct));
        if (pts < minPoints)
        {
            logger.LogWarning(
                "Demo regulation template {TemplateId} resolved to {DocId} but only has {Count} canonical points (need {Min})",
                templateId,
                doc.Id,
                pts,
                minPoints);
            return null;
        }

        return doc;
    }

    /// <summary>Best production CBUAE extract (non-demo) by canonical point count closest to ~397.</summary>
    private async Task<NdRegulationDocument?> FindBestProductionCbuaeTemplateAsync(
        AppDbContext dbCtx,
        Guid excludeId,
        CancellationToken ct)
    {
        var demoIds = await directory.GetDemoProfileIdsAsync(ct);
        // Narrow candidates first — do not load every regulation and every point set.
        var candidates = await dbCtx.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.Id != excludeId
                && !d.IsManual
                && d.Status == 1
                && (d.CreatedBy == null || !demoIds.Contains(d.CreatedBy.Value))
                && (d.Name.Contains("CBUAE") || d.Name.Contains("3945") || d.Name.Contains("cbuae")))
            .OrderByDescending(d => d.ExtractedAt ?? d.UpdatedAt)
            .Take(20)
            .ToListAsync(ct);

        NdRegulationDocument? best = null;
        var bestScore = int.MaxValue;
        var bestCount = 0;

        foreach (var candidate in candidates)
        {
            var storedName = candidate.Name;
            if (candidate.StoredDocumentId is Guid sid)
            {
                var stored = await dbCtx.StoredDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == sid, ct);
                if (stored != null)
                    storedName = NormalizeFileName(stored.OriginalFileName, stored.Title, stored.StoragePath);
            }

            if (!IsCbuaeDemoRegulation(candidate.Name, storedName))
                continue;

            var count = await CountCanonicalActivePointsAsync(dbCtx, candidate.Id, ct);
            if (count < MinDemoRegulationClonePoints)
                continue;
            // Only accept ~397±25 — never clone bloated extracts (~541/592).
            if (!IsExpectedCbuaePointCount(count))
                continue;

            var score = Math.Abs(count - ExpectedCbuaeRegulationPointCount);
            if (best == null || score < bestScore || (score == bestScore && count > bestCount))
            {
                best = candidate;
                bestScore = score;
                bestCount = count;
            }

            if (score == 0)
                break;
        }

        if (best != null)
        {
            logger.LogInformation(
                "Demo CBUAE clone fallback source {SourceId} with {Count} canonical points",
                best.Id,
                bestCount);
        }

        return best;
    }

    private async Task<StoredDocument?> ResolveRegulationParseStoredSourceAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        string fileName,
        NdRegulationDocument destRegDoc,
        StoredDocument destStored,
        CancellationToken ct)
    {
        var templateId = ResolveDemoRegulationTemplateId(destRegDoc.Name, fileName)
            ?? ResolveDemoRegulationTemplateId(destStored.Title, fileName);
        if (templateId is Guid regTemplateId)
        {
            var templateReg = await dbCtx.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == regTemplateId, ct);
            if (templateReg?.StoredDocumentId is Guid srcStoredId)
            {
                var srcStored = await dbCtx.StoredDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == srcStoredId, ct);
                if (srcStored != null)
                    return srcStored;
            }
        }

        return await FindSourceStoredDocumentAsync(dbCtx, demoIds, fileName, ct);
    }

    private async Task<NdRegulationDocument?> ResolveRegulationTemplateSourceAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        string fileName,
        string? destFileHash,
        NdRegulationDocument destDoc,
        CancellationToken ct)
    {
        var templateId = ResolveDemoRegulationTemplateId(destDoc.Name, fileName);
        if (templateId is Guid id)
        {
            var minPoints = UsesFullRegulationExtractTemplate(destDoc.Name, fileName) ? MinDemoRegulationClonePoints : 1;
            var byId = await LoadRegulationTemplateDocumentAsync(dbCtx, id, destDoc.Id, minPoints, ct);
            if (byId != null)
                return byId;
        }

        return await FindSourceRegulationDocumentAsync(
            dbCtx, demoIds, fileName, destFileHash, destDoc, ct);
    }

    private async Task MarkInternalParseFailedAsync(Guid docId, string message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var doc = await innerDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId);
            if (doc == null) return;
            doc.ParseStatus = "failed";
            doc.ParseError = string.IsNullOrWhiteSpace(message)
                ? "Demo parse failed. Retry parse."
                : message;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await innerDb.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mark demo internal parse failed for {DocId}", docId);
        }
    }

    private async Task MarkInternalSectionExtractFailedAsync(Guid docId, string message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var doc = await innerDb.StoredDocuments.FirstOrDefaultAsync(d => d.Id == docId);
            if (doc == null) return;
            doc.SectionExtractStatus = "failed";
            doc.SectionExtractError = string.IsNullOrWhiteSpace(message)
                ? "Demo section extract failed. Retry extract."
                : message;
            doc.SectionExtractProgressLabel = null;
            doc.SectionExtractProgressPct = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await innerDb.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mark demo internal extract failed for {DocId}", docId);
        }
    }

    private async Task UpdateRegulationProgressAsync(
        AppDbContext dbCtx,
        Guid regDocId,
        string label,
        int? pct,
        CancellationToken ct)
    {
        try
        {
            dbCtx.ChangeTracker.Clear();
            var row = await dbCtx.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId, ct);
            if (row == null) return;
            row.ExtractionStatus = "processing";
            row.ExtractionProgressLabel = label;
            row.ExtractionProgressPct = pct;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await dbCtx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Demo regulation progress update skipped for {DocId}", regDocId);
            dbCtx.ChangeTracker.Clear();
        }
    }

    private async Task MarkRegulationJobFailedAsync(Guid regDocId, string message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var innerDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var regDoc = await innerDb.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regDocId);
            if (regDoc == null) return;
            regDoc.ExtractionStatus = "failed";
            var friendly = string.IsNullOrWhiteSpace(message)
                ? "Processing failed. Try Parse or Extract again."
                : message.Contains("entity changes", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("violates check constraint", StringComparison.OrdinalIgnoreCase)
                    ? "Parse could not save results. Click Parse to try again."
                    : message.Length > 180 ? message[..180] + "…" : message;
            regDoc.ExtractionProgressLabel = friendly;
            regDoc.ExtractionProgressPct = null;
            regDoc.UpdatedAt = DateTimeOffset.UtcNow;
            await innerDb.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mark demo regulation job failed for {DocId}", regDocId);
        }
    }

    private async Task<StoredDocument?> FindSourceStoredDocumentAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        string fileName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var candidates = await dbCtx.StoredDocuments.AsNoTracking()
            .Where(d => d.ParseStatus == "parsed"
                && (d.UploadedBy == null || !demoIds.Contains(d.UploadedBy.Value)))
            .OrderByDescending(d => d.ParsedAt ?? d.UpdatedAt)
            .ToListAsync(ct);

        return candidates.FirstOrDefault(d =>
            RegulationNamesMatch(
                BuildRegulationMatchKeys(fileName, null),
                NormalizeFileName(d.OriginalFileName, d.Title, d.StoragePath),
                d.Title));
    }

    private static async Task<StoredDocument?> FindLatestParsedStoredDocumentAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        CancellationToken ct) =>
        await dbCtx.StoredDocuments.AsNoTracking()
            .Where(d => d.ParseStatus == "parsed"
                && (d.UploadedBy == null || !demoIds.Contains(d.UploadedBy.Value)))
            .OrderByDescending(d => d.ParsedAt ?? d.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    private static async Task<StoredDocument?> FindLatestSectionExtractedDocumentAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        CancellationToken ct) =>
        await dbCtx.StoredDocuments.AsNoTracking()
            .Where(d => d.SectionExtractStatus == "extracted"
                && (d.UploadedBy == null || !demoIds.Contains(d.UploadedBy.Value)))
            .OrderByDescending(d => d.SectionExtractedAt ?? d.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    private static async Task<NdRegulationDocument?> FindSourceRegulationDocumentAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        string fileName,
        string? destFileHash,
        NdRegulationDocument destDoc,
        CancellationToken ct)
    {
        var destKeys = BuildRegulationMatchKeys(fileName, destDoc.Name);
        if (destKeys.Count == 0 && string.IsNullOrWhiteSpace(destFileHash))
            return null;

        var candidates = await dbCtx.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.Id != destDoc.Id && !d.IsManual)
            .OrderByDescending(d => d.ExtractedAt ?? d.UpdatedAt)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return null;

        var pointCounts = await LoadActivePointCountsAsync(
            dbCtx,
            candidates.Select(c => c.Id).ToList(),
            ct);

        var storedIds = candidates
            .Where(c => c.StoredDocumentId.HasValue)
            .Select(c => c.StoredDocumentId!.Value)
            .Distinct()
            .ToList();
        var storedById = storedIds.Count == 0
            ? new Dictionary<Guid, StoredDocument>()
            : await dbCtx.StoredDocuments.AsNoTracking()
                .Where(s => storedIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);

        NdRegulationDocument? bestProduction = null;
        var bestProductionPoints = 0;
        NdRegulationDocument? bestDemo = null;
        var bestDemoPoints = 0;
        NdRegulationDocument? bestHash = null;
        var bestHashPoints = 0;

        foreach (var candidate in candidates)
        {
            var pts = pointCounts.GetValueOrDefault(candidate.Id);
            if (pts <= 0)
                continue;

            storedById.TryGetValue(candidate.StoredDocumentId ?? Guid.Empty, out var stored);
            var candFile = stored != null
                ? NormalizeFileName(stored.OriginalFileName, stored.Title, stored.StoragePath)
                : null;

            var isDemoOwned = candidate.CreatedBy is Guid owner && demoIds.Contains(owner);
            var nameMatch = RegulationNamesMatch(destKeys, candFile, candidate.Name);
            var hashMatch = !string.IsNullOrWhiteSpace(destFileHash)
                && stored != null
                && !string.IsNullOrWhiteSpace(stored.FileHash)
                && string.Equals(destFileHash, stored.FileHash, StringComparison.OrdinalIgnoreCase);

            if (hashMatch && pts >= MinDemoRegulationClonePoints && pts > bestHashPoints)
            {
                bestHashPoints = pts;
                bestHash = candidate;
            }

            if (!nameMatch)
                continue;

            if (pts < MinDemoRegulationClonePoints)
                continue;

            if (!isDemoOwned && pts > bestProductionPoints)
            {
                bestProductionPoints = pts;
                bestProduction = candidate;
            }
            else if (isDemoOwned && pts > bestDemoPoints)
            {
                bestDemoPoints = pts;
                bestDemo = candidate;
            }
        }

        if (bestHash != null && bestHashPoints >= MinDemoRegulationClonePoints)
            return bestHash;

        return bestProduction ?? bestDemo;
    }

    private async Task<NdRegulationDocument?> FindLatestExtractedRegulationDocumentAsync(
        AppDbContext dbCtx,
        HashSet<Guid> demoIds,
        Guid excludeRegulationId,
        bool requireCbuaePointCount,
        CancellationToken ct)
    {
        var cbuaeTemplateId = demoOptions.Value.DemoRegulationTemplateDocumentId;
        var tfsTemplateId = demoOptions.Value.DemoTfsRegulationTemplateDocumentId;
        var candidates = await dbCtx.NdRegulationDocuments.AsNoTracking()
            .Where(d => d.Id != excludeRegulationId && !d.IsManual)
            .OrderByDescending(d => d.ExtractedAt ?? d.UpdatedAt)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return null;

        var pointCounts = await LoadActivePointCountsAsync(
            dbCtx,
            candidates.Select(c => c.Id).ToList(),
            ct);

        IEnumerable<NdRegulationDocument> FilterPool(IEnumerable<NdRegulationDocument> pool)
        {
            foreach (var c in pool)
            {
                var count = pointCounts.GetValueOrDefault(c.Id);
                if (count < MinDemoRegulationClonePoints)
                    continue;
                if (requireCbuaePointCount
                    && !IsExpectedCbuaePointCount(count)
                    && !IsTrustedProductionCloneSource(c, demoIds, cbuaeTemplateId, tfsTemplateId))
                    continue;
                yield return c;
            }
        }

        NdRegulationDocument? PickBest(IEnumerable<NdRegulationDocument> pool) =>
            FilterPool(pool)
                .OrderByDescending(c => pointCounts.GetValueOrDefault(c.Id))
                .ThenByDescending(c => c.ExtractedAt ?? c.UpdatedAt)
                .FirstOrDefault();

        var production = PickBest(candidates.Where(c =>
            c.CreatedBy == null || !demoIds.Contains(c.CreatedBy.Value)));
        if (production != null)
            return production;

        return PickBest(candidates);
    }

    private static NdAnalysisPoint? MatchSourcePoint(
        NdAnalysisPoint target,
        List<NdAnalysisPoint> sourcePoints,
        int index)
    {
        if (target.RegulationPointId is Guid regId)
        {
            var byReg = sourcePoints.FirstOrDefault(p => p.RegulationPointId == regId);
            if (byReg != null) return byReg;
        }

        return index < sourcePoints.Count ? sourcePoints[index] : sourcePoints.LastOrDefault();
    }

    private static void CopyAnalysisPointFields(NdAnalysisPoint target, NdAnalysisPoint source)
    {
        target.LandingAiStatus = source.LandingAiStatus;
        target.LandingAiResult = source.LandingAiResult;
        target.LandingAiActionPlan = source.LandingAiActionPlan;
        target.LandingAiRunAt = source.LandingAiRunAt ?? DateTimeOffset.UtcNow;
        target.LandingAiError = source.LandingAiError;
        target.GoogleAiStatus = source.GoogleAiStatus;
        target.GoogleAiResult = source.GoogleAiResult;
        target.GoogleAiRunAt = source.GoogleAiRunAt ?? DateTimeOffset.UtcNow;
        target.GoogleAiError = source.GoogleAiError;
        target.DualVerifyStatus = source.DualVerifyStatus;
        target.DualVerifyRunAt = source.DualVerifyRunAt ?? DateTimeOffset.UtcNow;
        target.FinalStatus = source.FinalStatus;
        target.FinalActionPlan = source.FinalActionPlan;
        target.OriginalAiActionPlan = source.OriginalAiActionPlan;
        target.LandingAiRerunCount = source.LandingAiRerunCount;
        target.DualVerifyRerunCount = source.DualVerifyRerunCount;
    }

    private static string NormalizeFileName(string? originalFileName, string? title, string? storagePath)
    {
        if (!string.IsNullOrWhiteSpace(originalFileName))
            return originalFileName.Trim();
        if (!string.IsNullOrWhiteSpace(title))
            return title.Trim();
        if (!string.IsNullOrWhiteSpace(storagePath))
            return Path.GetFileName(storagePath);
        return "";
    }
}
