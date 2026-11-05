using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/users")]
public class UsersController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record UpdateUserRequest(string? FullName, string? Role, bool? IsActive, Guid? DepartmentId, List<Guid>? DepartmentIds = null);
    public record InviteUserRequest(string FullName, string Email, string Role, string? Password, Guid? DepartmentId);
    public record SetPasswordRequest(string Password);

    private const int MinPasswordLength = 6;

    [HttpPost("{id:guid}/set-password")]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetPasswordRequest body, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var workspaceError = await GuardWorkspaceMemberAsync(admin, id, ct);
        if (workspaceError != null) return workspaceError;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var password = body.Password?.Trim();
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
            return BadRequest(new { success = false, message = $"Password must be at least {MinPasswordLength} characters." });

        var profile = await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile == null)
            return NotFound(new { success = false, message = "User not found." });
        // One login across every business, so another business's admin must not be able to change it.
        if (profile.TenantId != CurrentWorkspaceId && !IsPlatformAdmin(admin))
            return StatusCode(403, new { success = false, message = "This user's password is managed by their home business." });

        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return BadRequest(new { success = false, message = "Supabase admin API not configured." });

        var client = CreateAdminClient(opts);
        var payload = new { password, email_confirm = true };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await client.PutAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users/{id}", content, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            return BadRequest(new { success = false, message = ParseSupabaseError(err) });
        }

        return Ok(new { success = true, message = "Password updated." });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (viewer, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var authById = await FetchAuthUsersAsync(ct);
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var workspaceId = CurrentWorkspaceId;
        var users = await NdWorkspaceMembers.ProfilesInWorkspaceAsync(db, workspaceId, ct);
        users = NdDemoDataFilters.FilterProfilesForUserManagement(
            users,
            demoCtx,
            authById.ToDictionary(kv => kv.Key, kv => kv.Value.Email));

        var departmentNames = await db.NdDepartments.AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var memberships = (await db.NdProfileDepartments.AsNoTracking()
                .Select(l => new { l.ProfileId, l.DepartmentId })
                .ToListAsync(ct))
            .GroupBy(l => l.ProfileId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.DepartmentId).ToList());

        // Which businesses each user is in: for the platform super admin only, as a business admin must not
        // learn the names of other clients.
        Dictionary<Guid, List<object>>? businessesByUser = null;
        if (IsPlatformAdmin(viewer) && !demoCtx.ViewerIsDemo)
        {
            var ids = users.Select(u => u.Id).ToList();
            var names = await db.NdWorkspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
            var homes = await db.NdProfiles.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.TenantId, p.Role })
                .ToListAsync(ct);
            var others = await db.NdWorkspaceMembers.AsNoTracking().Where(m => ids.Contains(m.ProfileId)).ToListAsync(ct);
            businessesByUser = homes.ToDictionary(h => h.Id, h =>
            {
                var list = new List<object>
                {
                    new { id = h.TenantId, name = h.TenantId is Guid t ? names.GetValueOrDefault(t) : null, role = h.Role, isHome = true },
                };
                list.AddRange(others.Where(o => o.ProfileId == h.Id).Select(o =>
                    (object)new { id = (Guid?)o.WorkspaceId, name = names.GetValueOrDefault(o.WorkspaceId), role = o.Role, isHome = false }));
                return list;
            });
        }

        return Ok(new
        {
            success = true,
            data = users.Select(u =>
            {
                authById.TryGetValue(u.Id, out var auth);
                return new
                {
                    id = u.Id,
                    fullName = u.FullName,
                    email = auth?.Email,
                    role = u.Role,
                    isPlatformAdmin = IsPlatformAdmin(u),
                    departmentId = u.DepartmentId,
                    departmentName = u.DepartmentId is Guid did && departmentNames.TryGetValue(did, out var dn) ? dn : null,
                    departmentIds = memberships.GetValueOrDefault(u.Id, [])
                        .Concat(u.DepartmentId is Guid mainId ? [mainId] : Array.Empty<Guid>())
                        .Distinct()
                        .ToList(),
                    isActive = u.IsActive,
                    accountStatus = ResolveAccountStatus(u, auth),
                    isGuest = u.TenantId != workspaceId,
                    businesses = businessesByUser?.GetValueOrDefault(u.Id),
                    invitedAt = auth?.InvitedAt,
                    lastSignInAt = auth?.LastSignInAt,
                    emailConfirmedAt = auth?.EmailConfirmedAt,
                    createdAt = u.CreatedAt,
                };
            }),
        });
    }

    internal sealed record AuthUserInfo(
        string? Email,
        DateTimeOffset? InvitedAt,
        DateTimeOffset? LastSignInAt,
        DateTimeOffset? EmailConfirmedAt);

    internal static string ResolveAccountStatus(NdProfile profile, AuthUserInfo? auth)
    {
        if (!profile.IsActive) return "deactivated";
        if (auth == null) return "active";
        if (auth.EmailConfirmedAt.HasValue) return "active";
        if (auth.InvitedAt.HasValue && !auth.LastSignInAt.HasValue) return "pending_invitation";
        if (!auth.EmailConfirmedAt.HasValue && !auth.LastSignInAt.HasValue) return "pending_invitation";
        return "active";
    }

    private Task<Dictionary<Guid, AuthUserInfo>> FetchAuthUsersAsync(CancellationToken ct) =>
        FetchAuthUsersAsync(httpClientFactory, jwtOptions.Value, ct);

    internal static async Task<Dictionary<Guid, AuthUserInfo>> FetchAuthUsersAsync(
        IHttpClientFactory httpClientFactory,
        SupabaseJwtOptions opts,
        CancellationToken ct)
    {
        var map = new Dictionary<Guid, AuthUserInfo>();
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return map;

        var client = CreateAdminClient(httpClientFactory, opts);
        var res = await client.GetAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users?per_page=1000", ct);
        if (!res.IsSuccessStatusCode) return map;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("users", out var usersEl) || usersEl.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var u in usersEl.EnumerateArray())
        {
            if (!u.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
                continue;

            map[id] = new AuthUserInfo(
                u.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null,
                ParseTs(u, "invited_at"),
                ParseTs(u, "last_sign_in_at"),
                ParseTs(u, "email_confirmed_at"));
        }

        return map;
    }

    private static DateTimeOffset? ParseTs(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : DateTimeOffset.Parse(s);
    }

    private HttpClient CreateAdminClient(SupabaseJwtOptions opts) => CreateAdminClient(httpClientFactory, opts);

    private static HttpClient CreateAdminClient(IHttpClientFactory httpClientFactory, SupabaseJwtOptions opts)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);
        return client;
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest body, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var workspaceError = await GuardWorkspaceMemberAsync(admin, id, ct);
        if (workspaceError != null) return workspaceError;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });

        var workspaceId = CurrentWorkspaceId;
        var member = user.TenantId == workspaceId
            ? null
            : await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == id && m.WorkspaceId == workspaceId, ct);
        // Name and main department belong to the home workspace; a member only has a role and state here.
        if (member == null && !string.IsNullOrWhiteSpace(body.FullName)) user.FullName = body.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            var role = NormalizeManagedRole(body.Role);
            if (role == null)
                return BadRequest(new { success = false, message = "Unknown role." });
            if (admin!.Id == id && role != admin.Role)
                return BadRequest(new { success = false, message = "You cannot change your own role." });
            if (member != null) member.Role = role;
            else user.Role = role;
        }
        if (body.IsActive.HasValue)
        {
            if (member != null) member.IsActive = body.IsActive.Value;
            else user.IsActive = body.IsActive.Value;
        }
        // Guid.Empty is how the picker clears a department, since a null field means "unchanged".
        if (member == null && body.DepartmentId.HasValue)
            user.DepartmentId = body.DepartmentId.Value == Guid.Empty ? null : body.DepartmentId.Value;
        if (body.DepartmentIds != null)
        {
            var wanted = body.DepartmentIds.Distinct().ToList();
            var known = await db.NdDepartments.AsNoTracking().CountAsync(d => wanted.Contains(d.Id), ct);
            if (known != wanted.Count)
                return BadRequest(new { success = false, message = "One or more departments were not found." });
            await NdDepartmentAccess.SetUserDepartmentsAsync(db, user, wanted, ct);
        }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, id);
        return Ok(new { success = true, data = user });
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var workspaceError = await GuardWorkspaceMemberAsync(admin, id, ct);
        if (workspaceError != null) return workspaceError;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });
        await SetActiveHereAsync(user, false, ct);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, id);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var workspaceError = await GuardWorkspaceMemberAsync(admin, id, ct);
        if (workspaceError != null) return workspaceError;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });
        await SetActiveHereAsync(user, true, ct);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateAuthProfile(memoryCache, id);
        return Ok(new { success = true });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var workspaceError = await GuardWorkspaceMemberAsync(admin, id, ct);
        if (workspaceError != null) return workspaceError;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        if (admin!.Id == id)
            return BadRequest(new { success = false, message = "You cannot delete your own account." });

        // A user who belongs elsewhere is only removed from this business; the login stays.
        var workspaceId = CurrentWorkspaceId;
        var profile = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile != null && profile.TenantId != workspaceId)
        {
            var member = await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == id && m.WorkspaceId == workspaceId, ct);
            if (member != null) db.NdWorkspaceMembers.Remove(member);
            if (profile.ActiveTenantId == workspaceId) profile.ActiveTenantId = null;
            await db.SaveChangesAsync(ct);
            InvalidateAuthProfile(memoryCache, id);
            return Ok(new { success = true, deleted = true, removedFromWorkspaceOnly = true });
        }

        if (await db.NdWorkspaceMembers.AsNoTracking().AnyAsync(m => m.ProfileId == id, ct))
            return BadRequest(new { success = false, message = "This user also belongs to other businesses. Remove them from those first." });

        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return BadRequest(new { success = false, message = "Supabase admin API not configured." });

        // The profile goes first: if saved work still points at it the delete is refused, and the login must
        // survive, otherwise the user would stay listed with no way to sign in.
        var client = CreateAdminClient(opts);
        var strategy = db.Database.CreateExecutionStrategy();
        IActionResult result = Ok(new { success = true, deleted = true });
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (profile != null)
            {
                db.NdProfiles.Remove(profile);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    db.Entry(profile).State = EntityState.Unchanged;
                    result = BadRequest(new { success = false, message = "This user has saved work that needs them, so they cannot be deleted. Deactivate them instead." });
                    return;
                }
            }

            var delRes = await client.DeleteAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users/{id}", ct);
            if (!delRes.IsSuccessStatusCode && delRes.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await delRes.Content.ReadAsStringAsync(ct);
                result = BadRequest(new { success = false, message = body });
                return;
            }
            await transaction.CommitAsync(ct);
        });

        InvalidateAuthProfile(memoryCache, id);
        demoDirectory.InvalidateProfileCache();
        return result;
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest body, CancellationToken ct)
    {
        var (admin, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var email = body.Email?.Trim();
        var fullName = body.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName))
            return BadRequest(new { success = false, message = "Full name and email are required." });

        var inviteIsolationError = GuardInviteEmail(demoCtx, email);
        if (inviteIsolationError != null) return inviteIsolationError;

        var inviteRole = NormalizeManagedRole(body.Role);
        if (inviteRole == null)
            return BadRequest(new { success = false, message = "Unknown role." });

        // The email already has a login: it is the same person, so give them access to this business too.
        var authUsers = await FetchAuthUsersAsync(ct);
        var existingId = authUsers.FirstOrDefault(a => string.Equals(a.Value.Email, email, StringComparison.OrdinalIgnoreCase)).Key;
        var existingProfile = existingId == Guid.Empty ? null : await db.NdProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == existingId, ct);
        if (existingProfile != null)
        {
            if (existingProfile.TenantId == CurrentWorkspaceId)
                return BadRequest(new { success = false, message = "This user is already in this business." });
            if (existingProfile.IsPlatformAdmin)
                return BadRequest(new { success = false, message = "A platform super admin already has access to every business." });
            await NdWorkspaceMembers.AddOrUpdateAsync(db, existingProfile, CurrentWorkspaceId, inviteRole, admin!.Id, ct);
            await db.SaveChangesAsync(ct);
            InvalidateAuthProfile(memoryCache, existingProfile.Id);
            return Ok(new { success = true, message = "This email already has an account, so it was added to this business. They keep their current password.", data = new { id = existingProfile.Id } });
        }

        var (profileId, message, inviteError) = await CreateWorkspaceUserAsync(
            db, jwtOptions.Value, httpClientFactory, memoryCache, demoDirectory,
            admin!.Id, CurrentWorkspaceId, fullName, email, inviteRole, body.Password, body.DepartmentId, ct);
        if (inviteError != null) return BadRequest(new { success = false, message = inviteError });
        return Ok(new { success = true, message, data = new { id = profileId } });
    }

    /// <summary>
    /// Creates the Supabase auth user and its profile in <paramref name="workspaceId"/>. Shared by user
    /// invites and by workspace creation (first workspace admin).
    /// </summary>
    internal static async Task<(Guid Id, string? Message, string? Error)> CreateWorkspaceUserAsync(
        AppDbContext db,
        SupabaseJwtOptions opts,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        NdDemoUserDirectory demoDirectory,
        Guid createdBy,
        Guid workspaceId,
        string fullName,
        string email,
        string role,
        string? rawPassword,
        Guid? departmentId,
        CancellationToken ct)
    {
        var password = rawPassword?.Trim();
        if (!string.IsNullOrWhiteSpace(password) && password.Length < MinPasswordLength)
            return (Guid.Empty, null, $"Password must be at least {MinPasswordLength} characters.");

        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return (Guid.Empty, null, "Supabase admin API not configured.");

        var client = CreateAdminClient(httpClientFactory, opts);
        var createPayload = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["email_confirm"] = true,
            ["user_metadata"] = new { full_name = fullName, role },
        };
        if (!string.IsNullOrWhiteSpace(password))
            createPayload["password"] = password;

        var content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");
        var res = await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users", content, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return (Guid.Empty, null, ParseSupabaseError(responseBody));

        using var doc = JsonDocument.Parse(responseBody);
        var userId = doc.RootElement.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var uid)
            ? uid
            : Guid.Empty;

        if (userId != Guid.Empty)
        {
            var existing = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == userId, ct);
            if (existing == null)
            {
                db.NdProfiles.Add(new NdProfile
                {
                    Id = userId,
                    FullName = fullName,
                    Role = role,
                    DepartmentId = departmentId == Guid.Empty ? null : departmentId,
                    CreatedBy = createdBy,
                    IsActive = true,
                    TenantId = workspaceId,
                });
            }
            else
            {
                existing.FullName = fullName;
                existing.Role = role;
                existing.TenantId = workspaceId;
                if (departmentId.HasValue)
                    existing.DepartmentId = departmentId.Value == Guid.Empty ? null : departmentId.Value;
            }
            await db.SaveChangesAsync(ct);
            InvalidateAuthProfile(memoryCache, userId);
            demoDirectory.InvalidateProfileCache();
        }

        var message = string.IsNullOrWhiteSpace(password)
            ? "User created (email pre-confirmed). Set a password or share login credentials."
            : "User created and ready to sign in (no email verification required).";
        return (userId, message, null);
    }

    private static Guid CurrentWorkspaceId => WorkspaceScope.CurrentWorkspaceId ?? WorkspaceScope.DefaultWorkspaceId;

    private static readonly HashSet<string> ManagedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "super_admin", "maker", "checker", "reviewer",
    };

    /// <summary>"admin" is accepted as the workspace-admin role name and stored as super_admin.</summary>
    internal static string? NormalizeManagedRole(string? raw)
    {
        var r = raw?.Trim().ToLowerInvariant();
        if (r == "admin") return "super_admin";
        return r != null && ManagedRoles.Contains(r) ? r : null;
    }

    /// <summary>
    /// Admins only manage users of the workspace they are acting in, and a workspace admin can never
    /// touch the platform super admin account.
    /// </summary>
    private async Task<IActionResult?> GuardWorkspaceMemberAsync(NdProfile admin, Guid targetId, CancellationToken ct)
    {
        var target = await db.NdProfiles.AsNoTracking()
            .Where(p => p.Id == targetId)
            .Select(p => new { p.TenantId, p.IsPlatformAdmin, p.Role })
            .FirstOrDefaultAsync(ct);
        var workspaceId = CurrentWorkspaceId;
        if (target == null
            || (target.TenantId != workspaceId
                && !await db.NdWorkspaceMembers.AsNoTracking().AnyAsync(m => m.ProfileId == targetId && m.WorkspaceId == workspaceId, ct)))
            return NotFound(new { success = false, message = "User not found." });
        if (target.IsPlatformAdmin && target.Role == "super_admin" && !IsPlatformAdmin(admin))
            return StatusCode(403, new { success = false, message = "Only the platform super admin can change this account." });
        return null;
    }

    /// <summary>Home users change on the profile; a member from another business changes here only.</summary>
    private async Task SetActiveHereAsync(NdProfile user, bool isActive, CancellationToken ct)
    {
        var workspaceId = CurrentWorkspaceId;
        if (user.TenantId == workspaceId)
        {
            user.IsActive = isActive;
            return;
        }
        var member = await db.NdWorkspaceMembers.FirstOrDefaultAsync(m => m.ProfileId == user.Id && m.WorkspaceId == workspaceId, ct);
        if (member != null) member.IsActive = isActive;
    }

    private static string ParseSupabaseError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Request failed.";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                var msg = msgEl.GetString() ?? raw;
                if (root.TryGetProperty("error_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                {
                    var code = codeEl.GetString();
                    if (code == "email_address_invalid")
                        return $"{msg} Try a different address (e.g. name@reguliq.com) or check Supabase auth email settings.";
                    if (code == "email_exists")
                        return "A user with this email already exists.";
                }
                return msg;
            }
            if (root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
                return messageEl.GetString() ?? raw;
        }
        catch
        {
            // fall through
        }
        return raw.Length > 240 ? raw[..240] + "…" : raw;
    }

    private static IActionResult? GuardProfileAccess(NdDemoIsolationContext demoCtx, string? email)
    {
        if (!demoCtx.Enabled) return null;
        if (!demoCtx.ViewerIsDemo) return null;
        if (!NdDemoDataFilters.CanAccessProfileEmail(email, demoCtx))
            return new NotFoundObjectResult(new { success = false, message = "Not found" });
        return null;
    }

    private static IActionResult? GuardInviteEmail(NdDemoIsolationContext demoCtx, string email)
    {
        if (!demoCtx.Enabled) return null;
        var isDemoEmail = NdDemoIsolationHelper.IsDemoEmail(email);
        if (demoCtx.ViewerIsDemo && !isDemoEmail)
            return new ObjectResult(new { success = false, message = "Demo administrators can only invite users whose email contains \"demo\"." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        return null;
    }
}
