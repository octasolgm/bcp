using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>Which departments can see an analysis: none set means all departments. Workspace admins only.</summary>
[ApiController]
[Route("nd/analysis-runs")]
public class AnalysisRunDepartmentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory,
    NdDashboardCacheService dashboardCache,
    IMemoryCache cache) : NdControllerBase
{
    public record SetRunDepartmentsRequest(List<Guid>? DepartmentIds);

    /// <summary>Every analysis limited to specific departments: run id -> department ids.</summary>
    [HttpGet("departments")]
    public async Task<IActionResult> All(CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var links = await db.NdAnalysisRunDepartments.AsNoTracking()
            .Select(l => new { l.AnalysisRunId, l.DepartmentId })
            .ToListAsync(ct);
        var data = links
            .GroupBy(l => l.AnalysisRunId)
            .ToDictionary(g => g.Key.ToString(), g => g.Select(l => l.DepartmentId).ToList());
        return Ok(new { success = true, data });
    }

    [HttpGet("{id:guid}/departments")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var ids = await db.NdAnalysisRunDepartments.AsNoTracking()
            .Where(l => l.AnalysisRunId == id)
            .Select(l => l.DepartmentId)
            .ToListAsync(ct);
        return Ok(new { success = true, data = ids });
    }

    /// <summary>Replaces the departments an analysis is shared with; an empty list shares it with all departments.</summary>
    [HttpPut("{id:guid}/departments")]
    public async Task<IActionResult> Set(Guid id, [FromBody] SetRunDepartmentsRequest body, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo)
            return StatusCode(403, new { success = false, message = "Not available on demo accounts." });

        if (!await db.NdAnalysisRuns.AnyAsync(r => r.Id == id, ct))
            return NotFound(new { success = false, message = "Analysis not found." });

        var wanted = (body.DepartmentIds ?? []).Distinct().ToList();
        var known = await db.NdDepartments.AsNoTracking().CountAsync(d => wanted.Contains(d.Id), ct);
        if (known != wanted.Count)
            return BadRequest(new { success = false, message = "One or more departments were not found." });

        var current = await db.NdAnalysisRunDepartments.Where(l => l.AnalysisRunId == id).ToListAsync(ct);
        db.NdAnalysisRunDepartments.RemoveRange(current.Where(l => !wanted.Contains(l.DepartmentId)));
        foreach (var departmentId in wanted.Where(w => current.All(l => l.DepartmentId != w)))
            db.NdAnalysisRunDepartments.Add(new NdAnalysisRunDepartment { AnalysisRunId = id, DepartmentId = departmentId });
        await db.SaveChangesAsync(ct);

        NdDepartmentAccessFilter.Forget(cache, id);
        dashboardCache.Invalidate();
        return Ok(new { success = true, data = wanted });
    }
}
