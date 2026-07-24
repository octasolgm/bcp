using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/analysis-runs")]
public class AnalysisRunsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdAnalysisProcessor processor,
    DemoAnalysisSeedService demoSeed,
    IServiceScopeFactory scopeFactory,
    NdAnalysisRunCancellationTracker runCancellation) : NdControllerBase
{
    private const string DeletedStatus = "deleted";

    public record CreateRunRequest(
        string Name,
        string? Description,
        Guid? LibraryId,
        Guid? DepartmentId,
        List<object> SelectedPointsSnapshot,
        List<string> SelectedInternalDocIds,
        List<string> SelectedRegulationDocIds);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] bool mineOnly = false,
        [FromQuery] bool deletedOnly = false,
        [FromQuery] bool ndOnly = false,
        [FromQuery] bool summaryOnly = false,
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        if (deletedOnly)
        {
            if (profile!.Role != "super_admin")
                return StatusCode(403, new { success = false, message = "Forbidden" });

            var deletedRuns = await db.NdAnalysisRuns.AsNoTracking()
                .Where(r => r.Status == DeletedStatus)
                .OrderByDescending(r => r.DeletedAt ?? r.UpdatedAt)
                .Take(200)
                .ToListAsync(ct);

            var deletedItems = deletedRuns.Select(r => NdLegacyDataQueries.MapNdRunSummary(r)).Cast<object>().ToList();
            deletedItems.AddRange(await LoadHiddenLegacyRunsAsync(ct));
            return Ok(new { success = true, data = deletedItems });
        }

        var hiddenLegacy = await db.NdHiddenLegacyRuns.AsNoTracking()
            .Select(h => h.LegacyId)
            .ToListAsync(ct);
        var hiddenLegacySet = hiddenLegacy.ToHashSet();

        var q = db.NdAnalysisRuns.AsNoTracking()
            .Where(r => r.Status != DeletedStatus)
            .AsQueryable();

        if (profile!.Role == "maker" || mineOnly)
            q = q.Where(r => r.CreatedBy == profile.Id);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        var runs = await q.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);

        if (ndOnly)
        {
            if (summaryOnly)
            {
                var summaries = await NdRunEnrichmentHelper.MapSummariesLightAsync(db, runs, ct);
                return Ok(new { success = true, data = summaries });
            }

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
                .Where(r => !hiddenLegacySet.Contains(r.Id))
                .Select(NdLegacyDataQueries.MapLegacyAnalysisRun));

            var standaloneDv = await db.DualVerifySessions.AsNoTracking()
                .Where(s => !linkedSet.Contains(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .Take(50)
                .ToListAsync(ct);
            items.AddRange(standaloneDv
                .Where(s => !hiddenLegacySet.Contains(s.Id))
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

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRunRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var points = body.SelectedPointsSnapshot ?? [];
        var run = new NdAnalysisRun
        {
            Name = body.Name.Trim(),
            Description = body.Description,
            LibraryId = body.LibraryId,
            DepartmentId = body.DepartmentId,
            SelectedPointsSnapshot = JsonSerializer.Serialize(points),
            SelectedInternalDocIds = JsonSerializer.Serialize(body.SelectedInternalDocIds ?? []),
            SelectedRegulationDocIds = JsonSerializer.Serialize(body.SelectedRegulationDocIds ?? []),
            TotalPointsCount = points.Count,
            Status = "draft",
            CreatedBy = profile!.Id,
        };
        db.NdAnalysisRuns.Add(run);

        foreach (var pt in points)
        {
            var json = JsonSerializer.Serialize(pt);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? regPointId = root.TryGetProperty("regulationPointId", out var rpid) && Guid.TryParse(rpid.GetString(), out var g1) ? g1 : null;

            db.NdAnalysisPoints.Add(new NdAnalysisPoint
            {
                AnalysisRunId = run.Id,
                RegulationPointId = regPointId,
                PointSnapshot = json,
            });
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = new { id = run.Id } });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
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

        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var history = await db.NdAnalysisStatusHistories.AsNoTracking()
            .Where(h => h.AnalysisRunId == id)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        var points = await db.NdAnalysisPoints
            .AsNoTracking()
            .Where(p => p.AnalysisRunId == id)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                id = p.Id,
                regulationPointId = p.RegulationPointId,
                pointSnapshot = p.PointSnapshot,
                landingAiStatus = p.LandingAiStatus,
                landingAiResult = p.LandingAiResult,
                landingAiError = p.LandingAiError,
                googleAiStatus = p.GoogleAiStatus,
                googleAiResult = p.GoogleAiResult,
                googleAiError = p.GoogleAiError,
                dualVerifyStatus = p.DualVerifyStatus,
                finalStatus = p.FinalStatus,
                finalActionPlan = p.FinalActionPlan,
                originalAiActionPlan = p.OriginalAiActionPlan,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                run = MapRunDetail(run, creator?.FullName),
                points,
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
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
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

        if (resume)
        {
            // One-shot open/resume payload: snapshots + AI text via projection (no Include / no PDF enrich).
            var resumePoints = await db.NdAnalysisPoints
                .AsNoTracking()
                .Where(p => p.AnalysisRunId == id)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new
                {
                    id = p.Id,
                    regulationPointId = p.RegulationPointId,
                    pointSnapshot = p.PointSnapshot,
                    landingAiStatus = p.LandingAiStatus,
                    landingAiResult = p.LandingAiResult,
                    landingAiError = p.LandingAiError,
                    googleAiStatus = p.GoogleAiStatus,
                    googleAiResult = p.GoogleAiResult,
                    googleAiError = p.GoogleAiError,
                    dualVerifyStatus = p.DualVerifyStatus,
                    finalStatus = p.FinalStatus,
                    finalActionPlan = p.FinalActionPlan,
                    originalAiActionPlan = p.OriginalAiActionPlan,
                })
                .ToListAsync(ct);

            return Ok(new
            {
                success = true,
                data = new
                {
                    id = run.Id,
                    name = run.Name,
                    status = run.Status,
                    libraryId = run.LibraryId,
                    selectedPointsSnapshot = run.SelectedPointsSnapshot,
                    selectedInternalDocIds = run.SelectedInternalDocIds,
                    selectedRegulationDocIds = run.SelectedRegulationDocIds,
                    totalPointsCount = run.TotalPointsCount,
                    processedPointsCount = run.ProcessedPointsCount,
                    landingAiCompletedCount = run.LandingAiCompletedCount,
                    dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                    dualVerifyFailedCount = run.DualVerifyFailedCount,
                    points = resumePoints,
                },
            });
        }

        // Poll payload: statuses only — never load PointSnapshot (can be huge × 140+ points).
        var points = await db.NdAnalysisPoints
            .AsNoTracking()
            .Where(p => p.AnalysisRunId == id)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                id = p.Id,
                regulationPointId = p.RegulationPointId,
                landingAiStatus = p.LandingAiStatus,
                googleAiStatus = p.GoogleAiStatus,
                dualVerifyStatus = p.DualVerifyStatus,
                finalStatus = p.FinalStatus,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                id = run.Id,
                status = run.Status,
                totalPointsCount = run.TotalPointsCount,
                processedPointsCount = run.ProcessedPointsCount,
                landingAiCompletedCount = run.LandingAiCompletedCount,
                dualVerifyCompletedCount = run.DualVerifyCompletedCount,
                dualVerifyFailedCount = run.DualVerifyFailedCount,
                points,
            },
        });
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
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

        var timeline = await NdRunHistoryHelper.BuildTimelineAsync(db, run, ct);
        return Ok(new { success = true, data = timeline });
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var linkedCt = runCancellation.Register(id);
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var proc = scope.ServiceProvider.GetRequiredService<NdAnalysisProcessor>();
            try
            {
                await proc.ProcessRunAsync(id, linkedCt);
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

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (run.Status == DeletedStatus)
            return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var terminal = run.Status is "completed" or "cancelled" or "dual_verify_failed" or "landing_ai_complete";
        if (terminal && run.Points.All(p =>
                p.LandingAiStatus is not ("pending" or "running")
                && p.DualVerifyStatus is not ("pending" or "running")))
        {
            return Ok(new { success = true, message = "Analysis already finished", id, status = run.Status });
        }

        runCancellation.RequestStop(id);

        foreach (var point in run.Points)
        {
            if (point.LandingAiStatus is "pending" or "running")
            {
                point.LandingAiStatus = "cancelled";
                point.LandingAiError = "Stopped by user";
                point.DualVerifyStatus = "skipped";
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (point.DualVerifyStatus is "pending" or "running")
            {
                point.DualVerifyStatus = "cancelled";
                point.GoogleAiStatus = point.GoogleAiStatus is "running" or "pending" ? "cancelled" : point.GoogleAiStatus;
                point.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        run.Status = "cancelled";
        run.ProcessedPointsCount = run.Points.Count(p =>
            p.LandingAiStatus is "compliant" or "partial_compliant" or "non_compliant" or "failed" or "cancelled");
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, message = "Analysis stopped", id, status = run.Status });
    }

    [HttpPost("{id:guid}/rerun-point/{pointId:guid}")]
    public async Task<IActionResult> RerunPoint(
        Guid id,
        Guid pointId,
        [FromQuery] bool evidenceOnly = false,
        [FromQuery] int? actionIndex = null,
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        await processor.ProcessPointAsync(id, pointId, dualVerifyOnly: false, evidenceOnly, actionIndex, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/rerun-dual-verify/{pointId:guid}")]
    public async Task<IActionResult> RerunDualVerifyPoint(
        Guid id,
        Guid pointId,
        [FromQuery] bool evidenceOnly = false,
        [FromQuery] int? actionIndex = null,
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        await processor.ProcessPointAsync(id, pointId, dualVerifyOnly: true, evidenceOnly, actionIndex, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/rerun-dual-verify/all")]
    public async Task<IActionResult> RerunAllFailedDualVerify(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        await processor.RerunAllFailedDualVerifyAsync(id, ct);
        return Ok(new { success = true });
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
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/soft-delete")]
    public async Task<IActionResult> SoftDelete(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return await SoftDeleteLegacyAsync(id, profile!, ct);
        if (run.Status == DeletedStatus)
            return BadRequest(new { success = false, message = "Analysis run is already deleted." });
        if (profile!.Role == "maker" && run.CreatedBy != null && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var from = run.Status;
        run.StatusBeforeDelete = from;
        run.Status = DeletedStatus;
        run.DeletedAt = DateTimeOffset.UtcNow;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RecordStatusChangeAsync(db, id, from, DeletedStatus, profile.Id, "Soft deleted", ct);

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

    private static object MapRunSummary(NdAnalysisRun r) => new
    {
        id = r.Id,
        name = r.Name,
        status = r.Status,
        totalPointsCount = r.TotalPointsCount,
        processedPointsCount = r.ProcessedPointsCount,
        dualVerifyFailedCount = r.DualVerifyFailedCount,
        departmentId = r.DepartmentId,
        createdBy = r.CreatedBy,
        createdAt = r.CreatedAt,
        submittedToCheckerAt = r.SubmittedToCheckerAt,
    };

    private static object MapRunDetail(NdAnalysisRun r, string? creatorName) => new
    {
        id = r.Id,
        name = r.Name,
        description = r.Description,
        status = r.Status,
        libraryId = r.LibraryId,
        selectedPointsSnapshot = r.SelectedPointsSnapshot,
        selectedInternalDocIds = r.SelectedInternalDocIds,
        selectedRegulationDocIds = r.SelectedRegulationDocIds,
        totalPointsCount = r.TotalPointsCount,
        processedPointsCount = r.ProcessedPointsCount,
        landingAiCompletedCount = r.LandingAiCompletedCount,
        dualVerifyCompletedCount = r.DualVerifyCompletedCount,
        dualVerifyFailedCount = r.DualVerifyFailedCount,
        departmentId = r.DepartmentId,
        createdBy = r.CreatedBy,
        createdByName = creatorName,
        createdAt = r.CreatedAt,
    };

    private static object MapPoint(NdAnalysisPoint p, string? pointSnapshotOverride = null) => new
    {
        id = p.Id,
        regulationPointId = p.RegulationPointId,
        pointSnapshot = pointSnapshotOverride ?? p.PointSnapshot,
        landingAiStatus = p.LandingAiStatus,
        landingAiResult = p.LandingAiResult,
        landingAiError = p.LandingAiError,
        googleAiStatus = p.GoogleAiStatus,
        googleAiResult = p.GoogleAiResult,
        googleAiError = p.GoogleAiError,
        dualVerifyStatus = p.DualVerifyStatus,
        finalStatus = p.FinalStatus,
        finalActionPlan = p.FinalActionPlan,
        originalAiActionPlan = p.OriginalAiActionPlan,
    };
}
