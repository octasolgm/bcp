using System.Text.Json;
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
    NdDemoUserDirectory demoDirectory,
    IWebHostEnvironment env) : NdControllerBase
{
    /// <summary>Departments nest at most this deep (e.g. Compliance › AML/CFT › Sanctions Screening).</summary>
    public const int MaxDepth = 3;
    private const string SampleFile = "departments-bank-sample.json";

    public record DepartmentRequest(string Name, string? Description, bool? IsActive, Guid? ParentId = null);

    private sealed record SampleNode(string Name, List<SampleNode>? Children);

    private sealed record SamplePack(List<SampleNode> Departments);

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

        var internalCountMap = new Dictionary<Guid, int>();
        try
        {
            var internalCounts = await db.NdInternalDocumentDepartments.AsNoTracking()
                .Where(l => db.StoredDocuments.Any(s => s.Id == l.StoredDocumentId && !s.IsHidden))
                .GroupBy(l => l.DepartmentId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var c in internalCounts) internalCountMap[c.Key] = c.Count;
        }
        catch { /* table may not exist before bootstrap */ }

        return Ok(new
        {
            success = true,
            data = rows.Select(d => new
            {
                id = d.Id,
                parentId = d.ParentId,
                internalDocumentCount = internalCountMap.GetValueOrDefault(d.Id),
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

        var name = body.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { success = false, message = "Name is required." });
        if (body.ParentId is Guid parentId)
        {
            var depth = 1;
            Guid? cursor = parentId;
            while (cursor is Guid id)
            {
                var parent = await db.NdDepartments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
                if (parent == null) return NotFound(new { success = false, message = "Parent department not found." });
                cursor = parent.ParentId;
                depth++;
            }
            if (depth > MaxDepth)
                return BadRequest(new { success = false, message = $"Departments go at most {MaxDepth} levels deep." });
        }

        var row = new NdDepartment
        {
            Name = name,
            Description = body.Description,
            ParentId = body.ParentId,
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

        // Sub-departments go with it (the parent link cascades in the database).
        db.NdDepartments.Remove(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, deleted = true });
    }

    /// <summary>Adds typical bank departments (three levels) to a workspace that has none yet.</summary>
    [HttpPost("import-sample")]
    public async Task<IActionResult> ImportSample(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (await NdDemoDataFilters.ApplyToDepartments(db.NdDepartments.AsNoTracking(), demoCtx).AnyAsync(ct))
            return Conflict(new { success = false, message = "This workspace already has departments." });

        var path = Path.Combine(env.ContentRootPath, "SeedData", SampleFile);
        if (!System.IO.File.Exists(path))
            return StatusCode(500, new { success = false, message = "Sample departments are not available." });
        var pack = JsonSerializer.Deserialize<SamplePack>(
            await System.IO.File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var added = new List<NdDepartment>();
        void AddLevel(IEnumerable<SampleNode>? nodes, Guid? parentId, int depth)
        {
            if (nodes == null || depth > MaxDepth) return;
            foreach (var node in nodes)
            {
                var name = node.Name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var row = new NdDepartment { Name = name, ParentId = parentId, CreatedBy = profile!.Id };
                added.Add(row);
                AddLevel(node.Children, row.Id, depth + 1);
            }
        }
        AddLevel(pack?.Departments, null, 1);

        // Parents first, so every row's parent already exists.
        foreach (var row in added)
        {
            db.NdDepartments.Add(row);
            await db.SaveChangesAsync(ct);
        }
        return Ok(new { success = true, imported = added.Count });
    }
}
