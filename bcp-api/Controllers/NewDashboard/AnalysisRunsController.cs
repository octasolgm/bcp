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
    IServiceScopeFactory scopeFactory) : NdControllerBase
{
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
        CancellationToken ct = default)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var q = db.NdAnalysisRuns.AsNoTracking().AsQueryable();

        if (profile!.Role == "maker" || mineOnly)
            q = q.Where(r => r.CreatedBy == profile.Id);
        else if (profile.Role == "checker" && string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == "submitted_for_review" || r.Status == "pulled_back");
        else if (profile.Role == "reviewer" && string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == "checker_approved");

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(r => r.Status == status);

        var runs = await q.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        var items = runs.Select(NdLegacyDataQueries.MapNdRunSummary).Cast<object>().ToList();

        var linkedDvIds = await db.DocumentAnalysisRuns.AsNoTracking()
            .Where(r => r.DualVerifySessionId != null)
            .Select(r => r.DualVerifySessionId!.Value)
            .ToListAsync(ct);
        var linkedSet = linkedDvIds.ToHashSet();

        var legacyRuns = await db.DocumentAnalysisRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        items.AddRange(legacyRuns.Select(NdLegacyDataQueries.MapLegacyAnalysisRun));

        var standaloneDv = await db.DualVerifySessions.AsNoTracking()
            .Where(s => !linkedSet.Contains(s.Id))
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        items.AddRange(standaloneDv.Select(NdLegacyDataQueries.MapLegacyDualVerifySession));

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

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });

        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

        var creator = run.CreatedBy.HasValue
            ? await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == run.CreatedBy, ct)
            : null;

        var history = await db.NdAnalysisStatusHistories.AsNoTracking()
            .Where(h => h.AnalysisRunId == id)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                run = MapRunDetail(run, creator?.FullName),
                points = run.Points.OrderBy(p => p.CreatedAt).Select(MapPoint),
                history,
            },
        });
    }

    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns
            .Include(r => r.Points)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound(new { success = false, message = "Not found" });
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403, new { success = false, message = "Forbidden" });

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
                points = run.Points.Select(MapPoint),
            },
        });
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

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var proc = scope.ServiceProvider.GetRequiredService<NdAnalysisProcessor>();
            try
            {
                await proc.ProcessRunAsync(id, CancellationToken.None);
            }
            catch { /* logged in processor */ }
        }, CancellationToken.None);

        return Ok(new { success = true, message = "Analysis started", id });
    }

    [HttpPost("{id:guid}/rerun-point/{pointId:guid}")]
    public async Task<IActionResult> RerunPoint(Guid id, Guid pointId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        await processor.ProcessPointAsync(id, pointId, dualVerifyOnly: false, ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/rerun-dual-verify/{pointId:guid}")]
    public async Task<IActionResult> RerunDualVerifyPoint(Guid id, Guid pointId, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var run = await db.NdAnalysisRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run == null) return NotFound();
        if (profile!.Role == "maker" && run.CreatedBy != profile.Id)
            return StatusCode(403);

        await processor.ProcessPointAsync(id, pointId, dualVerifyOnly: true, ct);
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
            ReviewerRole = "checker",
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
            ReviewerRole = "checker",
            Action = "submitted",
        });

        await db.SaveChangesAsync(ct);
        await RecordStatusChangeAsync(db, id, from, run.Status, profile.Id, "Resubmitted", ct);
        return Ok(new { success = true });
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

    private static object MapPoint(NdAnalysisPoint p) => new
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
    };
}
