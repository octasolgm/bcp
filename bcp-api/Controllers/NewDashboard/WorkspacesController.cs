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

        // Counts span every workspace, so they bypass the request's tenant filter on purpose. Demo accounts are
        // left out, as they are on the User management badge; members from other businesses are counted.
        var demoIds = (await demoDirectory.GetDemoProfileIdsAsync(ct))?.ToList() ?? [];
        var users = await db.NdProfiles.AsNoTracking()
            .Where(p => !demoIds.Contains(p.Id))
            .GroupBy(p => p.TenantId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key ?? Guid.Empty, x => x.Count, ct);
        var memberRows = await db.NdWorkspaceMembers.AsNoTracking().ToListAsync(ct);
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
            .Where(p => p.Role == "super_admin" && !p.IsPlatformAdmin && !demoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.TenantId })
            .ToListAsync(ct);
        var memberNames = await LoadProfileNamesAsync(db, memberRows.Select(m => (Guid?)m.ProfileId), ct);

        return Ok(new
        {
            success = true,
            data = workspaces.Select(w => new
            {
                workspace = MapWorkspace(w),
                userCount = users.GetValueOrDefault(w.Id) + memberRows.Count(m => m.WorkspaceId == w.Id),
                documentCount = docs.GetValueOrDefault(w.Id),
                analysisCount = runs.GetValueOrDefault(w.Id),
                admins = admins.Where(a => a.TenantId == w.Id).Select(a => new { id = a.Id, fullName = a.FullName })
                    .Concat(memberRows.Where(m => m.WorkspaceId == w.Id && m.Role == "super_admin")
                        .Select(m => new { id = m.ProfileId, fullName = memberNames.GetValueOrDefault(m.ProfileId) ?? "" })),
            }),
            currentWorkspaceId = WorkspaceScope.CurrentWorkspaceId,
        });
    }

    /// <summary>Clause limit a new workspace starts with; the platform admin can change or clear it.</summary>
    private const int DefaultClauseLimit = 50;

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
            MaxDocumentMb = NdUploadLimits.DefaultMaxDocumentMb,
            MaxUploadMb = NdUploadLimits.DefaultMaxUploadMb,
            MaxStorageMb = NdUploadLimits.DefaultMaxStorageMb,
            ClauseLimit = DefaultClauseLimit,
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

    public record DateFormatRequest(string? Region);

    /// <summary>Every date convention a workspace may display in. Kept in sync with the frontend's
    /// NdDateFormats map (bcp-web/src/lib/nd/date-format.ts) — add to both together.</summary>
    private static readonly HashSet<string> ValidDateFormatRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "uae", "pakistan", "usa", "uk", "india", "iso",
    };

    /// <summary>Sets (or clears, with null/"default") which country's date convention this workspace's
    /// UI displays dates in. A business admin manages their own workspace's setting; the platform admin
    /// can set any workspace's.</summary>
    [HttpPut("{id:guid}/date-format")]
    public async Task<IActionResult> SetDateFormat(Guid id, [FromBody] DateFormatRequest body, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        // A workspace admin (super_admin without the platform flag) may only set their own workspace's
        // date format, not another business's.
        if (!IsPlatformAdmin(profile) && profile.TenantId != id)
            return StatusCode(403, new { success = false, message = "You can only set your own workspace's date format." });

        var region = string.IsNullOrWhiteSpace(body.Region) || body.Region.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : body.Region.Trim().ToLowerInvariant();
        if (region != null && !ValidDateFormatRegions.Contains(region))
        {
            return BadRequest(new
            {
                success = false,
                message = $"'{body.Region}' isn't a supported date format. Use one of: {string.Join(", ", ValidDateFormatRegions)}, or default.",
            });
        }

        var ws = await db.NdWorkspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        ws.DateFormatRegion = region;
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await InvalidateMembersAsync(id, ct);

        return Ok(new { success = true, data = new { dateFormatRegion = ws.DateFormatRegion } });
    }

    public record ModulesRequest(bool? ActionsEnabled, bool? EmbeddingsEnabled);

    /// <summary>Turns this workspace's optional modules on or off. Actions off only hides corrective actions
    /// in reports; embeddings off stops finalize from generating corrected document copies. Only the platform
    /// super admin can change them; a business admin can see them but not change them.</summary>
    [HttpPut("{id:guid}/modules")]
    public async Task<IActionResult> SetModules(Guid id, [FromBody] ModulesRequest body, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        var ws = await db.NdWorkspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        if (body.ActionsEnabled.HasValue) ws.ActionsEnabled = body.ActionsEnabled.Value;
        if (body.EmbeddingsEnabled.HasValue) ws.EmbeddingsEnabled = body.EmbeddingsEnabled.Value;
        // Embeddings write resolved actions into documents, so they depend on the Actions module:
        // turning Actions off turns Embeddings off, and Embeddings cannot be on without Actions.
        if (!ws.ActionsEnabled)
        {
            if (body.EmbeddingsEnabled == true)
                return BadRequest(new { success = false, message = "Turn on Actions first: embeddings need actions." });
            ws.EmbeddingsEnabled = false;
        }
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await InvalidateMembersAsync(id, ct);

        return Ok(new { success = true, data = new { actionsEnabled = ws.ActionsEnabled, embeddingsEnabled = ws.EmbeddingsEnabled } });
    }

    public record UploadLimitsRequest(int? MaxDocumentMb, int? MaxUploadMb, int? MaxStorageMb);

    /// <summary>Sets a workspace's upload limits in MB (null or 0 = no limit). Platform admin only, so a
    /// business cannot raise its own quota.</summary>
    [HttpPut("{id:guid}/upload-limits")]
    public async Task<IActionResult> SetUploadLimits(Guid id, [FromBody] UploadLimitsRequest body, CancellationToken ct)
    {
        var (_, error) = await RequirePlatformAdminAsync(db, jwt, ct);
        if (error != null) return error;

        static int? Clean(int? mb) => mb is > 0 ? mb : null;
        var doc = Clean(body.MaxDocumentMb);
        var upload = Clean(body.MaxUploadMb);
        var storage = Clean(body.MaxStorageMb);
        if (doc > NdUploadLimits.ServerMaxDocumentMb)
            return BadRequest(new { success = false, message = $"The per-document limit cannot be above {NdUploadLimits.ServerMaxDocumentMb} MB." });
        if (doc != null && upload != null && upload < doc)
            return BadRequest(new { success = false, message = "The per-upload limit cannot be smaller than the per-document limit." });
        if (upload != null && storage != null && storage < upload)
            return BadRequest(new { success = false, message = "The total storage limit cannot be smaller than the per-upload limit." });
        if (doc != null && storage != null && storage < doc)
            return BadRequest(new { success = false, message = "The total storage limit cannot be smaller than the per-document limit." });

        var ws = await db.NdWorkspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        ws.MaxDocumentMb = doc;
        ws.MaxUploadMb = upload;
        ws.MaxStorageMb = storage;
        ws.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await InvalidateMembersAsync(id, ct);

        return Ok(new { success = true, data = await NdUploadLimits.GetAsync(db, id, ct) });
    }

    /// <summary>A workspace's upload limits and how much of its storage is used, for the upload pages and
    /// Business settings.</summary>
    [HttpGet("{id:guid}/upload-limits")]
    public async Task<IActionResult> GetUploadLimits(Guid id, CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;
        if (!IsPlatformAdmin(profile) && profile.TenantId != id)
            return StatusCode(403, new { success = false, message = "You can only view your own workspace's limits." });

        return Ok(new { success = true, data = await NdUploadLimits.GetAsync(db, id, ct) });
    }

    /// <summary>The upload limits of the workspace the caller is working in, checked before every upload.</summary>
    [HttpGet("current/upload-limits")]
    public async Task<IActionResult> GetCurrentUploadLimits(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct, "super_admin", "maker", "checker", "reviewer");
        if (error != null) return error;

        var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
        return Ok(new { success = true, data = await NdUploadLimits.GetAsync(db, workspaceId, ct) });
    }

    /// <summary>Platform admin enters a workspace; every page then shows that workspace's data.</summary>
    [HttpPost("{id:guid}/switch")]
    public async Task<IActionResult> Switch(Guid id, CancellationToken ct)
    {
        var (admin, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var ws = await db.NdWorkspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws == null) return NotFound(new { success = false, message = "Workspace not found." });

        // The platform super admin can enter any business; everyone else only the ones they belong to.
        if (IsPlatformAdmin(admin))
        {
            var (_, managerError) = await RequireWorkspaceManagerAsync(ct);
            if (managerError != null) return managerError;
        }
        else
        {
            var home = await db.NdProfiles.AsNoTracking().Where(p => p.Id == admin.Id).Select(p => p.TenantId).FirstAsync(ct);
            var belongs = id == home
                || (ws.IsActive && await db.NdWorkspaceMembers.AsNoTracking().AnyAsync(m => m.ProfileId == admin.Id && m.WorkspaceId == id && m.IsActive, ct));
            if (!belongs) return StatusCode(403, new { success = false, message = "You do not have access to this business." });
        }

        var profile = await db.NdProfiles.FirstAsync(p => p.Id == admin.Id, ct);
        profile.ActiveTenantId = id == profile.TenantId ? null : id;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, admin.Id);
        dashboardCache.Invalidate();

        return Ok(new { success = true, data = MapWorkspace(ws) });
    }

    public record AddWorkspaceUserRequest(string FullName, string Email, string Role, string? Password);
    public record UpdateWorkspaceUserRequest(string? Role, bool? IsActive);
    public record AddExistingUserRequest(Guid UserId, string? Role);

    /// <summary>The businesses the signed-in user can work in, for the switcher at the top of the page.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (profile, error) = await RequireAuthAsync(db, jwt, ct);
        if (error != null) return error;

        var home = await db.NdProfiles.AsNoTracking().Where(p => p.Id == profile.Id).Select(p => new { p.TenantId, p.Role }).FirstAsync(ct);
        var members = await db.NdWorkspaceMembers.AsNoTracking()
            .Where(m => m.ProfileId == profile.Id && m.IsActive)
            .ToListAsync(ct);
        var ids = members.Select(m => m.WorkspaceId).ToList();
        if (home.TenantId is Guid homeId) ids.Add(homeId);
        var workspaces = await db.NdWorkspaces.AsNoTracking().Where(w => ids.Contains(w.Id) && w.IsActive).OrderBy(w => w.Name).ToListAsync(ct);
        return Ok(new
        {
            success = true,
            data = workspaces.Select(w => new
            {
                id = w.Id,
                name = w.Name,
                isActive = w.IsActive,
                isHome = w.Id == home.TenantId,
                role = w.Id == home.TenantId ? home.Role : members.First(m => m.WorkspaceId == w.Id).Role,
            }),
        });
    }

    /// <summary>Everyone on the platform who can be added to a business, with the businesses they are already in.</summary>
    [HttpGet("all-users")]
    public async Task<IActionResult> AllUsers(CancellationToken ct)
    {
        var (_, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var authById = await UsersController.FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);
        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);
        var names = await db.NdWorkspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var members = await db.NdWorkspaceMembers.AsNoTracking().ToListAsync(ct);
        var users = await db.NdProfiles.AsNoTracking().Where(p => !p.IsPlatformAdmin).OrderBy(p => p.FullName).ToListAsync(ct);

        return Ok(new
        {
            success = true,
            data = users
                .Where(p => !IsDemoAccount(p, authById.GetValueOrDefault(p.Id)?.Email, demoIds))
                .Select(p => new
                {
                    id = p.Id,
                    fullName = p.FullName,
                    email = authById.GetValueOrDefault(p.Id)?.Email,
                    role = p.Role,
                    homeWorkspaceId = p.TenantId,
                    homeWorkspaceName = p.TenantId is Guid t ? names.GetValueOrDefault(t) : null,
                    workspaceIds = members.Where(m => m.ProfileId == p.Id).Select(m => m.WorkspaceId).ToList(),
                }),
        });
    }

    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> Users(Guid id, CancellationToken ct)
    {
        var (manager, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var authById = await UsersController.FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);
        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);
        var names = await db.NdWorkspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var users = await NdWorkspaceMembers.ProfilesInWorkspaceAsync(db, id, ct);

        return Ok(new
        {
            success = true,
            // Only real client users: demo accounts are managed under Demo group.
            data = users.Where(p => !IsDemoAccount(p, authById.GetValueOrDefault(p.Id)?.Email, demoIds)).Select(p =>
            {
                authById.TryGetValue(p.Id, out var auth);
                return new
                {
                    id = p.Id,
                    fullName = p.FullName,
                    email = auth?.Email,
                    role = p.Role,
                    isActive = p.IsActive,
                    isPlatformAdmin = p.IsPlatformAdmin,
                    accountStatus = UsersController.ResolveAccountStatus(p, auth),
                    canEdit = EditRefusal(manager, p, auth?.Email) == null,
                    isGuest = p.TenantId != id,
                    homeWorkspaceName = p.TenantId is Guid t ? names.GetValueOrDefault(t) : null,
                };
            }),
        });
    }

    /// <summary>Creates a user in the chosen business. If the email already has a login, that person is added instead.</summary>
    [HttpPost("{id:guid}/users")]
    public async Task<IActionResult> AddUser(Guid id, [FromBody] AddWorkspaceUserRequest body, CancellationToken ct)
    {
        var (manager, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        if (!await db.NdWorkspaces.AsNoTracking().AnyAsync(w => w.Id == id, ct))
            return NotFound(new { success = false, message = "Workspace not found." });

        var fullName = body.FullName?.Trim() ?? "";
        var email = body.Email?.Trim() ?? "";
        if (fullName.Length == 0 || email.Length == 0)
            return BadRequest(new { success = false, message = "Full name and email are required." });
        if (NdDemoIsolationHelper.IsDemoEmail(email))
            return BadRequest(new { success = false, message = "Demo accounts cannot be added to a business." });

        var role = UsersController.NormalizeManagedRole(body.Role);
        if (role == null) return BadRequest(new { success = false, message = "Unknown role." });

        var authById = await UsersController.FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);
        var existingId = authById.FirstOrDefault(a => string.Equals(a.Value.Email, email, StringComparison.OrdinalIgnoreCase)).Key;
        if (existingId != Guid.Empty && await db.NdProfiles.AsNoTracking().AnyAsync(p => p.Id == existingId, ct))
        {
            var (added, addedError) = await AddExistingAsync(manager, id, existingId, role, ct);
            if (addedError != null) return addedError;
            return Ok(new { success = true, message = $"{added} already has an account, so they were added to this business. They keep their current password.", data = new { id = existingId } });
        }

        var (userId, message, addError) = await UsersController.CreateWorkspaceUserAsync(
            db, jwtOptions.Value, httpClientFactory, memoryCache, demoDirectory,
            manager.Id, id, fullName, email, role, body.Password, null, ct);
        if (addError != null) return BadRequest(new { success = false, message = addError });

        dashboardCache.Invalidate();
        return Ok(new { success = true, message, data = new { id = userId } });
    }

    /// <summary>Gives an existing user access to this business. No role keeps the one from their home workspace.</summary>
    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddExistingUserRequest body, CancellationToken ct)
    {
        var (manager, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        if (!await db.NdWorkspaces.AsNoTracking().AnyAsync(w => w.Id == id, ct))
            return NotFound(new { success = false, message = "Workspace not found." });

        string? role = null;
        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            role = UsersController.NormalizeManagedRole(body.Role);
            if (role == null) return BadRequest(new { success = false, message = "Unknown role." });
        }

        var (added, addedError) = await AddExistingAsync(manager, id, body.UserId, role, ct);
        if (addedError != null) return addedError;
        return Ok(new { success = true, message = $"{added} added to this business.", data = new { id = body.UserId } });
    }

    /// <summary>Removes a user's access to a business that is not their home. Their login and home workspace stay.</summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        var (_, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var member = await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == userId && m.WorkspaceId == id, ct);
        if (member == null) return NotFound(new { success = false, message = "This user is not a member of this business." });
        db.NdWorkspaceMembers.Remove(member);

        var profile = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == userId, ct);
        if (profile?.ActiveTenantId == id) profile.ActiveTenantId = null;
        await db.SaveChangesAsync(ct);

        InvalidateAuthProfile(memoryCache, userId);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    private async Task<(string? Name, IActionResult? Error)> AddExistingAsync(
        NdProfile manager, Guid workspaceId, Guid userId, string? role, CancellationToken ct)
    {
        var profile = await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == userId, ct);
        if (profile == null) return (null, NotFound(new { success = false, message = "User not found." }));
        if (profile.IsPlatformAdmin)
            return (null, BadRequest(new { success = false, message = "A platform super admin already has access to every business." }));
        if (profile.TenantId == workspaceId)
            return (null, BadRequest(new { success = false, message = $"{profile.FullName} is already in this business." }));

        var authById = await UsersController.FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);
        if (IsDemoAccount(profile, authById.GetValueOrDefault(userId)?.Email, await demoDirectory.GetDemoProfileIdsAsync(ct)))
            return (null, BadRequest(new { success = false, message = "Demo accounts cannot be added to a business." }));

        await NdWorkspaceMembers.AddOrUpdateAsync(db, profile, workspaceId, role, manager.Id, ct);
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, userId);
        dashboardCache.Invalidate();
        return (profile.FullName, null);
    }

    private static bool IsDemoAccount(NdProfile profile, string? email, IEnumerable<Guid>? demoIds) =>
        NdDemoIsolationHelper.IsDemoEmail(email)
        || NdDemoIsolationHelper.IsDemoName(profile.FullName)
        || (demoIds != null && demoIds.Contains(profile.Id));

    /// <summary>Changes the role or active state of a user in the chosen business, for that business only.</summary>
    [HttpPut("{id:guid}/users/{userId:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, Guid userId, [FromBody] UpdateWorkspaceUserRequest body, CancellationToken ct)
    {
        var (manager, error) = await RequireWorkspaceManagerAsync(ct);
        if (error != null) return error;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == userId, ct);
        var member = user == null || user.TenantId == id
            ? null
            : await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == userId && m.WorkspaceId == id, ct);
        if (user == null || (user.TenantId != id && member == null))
            return NotFound(new { success = false, message = "User not found in this workspace." });

        var authById = await UsersController.FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);
        var refusal = EditRefusal(manager, user, authById.GetValueOrDefault(userId)?.Email);
        if (refusal != null) return StatusCode(403, new { success = false, message = refusal });

        string? role = null;
        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            role = UsersController.NormalizeManagedRole(body.Role);
            if (role == null) return BadRequest(new { success = false, message = "Unknown role." });
        }

        if (member != null)
        {
            if (role != null) member.Role = role;
            if (body.IsActive.HasValue) member.IsActive = body.IsActive.Value;
            member.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            if (role != null) user.Role = role;
            if (body.IsActive.HasValue) user.IsActive = body.IsActive.Value;
            user.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        InvalidateAuthProfile(memoryCache, userId);
        dashboardCache.Invalidate();
        return Ok(new { success = true });
    }

    /// <summary>Your own account, platform super admins (set in the database only) and demo accounts stay as they are.</summary>
    private static string? EditRefusal(NdProfile manager, NdProfile target, string? targetEmail)
    {
        if (target.Id == manager.Id) return "You cannot change your own account here.";
        if (target.IsPlatformAdmin) return "Platform super admin accounts are managed in the database.";
        if (NdDemoIsolationHelper.IsDemoEmail(targetEmail) || NdDemoIsolationHelper.IsDemoName(target.FullName))
            return "Demo accounts cannot be changed here.";
        return null;
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
            .Where(p => p.TenantId == workspaceId || p.ActiveTenantId == workspaceId
                || db.NdWorkspaceMembers.Any(m => m.ProfileId == p.Id && m.WorkspaceId == workspaceId))
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
        aiCreditLowThresholdPct = w.AiCreditLowThresholdPct,
        aiCreditLimit = w.AiCreditLimit,
        clauseLimit = w.ClauseLimit,
        aiMarkupOverride = w.AiMarkupOverride,
        dateFormatRegion = w.DateFormatRegion,
        actionsEnabled = w.ActionsEnabled,
        embeddingsEnabled = w.EmbeddingsEnabled,
        maxDocumentMb = w.MaxDocumentMb,
        maxUploadMb = w.MaxUploadMb,
        maxStorageMb = w.MaxStorageMb,
    };

    internal static string Slugify(string raw)
    {
        var s = NonSlugChars().Replace(raw.Trim().ToLowerInvariant(), "-").Trim('-');
        return s.Length > 48 ? s[..48].Trim('-') : s;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
