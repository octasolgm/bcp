using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/departments")]
public class DepartmentsController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    NdDemoUserDirectory demoDirectory,
    IWebHostEnvironment env,
    NdDashboardCacheService dashboardCache,
    IMemoryCache cache) : NdControllerBase
{
    /// <summary>Departments nest at most this deep (e.g. Compliance › AML/CFT › Sanctions Screening).</summary>
    public const int MaxDepth = 3;
    private const string SampleFile = "departments-bank-sample.json";

    public record DepartmentRequest(string Name, string? Description, bool? IsActive, Guid? ParentId = null);

    public record AddUsersRequest(List<Guid>? UserIds);

    public record AddDocumentsRequest(List<Guid>? DocumentIds);

    private static Guid CurrentWorkspaceId => WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;

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

        var memberCountMap = new Dictionary<Guid, int>();
        try
        {
            var workspaceId = CurrentWorkspaceId;
            var main = await db.NdProfiles.AsNoTracking()
                .Where(p => p.TenantId == workspaceId && p.DepartmentId != null)
                .Select(p => new { p.Id, DepartmentId = p.DepartmentId!.Value })
                .ToListAsync(ct);
            var linked = await db.NdProfileDepartments.AsNoTracking()
                .Select(l => new { Id = l.ProfileId, l.DepartmentId })
                .ToListAsync(ct);
            foreach (var g in main.Concat(linked).Distinct().GroupBy(m => m.DepartmentId))
                memberCountMap[g.Key] = g.Count();
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
                memberCount = memberCountMap.GetValueOrDefault(d.Id),
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

    /// <summary>Adds users to a department, unit or team; what they already belong to stays.</summary>
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMembers(Guid id, [FromBody] AddUsersRequest body, CancellationToken ct)
    {
        var guard = await GuardManageAsync(id, ct);
        if (guard != null) return guard;

        var workspaceId = CurrentWorkspaceId;
        var wanted = (body.UserIds ?? []).Distinct().ToList();
        var users = await db.NdProfiles
            .Where(p => wanted.Contains(p.Id)
                && (p.TenantId == workspaceId || db.NdWorkspaceMembers.Any(m => m.ProfileId == p.Id && m.WorkspaceId == workspaceId)))
            .ToListAsync(ct);
        if (users.Count != wanted.Count)
            return BadRequest(new { success = false, message = "One or more users were not found in this workspace." });

        foreach (var user in users)
        {
            var departments = await NdDepartmentAccess.UserDepartmentIdsAsync(db, user, ct);
            if (!departments.Contains(id)) departments.Add(id);
            await NdDepartmentAccess.SetUserDepartmentsAsync(db, user, departments, ct);
        }
        await db.SaveChangesAsync(ct);
        foreach (var user in users) InvalidateAuthProfile(cache, user.Id);
        dashboardCache.Invalidate();
        return Ok(new { success = true, added = users.Count });
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var guard = await GuardManageAsync(id, ct);
        if (guard != null) return guard;

        var workspaceId = CurrentWorkspaceId;
        var user = await db.NdProfiles.FirstOrDefaultAsync(
            p => p.Id == userId
                && (p.TenantId == workspaceId || db.NdWorkspaceMembers.Any(m => m.ProfileId == p.Id && m.WorkspaceId == workspaceId)),
            ct);
        if (user == null) return NotFound(new { success = false, message = "User not found." });

        var departments = await NdDepartmentAccess.UserDepartmentIdsAsync(db, user, ct);
        departments.Remove(id);
        await NdDepartmentAccess.SetUserDepartmentsAsync(db, user, departments, ct);
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(cache, user.Id);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    /// <summary>Files internal documents under a department, unit or team; their other departments stay.</summary>
    [HttpPost("{id:guid}/documents")]
    public async Task<IActionResult> AddDocuments(Guid id, [FromBody] AddDocumentsRequest body, CancellationToken ct)
    {
        var guard = await GuardManageAsync(id, ct);
        if (guard != null) return guard;

        var wanted = (body.DocumentIds ?? []).Distinct().ToList();
        var known = await db.StoredDocuments.AsNoTracking()
            .CountAsync(d => wanted.Contains(d.Id) && (d.DocKind == "document" || d.DocKind == "internal"), ct);
        if (known != wanted.Count)
            return BadRequest(new { success = false, message = "One or more documents were not found." });

        var existing = await db.NdInternalDocumentDepartments.AsNoTracking()
            .Where(l => l.DepartmentId == id && wanted.Contains(l.StoredDocumentId))
            .Select(l => l.StoredDocumentId)
            .ToListAsync(ct);
        foreach (var documentId in wanted.Except(existing))
            db.NdInternalDocumentDepartments.Add(new NdInternalDocumentDepartment { StoredDocumentId = documentId, DepartmentId = id });
        await db.SaveChangesAsync(ct);
        foreach (var documentId in wanted) NdDepartmentAccessFilter.Forget(cache, documentId);
        dashboardCache.Invalidate();
        return Ok(new { success = true, added = wanted.Count - existing.Count });
    }

    [HttpDelete("{id:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> RemoveDocument(Guid id, Guid documentId, CancellationToken ct)
    {
        var guard = await GuardManageAsync(id, ct);
        if (guard != null) return guard;

        var link = await db.NdInternalDocumentDepartments
            .FirstOrDefaultAsync(l => l.DepartmentId == id && l.StoredDocumentId == documentId, ct);
        if (link != null)
        {
            db.NdInternalDocumentDepartments.Remove(link);
            await db.SaveChangesAsync(ct);
        }
        NdDepartmentAccessFilter.Forget(cache, documentId);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    /// <summary>Workspace admins of real accounts only, and the department must be in this workspace.</summary>
    private async Task<IActionResult?> GuardManageAsync(Guid id, CancellationToken ct)
    {
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo)
            return StatusCode(403, new { success = false, message = "Not available on demo accounts." });

        if (!await db.NdDepartments.AsNoTracking().AnyAsync(d => d.Id == id, ct))
            return NotFound(new { success = false, message = "Department not found." });
        return null;
    }

    /// <summary>
    /// Adds the typical bank departments (three levels) that this workspace does not have yet. A department counts as
    /// already there when the same name sits under the same parent, so adding again creates no duplicates and
    /// existing departments are left exactly as they are.
    /// </summary>
    [HttpPost("import-sample")]
    public async Task<IActionResult> ImportSample(CancellationToken ct)
    {
        var (profile, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        var existing = await NdDemoDataFilters.ApplyToDepartments(db.NdDepartments.AsNoTracking(), demoCtx)
            .Select(d => new { d.Id, d.ParentId, d.Name })
            .ToListAsync(ct);
        static string Key(Guid? parentId, string name) => $"{parentId}|{name.Trim()}";
        var existingIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in existing) existingIds.TryAdd(Key(d.ParentId, d.Name), d.Id);

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
                var key = Key(parentId, name);
                if (!existingIds.TryGetValue(key, out var id))
                {
                    var row = new NdDepartment { Name = name, ParentId = parentId, CreatedBy = profile!.Id };
                    added.Add(row);
                    id = row.Id;
                    existingIds[key] = id;
                }
                AddLevel(node.Children, id, depth + 1);
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
