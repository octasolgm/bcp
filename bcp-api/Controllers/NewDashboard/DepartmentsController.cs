using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/departments")]
public class DepartmentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record DepartmentRequest(string Name, string? Description, bool? IsActive);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct,
            "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var rows = new List<NdDepartment>();
        try
        {
            rows = await NdDemoDataFilters.ApplyToDepartments(
                    db.NdDepartments.AsNoTracking(), demoCtx)
                .OrderBy(d => d.Name)
                .ToListAsync(ct);
        }
        catch
        {
            return Ok(new { success = true, data = Array.Empty<object>() });
        }

        var docCountMap = new Dictionary<Guid?, int>();
        var libCountMap = new Dictionary<Guid?, int>();
        try
        {
            var docCounts = await NdDemoDataFilters.ApplyToRegulationDocuments(
                    db.NdRegulationDocuments.AsNoTracking(), demoCtx)
                .Where(d => !d.IsManual || d.Points.Any())
                .GroupBy(d => d.DepartmentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in docCounts) docCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist before bootstrap */ }

        try
        {
            var libCounts = await NdDemoDataFilters.ApplyToLibraries(
                    db.NdLibraries.AsNoTracking(), demoCtx)
                .GroupBy(l => l.DepartmentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in libCounts) libCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist before bootstrap */ }

        return Ok(new
        {
            success = true,
            data = rows.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                description = d.Description,
                isActive = d.IsActive,
                documentCount = docCountMap.GetValueOrDefault(d.Id),
                libraryCount = libCountMap.GetValueOrDefault(d.Id),
                createdAt = d.CreatedAt,
            }),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DepartmentRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var row = new NdDepartment
        {
            Name = body.Name.Trim(),
            Description = body.Description,
            CreatedBy = profile!.Id,
        };
        db.NdDepartments.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DepartmentRequest body, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var row = await db.NdDepartments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        if (!NdDemoDataFilters.CanAccessCreatedBy(row.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        row.Name = body.Name.Trim();
        row.Description = body.Description;
        if (body.IsActive.HasValue) row.IsActive = body.IsActive.Value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = row });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var row = await db.NdDepartments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (row == null) return NotFound(new { success = false, message = "Not found" });

        if (!NdDemoDataFilters.CanAccessCreatedBy(row.CreatedBy, demoCtx))
            return NotFound(new { success = false, message = "Not found" });

        db.NdDepartments.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true });
    }
}
