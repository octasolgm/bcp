using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

/// <summary>
/// Client workspaces (one per bank). Only the platform super admin manages them; everyone else can
/// only read which workspace they are in. Demo accounts never reach workspace management.
/// </summary>
[ApiController]
[Route("nd/workspaces")]
public partial class WorkspacesController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    NdDashboardCacheService dashboardCache,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record WorkspaceAdminInput(string FullName, string Email, string? Password);
    public record CreateWorkspaceRequest(string Name, string? Slug, string? Description, WorkspaceAdminInput? Admin);
    public record UpdateWorkspaceRequest(string? Name, string? Description, bool? IsActive);

    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var id = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        var ws = await db.NdWorkspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                workspace = ws == null ? null : MapWorkspace(ws),
                homeWorkspaceId = profile.TenantId,
                isPlatformAdmin = IsPlatformAdmin(profile),
                isSwitched = profile.TenantId != id,
            },
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (_, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var workspaces = await db.NdWorkspaces.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);

        // Counts span every workspace, so they bypass the request's tenant filter on purpose.
        var users = await db.NdProfiles.AsNoTracking()
            .GroupBy(p => p.TenantId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? Guid.Empty, x => x.Count, ct);
        var docs = await db.StoredDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => !d.IsHidden)
            .GroupBy(d => d.TenantId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? Guid.Empty, x => x.Count, ct);
        var runs = await db.NdAnalysisRuns.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Status != "deleted")
            .GroupBy(r => r.TenantId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? Guid.Empty, x => x.Count, ct);
        var admins = await db.NdProfiles.AsNoTracking()
            .Where(p => p.Role == "super_admin" && !p.IsPlatformAdmin)
            .Select(p => new { p.Id, p.FullName, p.TenantId })
            .ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = workspaces.Select(w => new
            {
                workspace = MapWorkspace(w),
                userCount = users.GetValueOrDefault(w.Id),
                documentCount = docs.GetValueOrDefault(w.Id),
                analysisCount = runs.GetValueOrDefault(w.Id),
                admins = admins.Where(a => a.TenantId == w.Id).Select(a => new { id = a.Id, fullName = a.FullName }),
            }),
            currentWorkspaceId = WorkspaceScope.CurrentWorkspaceId,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceRequest body, CancellationToken ct)
    {
        var (admin, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { success = false, message = "Workspace name is required." });

        var slug = Slugify(string.IsNullOrWhiteSpace(body.Slug) ? name : body.Slug);
        if (slug.Length == 0)
            return BadRequest(new { success = false, message = "Workspace short name must contain letters or numbers." });
        var slugLower = slug.ToLowerInvariant();
        if (await db.NdWorkspaces.AnyAsync(w => w.Slug.ToLower() == slugLower, ct))
            return Conflict(new { success = false, message = $"A workspace with the short name \"{slug}\" already exists." });

        var adminInput = body.Admin;
        if (adminInput != null
            && (string.IsNullOrWhiteSpace(adminInput.FullName) || string.IsNullOrWhiteSpace(adminInput.Email)))
            return BadRequest(new { success = false, message = "Workspace admin needs a full name and email." });

        var workspace = new NdWorkspace
        {
            Name = name,
            Slug = slug,
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            CreatedBy = admin.Id,
        };
        db.NdWorkspaces.Add(workspace);
        await db.SaveChangesAsync(ct);

        object? createdAdmin = null;
        string? adminMessage = null;
        if (adminInput != null)
        {
            var (adminId, message, adminError) = await UsersController.CreateWorkspaceUserAsync(
                db, jwtOptions.Value, httpClientFactory, memoryCache, demoDirectory,
                admin.Id, workspace.Id, adminInput.FullName.Trim(), adminInput.Email.Trim(),
                "super_admin", adminInput.Password, null, ct);
            if (adminError != null)
            {
                // The workspace is kept (it is empty and can be given an admin later from User management),
                // but the caller must know the admin account was not created.
                return Ok(new
                {
                    success = true,
                    data = MapWorkspace(workspace),
                    adminError,
                    message = $"Workspace created, but the admin account could not be created: {adminError}",
                });
            }
            createdAdmin = new { id = adminId, email = adminInput.Email.Trim(), fullName = adminInput.FullName.Trim() };
            adminMessage = message;
        }

        return Ok(new
        {
            success = true,
            data = MapWorkspace(workspace),
            admin = createdAdmin,
            message = adminMessage ?? "Workspace created.",
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkspaceRequest body, CancellationToken ct)
    {
        var (_, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var ws = await db.NdWorkspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        if (!string.IsNullOrWhiteSpace(body.Name)) ws.Name = body.Name.Trim();
        if (body.Description != null)
            ws.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        if (body.IsActive.HasValue)
        {
            if (!body.IsActive.Value && ws.Id == WorkspaceScope.DefaultWorkspaceId)
                return BadRequest(new { success = false, message = "The default workspace cannot be deactivated." });
            ws.IsActive = body.IsActive.Value;
        }
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Members' cached workspace state carries the active flag.
        if (body.IsActive.HasValue)
            await InvalidateMembersAsync(id, ct);

        return Ok(new { success = true, data = MapWorkspace(ws) });
    }

    /// <summary>Platform admin enters a workspace; every page then shows that workspace's data.</summary>
    [HttpPost("{id:guid}/switch")]
    public async Task<IActionResult> Switch(Guid id, CancellationToken ct)
    {
        var (admin, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var ws = await db.NdWorkspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        var profile = await db.NdProfiles.FirstAsync(p => p.Id == admin.Id, ct);
        profile.ActiveTenantId = id == profile.TenantId ? null : id;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, admin.Id);
        dashboardCache.Invalidate();

        return Ok(new { success = true, data = MapWorkspace(ws) });
    }

    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> Users(Guid id, CancellationToken ct)
    {
        var (_, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var users = await db.NdProfiles.AsNoTracking()
            .Where(p => p.TenantId == id)
            .OrderBy(p => p.FullName)
            .Select(p => new { id = p.Id, fullName = p.FullName, role = p.Role, isActive = p.IsActive, isPlatformAdmin = p.IsPlatformAdmin })
            .ToListAsync(ct);
        return Ok(new { success = true, data = users });
    }

    private async Task<(NdProfile Profile, IActionResult? Error)> RequireWorkspaceManagerAsync(CancellationToken ct)
    {
        var (profile, user, error) = await RequirePlatformAdminWithUserAsync(db, jwt, ct);
        if (error != null) return (null!, error);

        // Pre-existing demo admins keep their platform flag for the pages they already had, but client
        // workspaces must never be visible from a demo account.
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);
        if (demoCtx.ViewerIsDemo || NdDemoIsolationHelper.IsDemoName(profile.FullName))
            return (null!, StatusCode(403, new { success = false, message = "Demo accounts cannot manage workspaces." }));

        return (profile, null);
    }

    private async Task InvalidateMembersAsync(Guid workspaceId, CancellationToken ct)
    {
        var ids = await db.NdProfiles.AsNoTracking()
            .Where(p => p.TenantId == workspaceId || p.ActiveTenantId == workspaceId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        foreach (var pid in ids) InvalidateAuthProfile(memoryCache, pid);
    }

    private static object MapWorkspace(NdWorkspace w) => new
    {
        id = w.Id,
        name = w.Name,
        slug = w.Slug,
        description = w.Description,
        isActive = w.IsActive,
        isDefault = w.Id == WorkspaceScope.DefaultWorkspaceId,
        createdAt = w.CreatedAt,
    };

    internal static string Slugify(string raw)
    {
        var s = NonSlugChars().Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
        return s.Length > 48 ? s[..48].Trim('-') : s;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
