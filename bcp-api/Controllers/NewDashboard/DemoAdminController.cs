using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/admin/demo")]
public class DemoAdminController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoWorkspaceService workspace,
    NdDemoUserDirectory demoDirectory,
    DemoAnalysisSeedService demoSeed,
    NdDemoInterceptionService demoInterception,
    Microsoft.Extensions.Options.IOptions<NdDemoIsolationOptions> demoOptions) : NdControllerBase
{
    public record TemplateUpdateRequest(
        string? Name,
        string? Description,
        string? RegulationNameHint,
        string? InternalNameHint,
        bool? IsActive,
        int? SortOrder);

    public record PointUpsertRequest(
        string? ClauseNo,
        string? ClauseTitle,
        string? DesignStatus,
        string? OperatingStatus,
        string? OverallStatus,
        double? Confidence,
        string? Interpretation,
        List<string>? PolicyExtract,
        string? DocumentReference,
        string? GapDescription,
        string? SuggestedAction,
        string? GapDirection,
        int? SortOrder);

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo)
            return StatusCode(403, new { success = false, message = "Demo accounts cannot manage the demo workspace." });

        try
        {
            await workspace.EnsureTemplatesSeededAsync(ct);
            var templates = await db.NdDemoAnalysisTemplates.AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .Select(t => new
                {
                    t.Id,
                    t.Code,
                    t.Name,
                    t.Description,
                    t.RegulationNameHint,
                    t.InternalNameHint,
                    t.IsActive,
                    t.SortOrder,
                    pointCount = db.NdDemoAnalysisTemplatePoints.Count(p => p.TemplateId == t.Id),
                    t.UpdatedAt,
                })
                .ToListAsync(ct);

            return Ok(new
            {
                success = true,
                data = new
                {
                    templates,
                    note =
                        "Clear actions only affect demo-owned workspace records. Demo analysis templates (analys1demo / analys2demo) are never deleted.",
                },
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = $"Could not load demo templates: {ex.GetBaseException().Message}",
            });
        }
    }

    /// <summary>Diagnose whether demo CBUAE extract can clone from the configured production template.</summary>
    [HttpGet("regulation-clone-source")]
    public async Task<IActionResult> RegulationCloneSource(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker");
        if (error != null) return error;

        var templateId = demoOptions.Value.DemoRegulationTemplateDocumentId;
        var byId = await db.NdRegulationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == templateId, ct);
        var byStored = byId == null
            ? await db.NdRegulationDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.StoredDocumentId == templateId, ct)
            : null;
        var resolved = byId ?? byStored;
        var rawCount = 0;
        var canonicalCount = 0;
        if (resolved != null)
        {
            var points = await db.NdRegulationPoints.AsNoTracking()
                .Where(p => p.RegulationDocumentId == resolved.Id && p.Status == NdRegulationPointStatus.Active)
                .ToListAsync(ct);
            rawCount = points.Count;
            canonicalCount = NdRegulationPointCanonicalFilter.CountCanonical(points);
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                configuredTemplateId = templateId,
                resolvedRegulationDocumentId = resolved?.Id,
                resolvedVia = byId != null ? "regulationDocumentId" : byStored != null ? "storedDocumentId" : "notFound",
                name = resolved?.Name,
                extractionStatus = resolved?.ExtractionStatus,
                rawActivePoints = rawCount,
                canonicalPoints = canonicalCount,
                cloneReady = canonicalCount > 0,
            },
        });
    }

    [HttpPost("clear")]
    public async Task<IActionResult> Clear([FromBody] DemoClearRequest body, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo)
            return StatusCode(403, new { success = false, message = "Demo accounts cannot clear the demo workspace." });

        if (!body.ClearAll
            && !body.ClearInternalDocuments
            && !body.ClearRegulationDocuments
            && !body.ClearLibraries
            && !body.ClearAnalysisRuns
            && !body.ClearUsers)
        {
            return BadRequest(new { success = false, message = "Select at least one clear option." });
        }

        var result = await workspace.ClearDemoWorkspaceAsync(body, ct);
        return Ok(new
        {
            success = true,
            data = result,
            message =
                "Demo workspace cleared. Seed analysis templates were preserved. Soft-deleted demo documents and analyses were permanently removed when applicable.",
        });
    }

    [HttpGet("templates")]
    public async Task<IActionResult> ListTemplates(CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo)
            return StatusCode(403, new { success = false, message = "Demo accounts cannot manage the demo workspace." });

        try
        {
            await workspace.EnsureTemplatesSeededAsync(ct);
            var templates = await db.NdDemoAnalysisTemplates.AsNoTracking()
                .OrderBy(t => t.SortOrder)
                .Select(t => new
                {
                    t.Id,
                    t.Code,
                    t.Name,
                    t.Description,
                    t.RegulationNameHint,
                    t.InternalNameHint,
                    t.IsActive,
                    t.SortOrder,
                    pointCount = db.NdDemoAnalysisTemplatePoints.Count(p => p.TemplateId == t.Id),
                    t.CreatedAt,
                    t.UpdatedAt,
                })
                .ToListAsync(ct);

            return Ok(new { success = true, data = templates });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = $"Could not load demo templates: {ex.GetBaseException().Message}",
            });
        }
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var template = await db.NdDemoAnalysisTemplates.AsNoTracking()
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null)
            return NotFound(new { success = false, message = "Template not found." });

        return Ok(new { success = true, data = MapTemplate(template) });
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] TemplateUpdateRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var template = await db.NdDemoAnalysisTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null)
            return NotFound(new { success = false, message = "Template not found." });

        if (body.Name != null) template.Name = body.Name.Trim();
        if (body.Description != null) template.Description = body.Description.Trim();
        if (body.RegulationNameHint != null) template.RegulationNameHint = body.RegulationNameHint.Trim();
        if (body.InternalNameHint != null) template.InternalNameHint = body.InternalNameHint.Trim();
        if (body.IsActive.HasValue) template.IsActive = body.IsActive.Value;
        if (body.SortOrder.HasValue) template.SortOrder = body.SortOrder.Value;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { success = true, data = new { template.Id, template.Code, template.Name, template.IsActive } });
    }

    [HttpPost("templates/{id:guid}/points")]
    public async Task<IActionResult> AddPoint(Guid id, [FromBody] PointUpsertRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var template = await db.NdDemoAnalysisTemplates
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null)
            return NotFound(new { success = false, message = "Template not found." });

        var sort = body.SortOrder ?? (template.Points.Count == 0 ? 0 : template.Points.Max(p => p.SortOrder) + 1);
        var point = ApplyPoint(new NdDemoAnalysisTemplatePoint { TemplateId = id, SortOrder = sort }, body);
        db.NdDemoAnalysisTemplatePoints.Add(point);
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PropagateTemplateChangeAsync(template, ct);

        return Ok(new { success = true, data = MapPoint(point) });
    }

    [HttpPut("templates/{templateId:guid}/points/{pointId:guid}")]
    public async Task<IActionResult> UpdatePoint(
        Guid templateId,
        Guid pointId,
        [FromBody] PointUpsertRequest body,
        CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var point = await db.NdDemoAnalysisTemplatePoints
            .FirstOrDefaultAsync(p => p.Id == pointId && p.TemplateId == templateId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Point not found." });

        ApplyPoint(point, body);
        point.UpdatedAt = DateTimeOffset.UtcNow;
        var template = await db.NdDemoAnalysisTemplates.FirstAsync(t => t.Id == templateId, ct);
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PropagateTemplateChangeAsync(template, ct);

        return Ok(new { success = true, data = MapPoint(point) });
    }

    [HttpDelete("templates/{templateId:guid}/points/{pointId:guid}")]
    public async Task<IActionResult> DeletePoint(Guid templateId, Guid pointId, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var point = await db.NdDemoAnalysisTemplatePoints
            .FirstOrDefaultAsync(p => p.Id == pointId && p.TemplateId == templateId, ct);
        if (point == null)
            return NotFound(new { success = false, message = "Point not found." });

        db.NdDemoAnalysisTemplatePoints.Remove(point);
        var template = await db.NdDemoAnalysisTemplates.FirstAsync(t => t.Id == templateId, ct);
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PropagateTemplateChangeAsync(template, ct);

        return Ok(new { success = true });
    }

    [HttpPost("templates/{id:guid}/reload-from-seed-file")]
    public async Task<IActionResult> ReloadFromSeedFile(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var template = await db.NdDemoAnalysisTemplates
            .Include(t => t.Points)
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template == null)
            return NotFound(new { success = false, message = "Template not found." });
        if (template.Code != NdDemoWorkspaceService.Analys1Code)
            return BadRequest(new { success = false, message = "Seed file reload is only for analys1demo." });

        db.NdDemoAnalysisTemplatePoints.RemoveRange(template.Points);
        await db.SaveChangesAsync(ct);

        // Re-seed points from file via Ensure (points empty → load).
        await workspace.EnsureTemplatesSeededAsync(ct);
        var syncedRuns = await demoSeed.SyncAllCbuaeDemoRunsFromTemplateAsync(ct);
        var syncedDocs = await demoInterception.ResyncDemoCbuaeRegulationDocumentsAsync(ct);
        var refreshed = await db.NdDemoAnalysisTemplates.AsNoTracking()
            .Include(t => t.Points)
            .FirstAsync(t => t.Id == id, ct);

        return Ok(new
        {
            success = true,
            data = MapTemplate(refreshed),
            message = $"Reloaded {refreshed.Points.Count} points from seed file. "
                + $"Updated {syncedRuns} demo analysis run point(s) and re-cloned {syncedDocs} demo regulation document(s).",
        });
    }

    /// <summary>
    /// Keep every demo surface on the same clause list: analysis runs get re-judged and demo
    /// regulation documents get re-cloned so their point counts match the template.
    /// </summary>
    private async Task PropagateTemplateChangeAsync(NdDemoAnalysisTemplate template, CancellationToken ct)
    {
        if (template.Code != NdDemoWorkspaceService.Analys1Code) return;
        await demoSeed.SyncAllCbuaeDemoRunsFromTemplateAsync(ct);
        await demoInterception.ResyncDemoCbuaeRegulationDocumentsAsync(ct);
    }

    private static NdDemoAnalysisTemplatePoint ApplyPoint(NdDemoAnalysisTemplatePoint point, PointUpsertRequest body)
    {
        if (body.ClauseNo != null) point.ClauseNo = body.ClauseNo.Trim();
        if (body.ClauseTitle != null) point.ClauseTitle = body.ClauseTitle.Trim();
        if (body.DesignStatus != null) point.DesignStatus = body.DesignStatus.Trim();
        if (body.OperatingStatus != null) point.OperatingStatus = body.OperatingStatus.Trim();
        if (body.OverallStatus != null) point.OverallStatus = body.OverallStatus.Trim();
        if (body.Confidence.HasValue) point.Confidence = body.Confidence.Value;
        if (body.Interpretation != null) point.Interpretation = body.Interpretation;
        if (body.PolicyExtract != null)
            point.PolicyExtractJson = JsonSerializer.Serialize(body.PolicyExtract);
        if (body.DocumentReference != null) point.DocumentReference = body.DocumentReference;
        if (body.GapDescription != null) point.GapDescription = body.GapDescription;
        if (body.SuggestedAction != null) point.SuggestedAction = body.SuggestedAction;
        if (body.GapDirection != null) point.GapDirection = body.GapDirection;
        if (body.SortOrder.HasValue) point.SortOrder = body.SortOrder.Value;
        return point;
    }

    private static object MapTemplate(NdDemoAnalysisTemplate t) => new
    {
        t.Id,
        t.Code,
        t.Name,
        t.Description,
        t.RegulationNameHint,
        t.InternalNameHint,
        t.IsActive,
        t.SortOrder,
        t.CreatedAt,
        t.UpdatedAt,
        points = t.Points.OrderBy(p => p.SortOrder).Select(MapPoint).ToList(),
    };

    private static object MapPoint(NdDemoAnalysisTemplatePoint p)
    {
        List<string> extracts = [];
        try
        {
            extracts = JsonSerializer.Deserialize<List<string>>(p.PolicyExtractJson ?? "[]") ?? [];
        }
        catch { /* ignore */ }

        return new
        {
            p.Id,
            p.TemplateId,
            p.ClauseNo,
            p.ClauseTitle,
            p.DesignStatus,
            p.OperatingStatus,
            p.OverallStatus,
            p.Confidence,
            p.Interpretation,
            policyExtract = extracts,
            p.DocumentReference,
            p.GapDescription,
            p.SuggestedAction,
            p.GapDirection,
            p.SortOrder,
            p.UpdatedAt,
        };
    }
}
