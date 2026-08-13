using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/users")]
public class UsersController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record UpdateUserRequest(string? FullName, string? Role, bool? IsActive);
    public record InviteUserRequest(string FullName, string Email, string Role, string? Password);
    public record SetPasswordRequest(string Password);

    private const int MinPasswordLength = 6;

    [HttpPost("{id:guid}/set-password")]
    public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetPasswordRequest body, CancellationToken ct)
    {
        var (_, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

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
        var (_, user, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var authById = await FetchAuthUsersAsync(ct);
        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, user, ct);

        var users = await db.NdProfiles.AsNoTracking()
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
        users = NdDemoDataFilters.FilterProfilesForUserManagement(
            users,
            demoCtx,
            authById.ToDictionary(kv => kv.Key, kv => kv.Value.Email));

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
                    isActive = u.IsActive,
                    accountStatus = ResolveAccountStatus(u, auth),
                    invitedAt = auth?.InvitedAt,
                    lastSignInAt = auth?.LastSignInAt,
                    emailConfirmedAt = auth?.EmailConfirmedAt,
                    createdAt = u.CreatedAt,
                };
            }),
        });
    }

    private sealed record AuthUserInfo(
        string? Email,
        DateTimeOffset? InvitedAt,
        DateTimeOffset? LastSignInAt,
        DateTimeOffset? EmailConfirmedAt);

    private static string ResolveAccountStatus(NdProfile profile, AuthUserInfo? auth)
    {
        if (!profile.IsActive) return "deactivated";
        if (auth == null) return "active";
        if (auth.EmailConfirmedAt.HasValue) return "active";
        if (auth.InvitedAt.HasValue && !auth.LastSignInAt.HasValue) return "pending_invitation";
        if (!auth.EmailConfirmedAt.HasValue && !auth.LastSignInAt.HasValue) return "pending_invitation";
        return "active";
    }

    private async Task<Dictionary<Guid, AuthUserInfo>> FetchAuthUsersAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, AuthUserInfo>();
        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return map;

        var client = CreateAdminClient(opts);
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

    private HttpClient CreateAdminClient(SupabaseJwtOptions opts)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);
        return client;
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest body, CancellationToken ct)
    {
        var (_, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });

        if (!string.IsNullOrWhiteSpace(body.FullName)) user.FullName = body.FullName.Trim();
        if (!string.IsNullOrWhiteSpace(body.Role)) user.Role = body.Role;
        if (body.IsActive.HasValue) user.IsActive = body.IsActive.Value;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true, data = user });
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var (_, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });
        user.IsActive = false;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var (_, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        var user = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (user == null) return NotFound(new { success = false, message = "Not found" });
        user.IsActive = true;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var (admin, jwtUser, error) = await RequireAuthWithUserAsync(db, jwt, ct, "super_admin");
        if (error != null) return error;

        var demoCtx = await NdDemoIsolationContext.ResolveAsync(demoDirectory, jwtUser, ct);
        var authById = await FetchAuthUsersAsync(ct);
        var profileEmail = authById.TryGetValue(id, out var auth) ? auth.Email : null;
        var isolationError = GuardProfileAccess(demoCtx, profileEmail);
        if (isolationError != null) return isolationError;

        if (admin!.Id == id)
            return BadRequest(new { success = false, message = "You cannot delete your own account." });

        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return BadRequest(new { success = false, message = "Supabase admin API not configured." });

        var client = CreateAdminClient(opts);
        var delRes = await client.DeleteAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users/{id}", ct);
        if (!delRes.IsSuccessStatusCode && delRes.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await delRes.Content.ReadAsStringAsync(ct);
            return BadRequest(new { success = false, message = body });
        }

        var profile = await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile != null)
        {
            db.NdProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
        }

        return Ok(new { success = true, deleted = true });
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

        var password = body.Password?.Trim();
        if (!string.IsNullOrWhiteSpace(password) && password.Length < MinPasswordLength)
            return BadRequest(new { success = false, message = $"Password must be at least {MinPasswordLength} characters." });

        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return BadRequest(new { success = false, message = "Supabase admin API not configured." });

        var client = CreateAdminClient(opts);
        var createPayload = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["email_confirm"] = true,
            ["user_metadata"] = new { full_name = fullName, role = body.Role },
        };
        if (!string.IsNullOrWhiteSpace(password))
            createPayload["password"] = password;

        var content = new StringContent(JsonSerializer.Serialize(createPayload), Encoding.UTF8, "application/json");
        var res = await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/users", content, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return BadRequest(new { success = false, message = ParseSupabaseError(responseBody) });

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
                    Role = body.Role,
                    CreatedBy = admin!.Id,
                    IsActive = true,
                });
            }
            else
            {
                existing.FullName = fullName;
                existing.Role = body.Role;
            }
            await db.SaveChangesAsync(ct);
            demoDirectory.InvalidateProfileCache();
        }

        var message = string.IsNullOrWhiteSpace(password)
            ? "User created (email pre-confirmed). Set a password or share login credentials."
            : "User created and ready to sign in (no email verification required).";
        return Ok(new { success = true, message, data = new { id = userId } });
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
