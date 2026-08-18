using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/analysis-runs")]
public class AnalysisRunsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdAnalysisProcessor processor,
    DemoAnalysisSeedService demoSeed,
    IServiceScopeFactory scopeFactory,
    NdAnalysisRunCancellationTracker runCancellation,
    NdDashboardCacheService dashboardCache,
    NdDemoUserDirectory demoDirectory,
    NdDemoWorkspaceService demoWorkspace,
    NdDemoInterceptionService demoIntercept,
    ILogger<AnalysisRunsController> logger) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    public record CreateRunRequest(
        string Name,
        string? Description,
        Guid? LibraryId,
        Guid? DepartmentId,
        List<object> SelectedPointsSnapshot,
        List<string> SelectedInternalDocIds,
        List<string> SelectedRegulationDocIds,
        /// <summary>Optional prompt pack: v1 | v2 | v3. Analyse-v9 sends v3 (Regul.ai rules).</summary>
        string? ComparePromptVersion = null,
        /// <summary>bcp_landing (default), regul_pipeline (V3), or regul_pipeline_full (V4 full markdown).</summary>
        string? WorkflowEngine = null,
        bool EnableQualitative = false);

    public record ConfirmClausesClauseUpdate(
        Guid AnalysisPointId,
        string? PointNumber,
        string? PointTitle,
        string? PointContent);

    public record ConfirmClausesRequest(List<ConfirmClausesClauseUpdate>? Clauses);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] bool mineOnly = false,
        [FromQuery] bool deletedOnly = false,
        [FromQuery] bool ndOnly = false,
        [FromQuery] bool summaryOnly = false,
        [FromQuery] bool skipGapStats = false,
        [FromQuery] int? page = null,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        if (deletedOnly)
        {
            if (profile!.Role != "super_admin")
                return StatusCode(403, new { success = false, message = "Forbidden" });

            var deletedRuns = await NdDemoDataFilters.ApplyToAnalysisRuns(
                    db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status == DeletedStatus),
                    demoCtx)
                .OrderByDescending(r => r.DeletedAt ?? r.UpdatedAt)
                .Take(200)
                .SelectListColumns()
                .ToListAsync(ct);

            var deletedItems = deletedRuns.Select(r => NdLegacyDataQueries.MapNdRunSummary(r)).Cast<object>().ToList();
            deletedItems.AddRange(await LoadHiddenLegacyRunsAsync(ct));
            return Ok(new { success = true, data = deletedItems });
        }

        HashSet<Guid>? hiddenLegacySet = null;
        if (!ndOnly)
        {
            var hiddenLegacy = await db.NdHiddenLegacyRuns.AsNoTracking()
                .Select(h => h.LegacyId)
                .ToListAsync(ct);
            hiddenLegacySet = hiddenLegacy.ToHashSet();
        }

        var q = NdDemoDataFilters.ApplyToAnalysisRuns(
            db.NdAnalysisRuns.AsNoTracking().Where(r => r.Status != DeletedStatus),
            demoCtx);

        if (profile!.Role == "maker" || mineOnly)
            q = q.Where(r => r.CreatedBy == profile.Id);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        const int overviewPreviewLimit = 20;

        var demoProfileIds = demoCtx.Enabled
            ? await demoDirectory.GetDemoProfileIdsAsync(ct)
            : null;

        if (ndOnly && summaryOnly)
        {
            if (page is null or < 1)
            {
                // Overview first paint — skip heavy point aggregation by default.
                var overviewSkipGaps = skipGapStats || page is null;
                var cacheScope =
                    $"runs-summary:{profile!.Id}:{profile.Role}:{mineOnly}:{status ?? ""}:skipGap={overviewSkipGaps}";
                var summaries = await dashboardCache.GetOrCreateAsync(cacheScope, async innerCt =>
                {
                    var cachedRuns = await q
                        .OrderByDescending(r => r.CreatedAt)
                        .Take(overviewPreviewLimit)
                        .SelectListColumns()
                        .ToListAsync(innerCt);
                    return await NdRunEnrichmentHelper.MapSummariesLightAsync(
                        db, cachedRuns, innerCt, overviewSkipGaps, demoProfileIds);
                }, ct);
                return Ok(new { success = true, data = summaries });
            }

            var effectivePage = page.Value;
            var effectivePageSize = Math.Clamp(pageSize, 1, 100);
            var total = await q.CountAsync(ct);
            var pagedRuns = await q
                .OrderByDescending(r => r.CreatedAt)
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .SelectListColumns()
                .ToListAsync(ct);
            var pagedSummaries = await NdRunEnrichmentHelper.MapSummariesLightAsync(
                db, pagedRuns, ct, skipGapStats: false, demoProfileIds);
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)effectivePageSize);
            return Ok(new
            {
                success = true,
                data = pagedSummaries,
                pagination = new
                {
                    page = effectivePage,
                    pageSize = effectivePageSize,
                    total,
                    totalPages,
                },
            });
        }

        var listLimit = 100;
        var runs = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(listLimit)
            .SelectListColumns()
            .ToListAsync(ct);

        if (ndOnly)
        {
            var enriched = await NdRunEnrichmentHelper.EnrichRunsAsync(db, runs, ct);
            return Ok(new { success = true, data = enriched });
        }

        var items = runs.Select(r => NdLegacyDataQueries.MapNdRunSummary(r)).Cast<object>().ToList();

        if (!ndOnly)
        {
            var linkedDvIds = await db.DocumentAnalysisRuns.AsNoTracking()
                .Where(r => r.DualVerifySessionId != null)
                .Select(r => r.DualVerifySessionId!.Value)
                .ToListAsync(ct);
            var linkedSet = linkedDvIds.ToHashSet();

            var legacyRuns = await db.DocumentAnalysisRuns.AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync(ct);
            items.AddRange(legacyRuns
                .Where(r => hiddenLegacySet is null || !hiddenLegacySet.Contains(r.Id))
                .Select(NdLegacyDataQueries.MapLegacyAnalysisRun));

            var standaloneDv = await db.DualVerifySessions.AsNoTracking()
                .Where(s => !linkedSet.Contains(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .Take(50)
                .ToListAsync(ct);
            items.AddRange(standaloneDv
                .Where(s => hiddenLegacySet is null || !hiddenLegacySet.Contains(s.Id))
                .Select(NdLegacyDataQueries.MapLegacyDualVerifySession));
        }

        var sorted = items
            .OrderByDescending(i =>
            {
                var prop = i.GetType().GetProperty("createdAt");
                return prop?.GetValue(i) as DateTimeOffset? ?? DateTimeOffset.MinValue;
            })
            .Take(150)
            .ToList();

        return Ok(new { success = true, data = sorted });
    }

    /// <summary>Demo gap analysis from seeded 32-point compliance session — no AI/Kafka.</summary>
    [HttpPost("demo-from-seed")]
    public async Task<IActionResult> DemoFromSeed(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var run = await demoSeed.GetOrCreateDemoRunAsync(profile!.Id, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = run.Id,
                    pointCount = run.TotalPointsCount,
                    status = run.Status,
                },
                message = "Demo analysis run ready (seeded from database, no AI used).",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = $"Demo run failed: {ex.Message}" });
        }
    }

    /// <summary>Persist a completed demo run from the analyse UI (actual points analysed in session).</summary>
    [HttpPost("demo-save")]
    public async Task<IActionResult> DemoSave(
        [FromBody] DemoAnalysisSeedService.DemoSaveRequest body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var run = await demoSeed.CreateDemoRunFromSessionAsync(profile!.Id, body, ct);
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = run.Id,
                    pointCount = run.TotalPointsCount,
                    status = run.Status,
                    name = run.Name,
                },
                message = "Demo analysis saved to analysis runs.",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { success = false, message = $"Demo save failed: {detail}" });
        }
    }

    /// <summary>Persist a completed Regul V4 demo run from Arena / external judgment JSON.</summary>
    [HttpPost("demo-save-regul")]
    public async Task<IActionResult> DemoSaveRegul(
        [FromBody] DemoAnalysisSeedService.DemoRegulSaveRequest? body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var request = body ?? new DemoAnalysisSeedService.DemoRegulSaveRequest(UseSeedFile: true);
            var run = await demoSeed.CreateDemoRegulRunFromJudgmentsAsync(profile!.Id, request, ct);
            dashboardCache.Invalidate();
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = run.Id,
                    pointCount = run.TotalPointsCount,
                    status = run.Status,
                    name = run.Name,
                    workflowEngine = run.WorkflowEngine,
                },
                message = "Demo Regul analysis saved to analysis runs.",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { success = false, message = $"Demo Regul save failed: {detail}" });
        }
    }

    /// <summary>Load CBUAE × AML Manual Arena judgments from SeedData and create a demo Regul run.</summary>
    [HttpPost("demo-from-cbuae-seed")]
    public async Task<IActionResult> DemoFromCbuaeSeed(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        try
        {
            var run = await demoSeed.GetOrCreateDemoCbuaeRunAsync(profile!.Id, ct);
            dashboardCache.Invalidate();
            return Ok(new
            {
                success = true,
                data = new
                {
                    id = run.Id,
                    pointCount = run.TotalPointsCount,
                    status = run.Status,
                    name = run.Name,
                },
                message = "Demo CBUAE × AML Manual analysis run ready (seeded judgments).",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = $"Demo CBUAE seed failed: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRunRequest body, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var points = body.SelectedPointsSnapshot ?? [];
        string? storedPromptVersion = null;
        if (!string.IsNullOrWhiteSpace(body.ComparePromptVersion))
        {
            storedPromptVersion = ComparePromptVersionExtensions
                .ParseOrDefault(body.ComparePromptVersion, ComparePromptVersion.V2)
                .ToApiValue();
        }

        var run = new NdAnalysisRun
        {
            Name = body.Name.Trim(),
            Description = body.Description,
            ComparePromptVersion = storedPromptVersion,
            WorkflowEngine = AnalysisWorkflowEngine.ResolveForCreate(body.WorkflowEngine),
            EnableQualitative = body.EnableQualitative,
            LibraryId = body.LibraryId,
            DepartmentId = body.DepartmentId,
            SelectedPointsSnapshot = JsonSerializer.Serialize(points),
            SelectedInternalDocIds = JsonSerializer.Serialize(body.SelectedInternalDocIds ?? []),
            SelectedRegulationDocIds = JsonSerializer.Serialize(body.SelectedRegulationDocIds ?? []),
            TotalPointsCount = points.Count,
            Status = "draft",
            CreatedBy = profile!.Id,
        };

        if (demoCtx.ViewerIsDemo)
        {
            if (!run.Name.StartsWith("[Demo]", StringComparison.OrdinalIgnoreCase))
                run.Name = $"[Demo] {run.Name}".Trim();
            if (string.IsNullOrWhiteSpace(run.Description))
                run.Description = "Demonstration analysis run — simulated workflow, no AI credits.";
        }
        db.NdAnalysisRuns.Add(run);

        foreach (var pt in points)
        {
            var json = JsonSerializer.Serialize(pt);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? regPointId = null;
            if (root.TryGetProperty("regulationPointId", out var rpid) && Guid.TryParse(rpid.GetString(), out var g1))
                regPointId = g1;
            else if (root.TryGetProperty("pointId", out var pid) && Guid.TryParse(pid.GetString(), out var g2))
                regPointId = g2;

            db.NdAnalysisPoints.Add(new NdAnalysisPoint
            {
                AnalysisRunId = run.Id,
                RegulationPointId = regPointId,
                PointSnapshot = json,
            });
        }

        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();
        return Ok(new { success = true, data = new { id = run.Id } });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] bool lite, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        // Projected load — avoid Include(r => r.Points) which can stall under pool pressure.
        var run = await db.NdAnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (run.Status == DeletedStatus)
            return NotFound(new { success = false, message = "Not found" });

        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var history = lite
            ? []
            : await db.NdAnalysisStatusHistories.AsNoTracking()
                .Where(h => h.AnalysisRunId == id)
                .OrderBy(h => h.CreatedAt)
                .ToListAsync(ct);

        List<NdAnalysisPoint> points;
        if (lite)
        {
            points = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new NdAnalysisPoint
                {
                    Id = p.Id,
                    AnalysisRunId = p.AnalysisRunId,
                    RegulationPointId = p.RegulationPointId,
                    PointSnapshot = p.PointSnapshot,
                    LandingAiStatus = p.LandingAiStatus,
                    LandingAiError = p.LandingAiError,
                    GoogleAiStatus = p.GoogleAiStatus,
                    DualVerifyStatus = p.DualVerifyStatus,
                    FinalStatus = p.FinalStatus,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                })
                .ToListAsync(ct);
        }
        else
        {
            points = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct);
        }

        var createdByIsDemo = run.CreatedBy is Guid creatorId
            && await demoDirectory.IsDemoProfileAsync(creatorId, ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                run = NdRegulApiProjection.MapRunDetail(run, creator?.FullName, createdByIsDemo),
                points = lite
                    ? points.Select(p => NdRegulApiProjection.MapPointLite(p, run.WorkflowEngine)).ToList()
                    : points.Select(p => NdRegulApiProjection.MapPoint(p, run.WorkflowEngine)).ToList(),
                history,
            },
        });
    }

    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> Status(
        Guid id,
        [FromQuery] bool resume = false,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (run.Status == DeletedStatus)
            return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        if (await NdStaleRunRecovery.TryRecoverRunAsync(id, db, runCancellation, logger, ct))
        {
            run = await db.NdAnalysisRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run == null) return NotFound(new { success = false, message = "Not found" });
        }

        if (resume)
        {
            // One-shot open/resume payload: snapshots + AI text via projection (no Include / no PDF enrich).
            var resumePoints = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct);

            return Ok(new
            {
                success = true,
                data = NdRegulApiProjection.MapResumeResponse(run, resumePoints),
            });
        }

        // Poll payload: lightweight status; include INT snapshots + reverse section rows for Regul live UI.
        var litePoll = run.TotalPointsCount >= 150;
        List<NdAnalysisPoint> pointRows;
        if (litePoll)
        {
            pointRows = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new NdAnalysisPoint
                {
                    Id = p.Id,
                    RegulationPointId = p.RegulationPointId,
                    PointSnapshot = p.PointSnapshot,
                    LandingAiStatus = p.LandingAiStatus,
                    LandingAiResult = p.LandingAiResult,
                    LandingAiError = p.LandingAiError,
                    GoogleAiStatus = p.GoogleAiStatus,
                    DualVerifyStatus = p.DualVerifyStatus,
                    FinalStatus = p.FinalStatus,
                    CreatedAt = p.CreatedAt,
                })
                .ToListAsync(ct);
        }
        else
        {
            pointRows = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct);
        }

        int? regulReverseSectionTotal = null;
        int? regulReverseSectionCompleted = null;
        int? regulReverseSectionFailed = null;
        List<object>? regulReverseSections = null;
        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
        {
            var sectionRows = await db.NdRegulInternalSections
                .AsNoTracking()
                .Where(s => s.AnalysisRunId == id)
                .Select(s => new { s.Id, s.SectionRef, s.SectionText })
                .ToListAsync(ct);

            var sections = PointNumberSort.OrderByPointNumber(sectionRows, s => s.SectionRef).ToList();

            regulReverseSectionTotal = sections.Count;

            var mappingStatuses = await db.NdRegulReverseMappings
                .AsNoTracking()
                .Where(m => m.AnalysisRunId == id)
                .Select(m => new { m.InternalSectionId, m.Status })
                .ToListAsync(ct);
            var statusBySectionId = mappingStatuses
                .GroupBy(m => m.InternalSectionId)
                .ToDictionary(g => g.Key, g => g.First().Status);

            regulReverseSectionCompleted = mappingStatuses.Count(m =>
                m.Status is "completed" or "failed");
            regulReverseSectionFailed = mappingStatuses.Count(m => m.Status == "failed");

            if (sections.Count > 0
                && (run.RegulPipelinePhase is "reverse" or "qualitative"
                    || run.Status is "running" or "processing"))
            {
                regulReverseSections = sections.Select(s =>
                {
                    var st = statusBySectionId.TryGetValue(s.Id, out var mapped)
                        ? mapped
                        : "queued";
                    var preview = s.SectionText?.Trim() ?? "";
                    if (preview.Length > 96) preview = preview[..93] + "…";
                    return new
                    {
                        sectionRef = s.SectionRef,
                        title = preview,
                        status = st,
                    };
                }).Cast<object>().ToList();
            }
        }

        return Ok(new
        {
            success = true,
            data = NdRegulApiProjection.MapPollResponse(
                run,
                pointRows,
                regulReverseSectionTotal,
                regulReverseSectionCompleted,
                regulReverseSectionFailed,
                regulReverseSections,
                litePoll),
        });
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (run.Status == DeletedStatus)
            return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        var timeline = await NdRunHistoryHelper.BuildTimelineAsync(db, run, ct);
        return Ok(new { success = true, data = timeline });
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        if (run.Status is "running" or "processing" && runCancellation.HasActiveWorker(id))
            return Ok(new { success = true, message = "Analysis already in progress", id, status = run.Status });

        var useDemoSimulation = await ShouldUseDemoSimulationAsync(demoCtx, run, ct);
        var useRegul = AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine);

        if (useRegul && run.RegulClausesConfirmedAt == null)
        {
            if (useDemoSimulation)
            {
                run.RegulClausesConfirmedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Confirm regulatory clauses before starting Regul workflow analysis.",
                });
            }
        }

        var linkedCt = runCancellation.Register(id);
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                if (useDemoSimulation)
                {
                    var demoInterception = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                    await demoInterception.SimulateDemoAnalysisRunAsync(id, profile.Id, useRegul, linkedCt);
                }
                else if (useRegul)
                {
                    var regulProc = scope.ServiceProvider.GetRequiredService<NdRegulAnalysisProcessor>();
                    await regulProc.ProcessRunAsync(id, linkedCt);
                }
                else
                {
                    var proc = scope.ServiceProvider.GetRequiredService<NdAnalysisProcessor>();
                    await proc.ProcessRunAsync(id, linkedCt);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop requested — ProcessRunAsync / Cancel endpoint persist cancelled state.
            }
            catch { /* logged in processor */ }
            finally
            {
                runCancellation.Clear(id);
            }
        }, CancellationToken.None);

        return Ok(new { success = true, message = "Analysis started", id });
    }

    [HttpPost("{id:guid}/start-forward")]
    public async Task<IActionResult> StartForwardOnly(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });
        if (!NdDemoDataFilters.CanAccessCreatedBy(run.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            return BadRequest(new { success = false, message = "Forward-only start is for Regul workflow runs." });

        if (run.Status is "running" or "processing" && runCancellation.HasActiveWorker(id))
            return Ok(new { success = true, message = "Analysis already in progress", id, status = run.Status });

        var useDemoSimulation = await ShouldUseDemoSimulationAsync(demoCtx, run, ct);
        const bool useRegul = true;

        if (run.RegulClausesConfirmedAt == null)
        {
            if (useDemoSimulation)
            {
                run.RegulClausesConfirmedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Confirm regulatory clauses before starting forward analysis.",
                });
            }
        }

        if (useDemoSimulation)
        {
            run.Status = "running";
            run.RegulPipelinePhase = "queued";
        }
        else
        {
            run.Status = "running";
            run.RegulPipelinePhase = "forward";
        }
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();

        var linkedCt = runCancellation.Register(id);
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                if (useDemoSimulation)
                {
                    var demoInterception = scope.ServiceProvider.GetRequiredService<NdDemoInterceptionService>();
                    await demoInterception.SimulateDemoAnalysisRunAsync(id, profile.Id, useRegul, linkedCt);
                }
                else
                {
                    var regulProc = scope.ServiceProvider.GetRequiredService<NdRegulAnalysisProcessor>();
                    await regulProc.ProcessForwardOnlyRunAsync(id, linkedCt);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop requested — processor persists cancelled state.
            }
            catch { /* logged in processor */ }
            finally
            {
                runCancellation.Clear(id);
            }
        }, CancellationToken.None);

        return Ok(new { success = true, message = "Forward-only analysis started", id });
    }

    /// <summary>Regul workflow: review/edit clauses then confirm before Run analysis (Regul.ai extraction_review gate).</summary>
    [HttpPost("{id:guid}/confirm-clauses")]
    public async Task<IActionResult> ConfirmClauses(
        Guid id,
        [FromBody] ConfirmClausesRequest? body,
        CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });
        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            return BadRequest(new { success = false, message = "Not a Regul workflow run." });
        if (run.Status is not "draft")
            return BadRequest(new { success = false, message = "Clauses can only be confirmed on draft runs." });

        if (body?.Clauses is { Count: > 0 })
        {
            var byId = run.Points.ToDictionary(p => p.Id);
            foreach (var clause in body.Clauses)
            {
                if (!byId.TryGetValue(clause.AnalysisPointId, out var point)) continue;
                point.PointSnapshot = MergeClauseSnapshot(point.PointSnapshot, clause);
                point.UpdatedAt = DateTimeOffset.UtcNow;

                var (no, text) = ParseClauseSnapshot(point.PointSnapshot);
                var finding = await db.NdRegulForwardFindings
                    .FirstOrDefaultAsync(f => f.AnalysisRunId == run.Id && f.AnalysisPointId == point.Id, ct);
                if (finding != null)
                {
                    finding.ClauseNo = no;
                    finding.ClauseText = text;
                    finding.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        run.RegulClausesConfirmedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            message = "Clauses confirmed. You can now run analysis.",
            regulClausesConfirmedAt = run.RegulClausesConfirmedAt,
        });
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var meta = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Status, r.CreatedBy })
            .FirstOrDefaultAsync(ct);
        if (meta == null || meta.Status == DeletedStatus)
            return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && meta.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });
        if (!NdDemoDataFilters.CanAccessCreatedBy(meta.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        var terminal = meta.Status is "completed" or "cancelled" or "dual_verify_failed" or "landing_ai_complete";
        if (terminal)
            return Ok(new { success = true, message = "Analysis already finished", id, status = meta.Status });

        runCancellation.RequestStop(id);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE analysis_runs
              SET status = 'cancelled', regul_pipeline_phase = 'done', updated_at = now()
              WHERE id = {0} AND status NOT IN ('completed', 'cancelled', 'deleted');
            UPDATE analysis_points
              SET landing_ai_status = CASE WHEN landing_ai_status IN ('pending','running') THEN 'cancelled' ELSE landing_ai_status END,
                  landing_ai_error = CASE WHEN landing_ai_status IN ('pending','running') THEN 'Stopped by user' ELSE landing_ai_error END,
                  dual_verify_status = CASE
                    WHEN landing_ai_status IN ('pending','running') THEN 'skipped'
                    WHEN dual_verify_status IN ('pending','running') THEN 'cancelled'
                    ELSE dual_verify_status END,
                  google_ai_status = CASE WHEN google_ai_status IN ('pending','running') THEN 'cancelled' ELSE google_ai_status END,
                  updated_at = now()
              WHERE analysis_run_id = {0};
            UPDATE regul_forward_findings
              SET status = 'cancelled', error_message = 'Stopped by user', updated_at = now()
              WHERE analysis_run_id = {0} AND status IN ('pending', 'running');
            """,
            id);

        runCancellation.Clear(id);

        var updatedStatus = await db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct) ?? "cancelled";

        return Ok(new { success = true, message = "Analysis stopped", id, status = updatedStatus });
    }

    [HttpPost("{id:guid}/rerun-point/{pointId:guid}")]
    public async Task<IActionResult> RerunPoint(
        Guid id,
        Guid pointId,
        [FromQuery] bool evidenceOnly = false,
        [FromQuery] int? actionIndex = null,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var pointExists = await db.NdAnalysisPoints.AnyAsync(p => p.Id == pointId && p.AnalysisRunId == id, ct);
        if (!pointExists) return NotFound(new { success = false, message = "Analysis point not found" });

        var demoOwned = run.CreatedBy != null
            && await demoDirectory.IsDemoProfileAsync(run.CreatedBy.Value, ct);
        if (demoCtx.ViewerIsDemo || demoOwned)
        {
            if (!evidenceOnly)
            {
                var block = NdDemoIsolationHelper.ForbidDemoAiOperations(demoCtx)
                    ?? await NdDemoIsolationHelper.ForbidLiveAiOnDemoOwnedRunAsync(demoDirectory, run.CreatedBy, ct);
                return block ?? (IActionResult)StatusCode(403);
            }
            return await SimulateDemoEvidenceRerunAsync(id, pointId, ct);
        }

        return QueuePointProcessing(run, id, pointId, dualVerifyOnly: false, evidenceOnly, actionIndex);
    }

    /// <summary>Re-run every open gap in the run against a freshly uploaded evidence document.</summary>
    [HttpPost("{id:guid}/rerun-with-evidence")]
    public async Task<IActionResult> RerunRunWithEvidence(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var demoOwned = run.CreatedBy != null
            && await demoDirectory.IsDemoProfileAsync(run.CreatedBy.Value, ct);
        if (demoCtx.ViewerIsDemo || demoOwned)
            return await SimulateDemoEvidenceRerunAsync(id, null, ct);

        var openPointIds = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == id
                && (p.FinalStatus == "non_compliant" || p.FinalStatus == "partial_compliant"))
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (openPointIds.Count == 0)
            return Ok(new { success = true, message = "No open gaps to re-run", queued = 0 });

        foreach (var openPointId in openPointIds)
            QueuePointProcessing(run, id, openPointId, dualVerifyOnly: false, evidenceOnly: true, actionIndex: null);

        return Ok(new
        {
            success = true,
            message = $"Re-running {openPointIds.Count} gap(s) against the uploaded evidence",
            queued = openPointIds.Count,
        });
    }

    private async Task<IActionResult> SimulateDemoEvidenceRerunAsync(Guid runId, Guid? pointId, CancellationToken ct)
    {
        var label = await ResolveLatestEvidenceLabelAsync(runId, pointId, ct);
        var updated = await demoIntercept.SimulateEvidenceRerunAsync(runId, pointId, label, ct);
        return Ok(new
        {
            success = true,
            demo = true,
            updated,
            message = updated == 0
                ? "No open gaps to re-run"
                : $"Re-ran {updated} gap(s) against \"{label}\"",
        });
    }

    private async Task<string> ResolveLatestEvidenceLabelAsync(Guid runId, Guid? pointId, CancellationToken ct)
    {
        var pointIds = await db.NdAnalysisPoints
            .Where(p => p.AnalysisRunId == runId && (pointId == null || p.Id == pointId.Value))
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (pointIds.Count == 0) return "uploaded evidence document";

        var name = await db.NdAnalysisPointAttachments
            .Where(a => pointIds.Contains(a.AnalysisPointId))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.FileName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "uploaded evidence document" : name;
    }

    [HttpPost("{id:guid}/rerun-forward")]
    public async Task<IActionResult> RerunForwardOnly(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var demoAiBlock = NdDemoIsolationHelper.ForbidDemoAiOperations(demoCtx);
        if (demoAiBlock != null) return demoAiBlock;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var demoOwnedBlock = await NdDemoIsolationHelper.ForbidLiveAiOnDemoOwnedRunAsync(
            demoDirectory, run.CreatedBy, ct);
        if (demoOwnedBlock != null) return demoOwnedBlock;

        if (!AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
            return BadRequest(new { success = false, message = "Forward-only rerun is for Regul workflow runs." });

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var regulProc = scope.ServiceProvider.GetRequiredService<NdRegulAnalysisProcessor>();
            try
            {
                await regulProc.RerunForwardPhaseAsync(id, CancellationToken.None);
            }
            catch
            {
                // Errors are persisted on the run by the processor.
            }
        }, CancellationToken.None);

        return Ok(new { success = true, message = "Forward-only rerun started (reverse preserved)" });
    }

    private IActionResult QueuePointProcessing(
        NdAnalysisRun run,
        Guid runId,
        Guid pointId,
        bool dualVerifyOnly,
        bool evidenceOnly,
        int? actionIndex)
    {
        var useRegul = AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine);
        if (useRegul && evidenceOnly)
            dualVerifyOnly = false;

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                if (useRegul)
                {
                    var regulProc = scope.ServiceProvider.GetRequiredService<NdRegulAnalysisProcessor>();
                    await regulProc.ProcessPointAsync(runId, pointId, dualVerifyOnly, CancellationToken.None);
                }
                else
                {
                    var proc = scope.ServiceProvider.GetRequiredService<NdAnalysisProcessor>();
                    await proc.ProcessPointAsync(
                        runId,
                        pointId,
                        dualVerifyOnly,
                        evidenceOnly,
                        actionIndex,
                        CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // Stop requested for this run.
            }
            catch
            {
                // Errors are persisted on the analysis point by the processor.
            }
        }, CancellationToken.None);

        return Ok(new
        {
            success = true,
            message = dualVerifyOnly ? "Reverse rerun started" : "Point rerun started",
        });
    }

    [HttpPost("{id:guid}/rerun-dual-verify/{pointId:guid}")]
    public async Task<IActionResult> RerunDualVerifyPoint(
        Guid id,
        Guid pointId,
        [FromQuery] bool evidenceOnly = false,
        [FromQuery] int? actionIndex = null,
        CancellationToken ct = default)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var pointExists = await db.NdAnalysisPoints.AnyAsync(p => p.Id == pointId && p.AnalysisRunId == id, ct);
        if (!pointExists) return NotFound(new { success = false, message = "Analysis point not found" });

        var demoOwned = run.CreatedBy != null
            && await demoDirectory.IsDemoProfileAsync(run.CreatedBy.Value, ct);
        if (demoCtx.ViewerIsDemo || demoOwned)
        {
            if (!evidenceOnly)
            {
                var block = NdDemoIsolationHelper.ForbidDemoAiOperations(demoCtx)
                    ?? await NdDemoIsolationHelper.ForbidLiveAiOnDemoOwnedRunAsync(demoDirectory, run.CreatedBy, ct);
                return block ?? (IActionResult)StatusCode(403);
            }
            return await SimulateDemoEvidenceRerunAsync(id, pointId, ct);
        }

        return QueuePointProcessing(run, id, pointId, dualVerifyOnly: true, evidenceOnly, actionIndex);
    }

    [HttpPost("{id:guid}/rerun-dual-verify/all")]
    public async Task<IActionResult> RerunAllFailedDualVerify(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var demoAiBlock = NdDemoIsolationHelper.ForbidDemoAiOperations(demoCtx);
        if (demoAiBlock != null) return demoAiBlock;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        var demoOwnedBlock = await NdDemoIsolationHelper.ForbidLiveAiOnDemoOwnedRunAsync(
            demoDirectory, run.CreatedBy, ct);
        if (demoOwnedBlock != null) return demoOwnedBlock;

        if (AnalysisWorkflowEngine.IsRegulFamily(run.WorkflowEngine))
        {
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var regulProc = scope.ServiceProvider.GetRequiredService<NdRegulAnalysisProcessor>();
                try
                {
                    await regulProc.RerunReversePhaseAsync(id, CancellationToken.None);
                }
                catch
                {
                    // Errors are persisted on the run by the processor.
                }
            }, CancellationToken.None);

            return Ok(new { success = true, message = "Reverse pass rerun started" });
        }

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var proc = scope.ServiceProvider.GetRequiredService<NdAnalysisProcessor>();
            try
            {
                await proc.RerunAllFailedDualVerifyAsync(id, CancellationToken.None);
            }
            catch
            {
                // Errors are persisted on analysis points by the processor.
            }
        }, CancellationToken.None);

        return Ok(new { success = true, message = "Phase 2 reruns started" });
    }

    [HttpPost("{id:guid}/submit-for-review")]
    public async Task<IActionResult> SubmitForReview(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        if (run.Status is not ("completed" or "dual_verify_failed" or "landing_ai_complete"))
            return BadRequest(new { success = false, message = "Run is not ready for review." });

        var from = run.Status;
        run.Status = "submitted_for_review";
        run.SubmittedToCheckerAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        db.NdAnalysisReviews.Add(new NdAnalysisReview
        {
            AnalysisRunId = id,
            ReviewerId = profile.Id,
            ReviewerRole = "maker",
            Action = "submitted",
        });

        await db.SaveChangesAsync(ct);
        await RecordStatusChangeAsync(db, id, from, run.Status, profile.Id, null, ct);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/resubmit-for-review")]
    public async Task<IActionResult> ResubmitForReview(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (run.Status != "pulled_back")
            return BadRequest(new { success = false, message = "Run is not pulled back." });

        var from = run.Status;
        run.Status = "submitted_for_review";
        run.SubmittedToCheckerAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        db.NdAnalysisReviews.Add(new NdAnalysisReview
        {
            AnalysisRunId = id,
            ReviewerId = profile!.Id,
            ReviewerRole = "maker",
            Action = "submitted",
        });

        await db.SaveChangesAsync(ct);
        await RecordStatusChangeAsync(db, id, from, run.Status, profile.Id, "Resubmitted", ct);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/soft-delete")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return await SoftDeleteLegacyAsync(id, profile!, ct);

        var isDemoOwned = run.CreatedBy is Guid createdBy && demoCtx.DemoProfileIds.Contains(createdBy);
        var isDemoMarked = NdDemoDataFilters.IsDemoMarkedAnalysisRun(run);
        if (demoCtx.Enabled && !demoCtx.ViewerIsDemo && (isDemoOwned || isDemoMarked))
        {
            runCancellation.RequestStop(id);
            runCancellation.Clear(id);
            var removed = await demoWorkspace.PermanentlyDeleteAnalysisRunAsync(id, ct);
            if (!removed)
                return NotFound(new { success = false, message = "Not found" });
            dashboardCache.Invalidate();
            logger.LogInformation(
                "Production admin permanently deleted demo analysis run {RunId} (created by demo profile {CreatedBy})",
                id,
                run.CreatedBy);
            return Ok(new
            {
                success = true,
                permanentlyDeleted = true,
                message = "Demo analysis run permanently removed.",
            });
        }

        if (run.Status == DeletedStatus)
            return BadRequest(new { success = false, message = "Analysis run is already deleted." });
        if (profile!.Role == "maker" && run.CreatedBy != null && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var from = run.Status;
        run.StatusBeforeDelete = from;
        run.Status = DeletedStatus;
        run.DeletedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        db.NdAnalysisStatusHistories.Add(new NdAnalysisStatusHistory
        {
            AnalysisRunId = id,
            FromStatus = from,
            ToStatus = DeletedStatus,
            ChangedBy = profile.Id,
            Comment = "Soft deleted",
        });

        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();

        return Ok(new { success = true, message = "Analysis run removed from workspace." });
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null)
        {
            var hidden = await db.NdHiddenLegacyRuns.FirstOrDefaultAsync(h => h.LegacyId == id, ct);
            if (hidden == null) return NotFound(new { success = false, message = "Not found" });
            db.NdHiddenLegacyRuns.Remove(hidden);
            await db.SaveChangesAsync(ct);
            dashboardCache.Invalidate();
            return Ok(new { success = true, message = "Analysis run restored." });
        }
        if (run.Status != DeletedStatus)
            return BadRequest(new { success = false, message = "Analysis run is not deleted." });

        var from = run.Status;
        var restored = string.IsNullOrWhiteSpace(run.StatusBeforeDelete) ? "draft" : run.StatusBeforeDelete;
        run.Status = restored;
        run.StatusBeforeDelete = null;
        run.DeletedAt = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RecordStatusChangeAsync(db, id, from, restored, profile!.Id, "Restored", ct);
        dashboardCache.Invalidate();

        return Ok(new { success = true, message = "Analysis run restored.", status = restored });
    }

    /// <summary>Legacy analyses have no status column we own — hide via hidden_legacy_runs marker.</summary>
    private async Task<IActionResult> SoftDeleteLegacyAsync(Guid id, NdProfile profile, CancellationToken ct)
    {
        string? source = null;
        if (await db.DocumentAnalysisRuns.AsNoTracking().AnyAsync(r => r.Id == id, ct))
            source = "legacy_analysis";
        else if (await db.DualVerifySessions.AsNoTracking().AnyAsync(s => s.Id == id, ct))
            source = "legacy_dual_verify";

        if (source == null) return NotFound(new { success = false, message = "Not found" });

        if (await db.NdHiddenLegacyRuns.AsNoTracking().AnyAsync(h => h.LegacyId == id, ct))
            return BadRequest(new { success = false, message = "Analysis run is already deleted." });

        db.NdHiddenLegacyRuns.Add(new NdHiddenLegacyRun
        {
            Source = source,
            LegacyId = id,
            DeletedBy = profile.Id,
        });
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, message = "Analysis run removed from workspace." });
    }

    private async Task<bool> ShouldUseDemoSimulationAsync(
        NdDemoIsolationContext demoCtx,
        NdAnalysisRun run,
        CancellationToken ct)
    {
        if (!demoCtx.Enabled) return false;
        if (demoCtx.ViewerIsDemo) return true;
        if (run.CreatedBy is Guid createdBy && await demoDirectory.IsDemoProfileAsync(createdBy, ct))
            return true;
        return false;
    }

    private async Task<List<object>> LoadHiddenLegacyRunsAsync(CancellationToken ct)
    {
        var hidden = await db.NdHiddenLegacyRuns.AsNoTracking()
            .OrderByDescending(h => h.DeletedAt)
            .Take(200)
            .ToListAsync(ct);
        if (hidden.Count == 0) return [];

        var analysisIds = hidden.Where(h => h.Source == "legacy_analysis").Select(h => h.LegacyId).ToList();
        var dvIds = hidden.Where(h => h.Source == "legacy_dual_verify").Select(h => h.LegacyId).ToList();

        var legacyRuns = analysisIds.Count > 0
            ? await db.DocumentAnalysisRuns.AsNoTracking().Where(r => analysisIds.Contains(r.Id)).ToListAsync(ct)
            : [];
        var dvSessions = dvIds.Count > 0
            ? await db.DualVerifySessions.AsNoTracking().Where(s => dvIds.Contains(s.Id)).ToListAsync(ct)
            : [];

        var items = new List<object>();
        foreach (var h in hidden)
        {
            if (h.Source == "legacy_analysis")
            {
                var run = legacyRuns.FirstOrDefault(r => r.Id == h.LegacyId);
                if (run == null) continue;
                items.Add(new
                {
                    id = run.Id,
                    source = "legacy_analysis",
                    name = string.IsNullOrWhiteSpace(run.Label)
                        ? $"{run.RegulationFileName ?? "Regulation"} × {run.InternalFileName ?? "Compliance"}"
                        : run.Label,
                    status = DeletedStatus,
                    statusBeforeDelete = NdLegacyDataQueries.MapLegacyAnalysisStatus(run.Status),
                    deletedAt = h.DeletedAt,
                    totalPointsCount = run.PointCount,
                    processedPointsCount = run.CompletedPoints,
                    createdAt = run.CreatedAt,
                });
            }
            else
            {
                var session = dvSessions.FirstOrDefault(s => s.Id == h.LegacyId);
                if (session == null) continue;
                items.Add(new
                {
                    id = session.Id,
                    source = "legacy_dual_verify",
                    name = $"{session.GovFileName ?? session.GovDocId} × {session.InternalFileName ?? session.InternalDocId}",
                    status = DeletedStatus,
                    statusBeforeDelete = NdLegacyDataQueries.MapDualVerifyStatus(session.Status),
                    deletedAt = h.DeletedAt,
                    totalPointsCount = session.TotalPoints,
                    processedPointsCount = session.CompletedPoints,
                    createdAt = new DateTimeOffset(DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc)),
                });
            }
        }
        return items;
    }

    private static object MapRunSummary(NdAnalysisRun r) =>
        NdRegulApiProjection.MapRunSummary(r);

    private static object MapRunDetail(NdAnalysisRun r, string? creatorName) =>
        NdRegulApiProjection.MapRunDetail(r, creatorName);

    private static (string ClauseNo, string ClauseText) ParseClauseSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var no = root.TryGetProperty("pointNumber", out var pn) ? pn.GetString() ?? ""
                : root.TryGetProperty("point_number", out var pn2) ? pn2.GetString() ?? "" : "";
            var text = root.TryGetProperty("pointText", out var pt) ? pt.GetString() ?? ""
                : root.TryGetProperty("point_text", out var pt2) ? pt2.GetString() ?? ""
                : root.TryGetProperty("pointContent", out var pc) ? pc.GetString() ?? ""
                : root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (no, text);
        }
        catch
        {
            return ("", "");
        }
    }

    private static string MergeClauseSnapshot(string existing, ConfirmClausesClauseUpdate update)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(existing) ? "{}" : existing) as JsonObject ?? new JsonObject();
        if (!string.IsNullOrWhiteSpace(update.PointNumber))
            node["pointNumber"] = update.PointNumber.Trim();
        if (!string.IsNullOrWhiteSpace(update.PointTitle))
            node["pointTitle"] = update.PointTitle.Trim();
        if (update.PointContent != null)
            node["pointContent"] = update.PointContent;
        return node.ToJsonString();
    }

    private static object MapPoint(NdAnalysisPoint p, string? pointSnapshotOverride = null, string? workflowEngine = null) =>
        NdRegulApiProjection.MapPoint(p, workflowEngine, pointSnapshotOverride);
}
