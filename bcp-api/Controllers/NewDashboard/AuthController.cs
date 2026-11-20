using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Reguliq.Api.Services.NewDashboard.Demo;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/auth")]
public class AuthController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    NdDemoUserDirectory demoDirectory) : NdControllerBase
{
    public record ProfileUpsertRequest(string? FullName, string? Role, Guid? DepartmentId);
    public record ForgotPasswordRequest(string Email, string? RedirectTo);
    public record LoginRequest(string Email, string Password);
    public record RefreshRequest(string RefreshToken);
    public record ChangePasswordRequest(string Password);
    public record VerifyOtpRequest(string TokenHash, string? Type);

    /// <summary>
    /// Sign in. The browser posts the email and password here and the API talks to the identity
    /// provider; the browser never calls it directly and never holds the project keys.
    /// </summary>
    [HttpPost("login")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Reguliq.Api.Infrastructure.RateLimiterSetup.AuthPolicy)]
    public async Task<IActionResult> Login([FromBody] LoginRequest body, CancellationToken ct)
    {
        var email = body?.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(body?.Password))
            return BadRequest(new { success = false, message = "Email and password are required." });

        var opts = jwtOptions.Value;
        if (!AuthApiConfigured(opts))
            return StatusCode(503, new { success = false, message = "Sign-in is not configured on the server." });

        return await TokenGrantAsync(
            opts,
            "password",
            new Dictionary<string, object?> { ["email"] = email, ["password"] = body.Password },
            "Invalid email or password.",
            ct);
    }

    /// <summary>Exchange a refresh token for a fresh access token.</summary>
    [HttpPost("refresh")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Reguliq.Api.Infrastructure.RateLimiterSetup.AuthPolicy)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.RefreshToken))
            return BadRequest(new { success = false, message = "Refresh token is required." });

        var opts = jwtOptions.Value;
        if (!AuthApiConfigured(opts))
            return StatusCode(503, new { success = false, message = "Sign-in is not configured on the server." });

        return await TokenGrantAsync(
            opts,
            "refresh_token",
            new Dictionary<string, object?> { ["refresh_token"] = body.RefreshToken },
            "Your session has expired. Sign in again.",
            ct);
    }

    /// <summary>Sign out: revokes the session upstream. Always answers OK so the browser can clear its copy.</summary>
    [HttpPost("logout")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var opts = jwtOptions.Value;
        var bearer = ReadBearerToken();
        if (AuthApiConfigured(opts) && !string.IsNullOrWhiteSpace(bearer))
        {
            try
            {
                var client = CreateUserClient(opts, bearer);
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/logout", content, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The browser clears its session either way.
            }
        }

        return Ok(new { success = true });
    }

    /// <summary>Set a new password for the signed-in user (profile page, reset link, invite).</summary>
    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Password))
            return BadRequest(new { success = false, message = "Password is required." });

        var bearer = ReadBearerToken();
        var user = await jwt.ValidateTokenAsync(Request.Headers.Authorization.FirstOrDefault(), ct);
        if (user == null || string.IsNullOrWhiteSpace(bearer))
            return Unauthorized(new { success = false, message = "You are not signed in." });

        var opts = jwtOptions.Value;
        if (!AuthApiConfigured(opts))
            return StatusCode(503, new { success = false, message = "Password changes are not configured on the server." });

        var client = CreateUserClient(opts, bearer);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["password"] = body.Password });
        var res = await client.PutAsync(
            $"{opts.Url.TrimEnd('/')}/auth/v1/user",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            ct);

        if (res.IsSuccessStatusCode)
            return Ok(new { success = true });

        var responseBody = await res.Content.ReadAsStringAsync(ct);
        return StatusCode(
            (int)res.StatusCode == 422 ? 400 : (int)res.StatusCode,
            new { success = false, message = OwnWording(ParseSupabaseError(responseBody), "Could not update the password.") });
    }

    /// <summary>
    /// Turn the one-time token from a reset or invite email into a session. The link lands on our own
    /// page, which posts the token here instead of to the identity provider.
    /// </summary>
    [HttpPost("verify-otp")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Reguliq.Api.Infrastructure.RateLimiterSetup.AuthPolicy)]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.TokenHash))
            return BadRequest(new { success = false, message = "This link is invalid or has expired. Request a new one." });

        var opts = jwtOptions.Value;
        if (!AuthApiConfigured(opts))
            return StatusCode(503, new { success = false, message = "Sign-in is not configured on the server." });

        var type = string.IsNullOrWhiteSpace(body.Type) ? "recovery" : body.Type.Trim();
        var client = CreateAnonClient(opts);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["token_hash"] = body.TokenHash,
        });

        var res = await client.PostAsync(
            $"{opts.Url.TrimEnd('/')}/auth/v1/verify",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            return StatusCode(400, new
            {
                success = false,
                message = OwnWording(
                    ParseSupabaseError(responseBody),
                    "This link is invalid or has expired. Request a new one."),
            });
        }

        var session = ReadSession(responseBody);
        if (session == null)
            return StatusCode(400, new { success = false, message = "This link is invalid or has expired. Request a new one." });

        return Ok(new { success = true, data = session });
    }

    private static bool AuthApiConfigured(SupabaseJwtOptions opts) =>
        !string.IsNullOrWhiteSpace(opts.Url) && !string.IsNullOrWhiteSpace(opts.ApiKey);

    private string? ReadBearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : header.Trim();
    }

    private async Task<IActionResult> TokenGrantAsync(
        SupabaseJwtOptions opts,
        string grantType,
        Dictionary<string, object?> payload,
        string fallbackError,
        CancellationToken ct)
    {
        HttpResponseMessage res;
        string responseBody;
        try
        {
            var client = CreateAnonClient(opts);
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            res = await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/token?grant_type={grantType}", content, ct);
            responseBody = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return StatusCode(503, new
            {
                success = false,
                message = "The sign-in service is temporarily unreachable. Wait a moment and try again.",
            });
        }

        if (!res.IsSuccessStatusCode)
        {
            var message = OwnWording(ParseSupabaseError(responseBody), fallbackError);
            var status = (int)res.StatusCode;
            if (status == 429)
                return StatusCode(429, new { success = false, message = "Too many attempts. Wait a moment and try again." });
            return StatusCode(status is 400 or 401 or 403 ? 401 : status, new { success = false, message });
        }

        var session = ReadSession(responseBody);
        if (session == null)
            return StatusCode(502, new { success = false, message = "Sign-in succeeded but no session was returned. Try again." });

        return Ok(new { success = true, data = session });
    }

    /// <summary>
    /// Keep the identity provider's own phrasing off our sign-in page. Anything we have a wording for is
    /// replaced; anything else falls back to our generic message rather than leaking a provider string.
    /// </summary>
    private static string OwnWording(string? providerMessage, string fallback)
    {
        if (string.IsNullOrWhiteSpace(providerMessage)) return fallback;

        var m = providerMessage.Trim();
        // Password rules are worth showing as-is: they carry no provider branding and tell the user what to fix.
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase)
            && (m.Contains("should be", StringComparison.OrdinalIgnoreCase)
                || m.Contains("at least", StringComparison.OrdinalIgnoreCase)
                || m.Contains("characters", StringComparison.OrdinalIgnoreCase)))
            return m;
        if (m.Contains("invalid login credentials", StringComparison.OrdinalIgnoreCase))
            return "Invalid email or password.";
        if (m.Contains("email not confirmed", StringComparison.OrdinalIgnoreCase))
            return "This account has not been activated yet. Use the link in your invitation email.";
        if (m.Contains("user is banned", StringComparison.OrdinalIgnoreCase)
            || m.Contains("user not found", StringComparison.OrdinalIgnoreCase))
            return "Invalid email or password.";
        if (m.Contains("refresh token", StringComparison.OrdinalIgnoreCase))
            return "Your session has expired. Sign in again.";
        if (m.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || m.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return "Too many attempts. Wait a moment and try again.";

        return fallback;
    }

    private static object? ReadSession(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            var refreshToken = root.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : null;
            long? expiresAt = root.TryGetProperty("expires_at", out var eaEl) && eaEl.TryGetInt64(out var ea) ? ea : null;
            int? expiresIn = root.TryGetProperty("expires_in", out var eiEl) && eiEl.TryGetInt32(out var ei) ? ei : null;

            string? userId = null;
            string? email = null;
            if (root.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object)
            {
                userId = userEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                email = userEl.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
            }

            return new
            {
                accessToken,
                refreshToken,
                expiresAt,
                expiresIn,
                userId,
                email,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HttpClient CreateAnonClient(SupabaseJwtOptions opts)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ApiKey);
        return client;
    }

    private HttpClient CreateUserClient(SupabaseJwtOptions opts, string userAccessToken)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ApiKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);
        return client;
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = await jwt.ValidateTokenAsync(Request.Headers.Authorization.FirstOrDefault(), ct);
        if (user == null)
            return Unauthorized(new { success = false, message = "Unauthorized" });

        try
        {
            using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dbCts.CancelAfter(TimeSpan.FromSeconds(20));

            var profile = await db.NdProfiles.AsNoTracking()
                .Include(p => p.Department)
                .FirstOrDefaultAsync(p => p.Id == user.UserId, dbCts.Token);

            if (profile == null)
                profile = await GetOrCreateProfileAsync(db, user, user.Email?.Split('@')[0], null, null, dbCts.Token);
            else
                await NdWorkspaceMembers.ApplyToProfileAsync(db, profile, dbCts.Token);

            if (!profile.IsActive)
                return StatusCode(403, new { success = false, message = "Account deactivated" });

            var workspaceId = WorkspaceScope.CurrentWorkspaceId ?? profile.TenantId ?? WorkspaceScope.DefaultWorkspaceId;
            var workspace = await db.NdWorkspaces.AsNoTracking()
                .Where(w => w.Id == workspaceId)
                .Select(w => new { id = w.Id, name = w.Name, slug = w.Slug, isActive = w.IsActive, dateFormatRegion = w.DateFormatRegion, jurisdiction = w.Jurisdiction, actionsEnabled = w.ActionsEnabled, embeddingsEnabled = w.EmbeddingsEnabled, integrationsEnabled = w.IntegrationsEnabled })
                .FirstOrDefaultAsync(dbCts.Token);
            if (workspace is { isActive: false } && !IsPlatformAdmin(profile))
                return StatusCode(403, new { success = false, message = "Your workspace has been deactivated. Contact your administrator." });

            var demoIds = await demoDirectory.GetDemoProfileIdsAsync(dbCts.Token);
            return Ok(new { success = true, data = MapProfile(profile, user.Email, demoIds, workspace) });
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or SocketException or OperationCanceledException
            || ex.GetBaseException() is SocketException or TimeoutException or OperationCanceledException)
        {
            return StatusCode(503, new
            {
                success = false,
                message = "Database temporarily unreachable. Wait a moment and try signing in again.",
            });
        }
    }

    [HttpPost("profile")]
    public async Task<IActionResult> UpsertProfile([FromBody] ProfileUpsertRequest body, CancellationToken ct)
    {
        var user = ValidateJwt(jwt);
        if (user == null)
            return Unauthorized(new { success = false, message = "Unauthorized" });

        // Roles are never self-assigned here: a new profile is always created as maker, and only an admin can
        // change a role (UsersController). Taking body.Role let any newly signed-up account make itself super_admin.
        var profile = await GetOrCreateProfileAsync(
            db,
            user,
            body.FullName,
            requestedRole: null,
            body.DepartmentId,
            ct);

        if (!profile.IsActive)
            return StatusCode(403, new { success = false, message = "Account deactivated" });

        var demoIds = await demoDirectory.GetDemoProfileIdsAsync(ct);
        return Ok(new { success = true, data = MapProfile(profile, user.Email, demoIds), email = user.Email });
    }

    [HttpPost("forgot-password")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Reguliq.Api.Infrastructure.RateLimiterSetup.AuthPolicy)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest body, CancellationToken ct)
    {
        var email = body.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { success = false, message = "Email is required." });

        var opts = jwtOptions.Value;
        if (string.IsNullOrWhiteSpace(opts.Url) || string.IsNullOrWhiteSpace(opts.ServiceRoleKey))
            return BadRequest(new { success = false, message = "Supabase admin API not configured." });

        var redirectTo = string.IsNullOrWhiteSpace(body.RedirectTo)
            ? null
            : body.RedirectTo.Trim().TrimEnd('/');

        if (env.IsDevelopment())
        {
            var (link, tokenHash, genError) = await GenerateRecoveryLinkAsync(opts, email, redirectTo, ct);
            if (genError != null) return BadRequest(new { success = false, message = genError });
            return Ok(new
            {
                success = true,
                message = "Development mode: open the reset link below (no email sent).",
                data = new { resetLink = link, tokenHash },
            });
        }

        var recoverError = await SendRecoveryEmailAsync(opts, email, redirectTo, ct);
        if (recoverError != null)
        {
            if (recoverError.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(429, new
                {
                    success = false,
                    message = "Too many reset emails were requested. Wait about an hour, then try again.",
                });
            }

            return BadRequest(new { success = false, message = recoverError });
        }

        return Ok(new
        {
            success = true,
            message = "Check your email for a reset link.",
        });
    }

    private async Task<(string? Link, string? TokenHash, string? Error)> GenerateRecoveryLinkAsync(
        SupabaseJwtOptions opts,
        string email,
        string? redirectTo,
        CancellationToken ct)
    {
        var client = CreateAdminClient(opts);
        var payload = new Dictionary<string, object?> { ["type"] = "recovery", ["email"] = email };
        if (!string.IsNullOrWhiteSpace(redirectTo))
            payload["redirect_to"] = $"{redirectTo}/nd/auth/reset-password";

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/admin/generate_link", content, ct);
        var responseBody = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return (null, null, ParseSupabaseError(responseBody) ?? responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var link = doc.RootElement.TryGetProperty("action_link", out var linkEl)
            ? linkEl.GetString()
            : null;
        var tokenHash = doc.RootElement.TryGetProperty("hashed_token", out var hashEl)
            ? hashEl.GetString()
            : null;

        if (!string.IsNullOrWhiteSpace(tokenHash) && !string.IsNullOrWhiteSpace(redirectTo))
        {
            var direct = $"{redirectTo.TrimEnd('/')}/nd/auth/reset-password?token_hash={Uri.EscapeDataString(tokenHash)}&type=recovery";
            return (direct, tokenHash, null);
        }

        if (string.IsNullOrWhiteSpace(link))
            return (null, null, "Supabase did not return a recovery link.");

        return (link, tokenHash, null);
    }

    private async Task<string?> SendRecoveryEmailAsync(
        SupabaseJwtOptions opts,
        string email,
        string? redirectTo,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);

        var payload = new Dictionary<string, object?> { ["email"] = email };

        // /recover reads the redirect from the query string and ignores it in the body.
        var url = $"{opts.Url.TrimEnd('/')}/auth/v1/recover";
        if (!string.IsNullOrWhiteSpace(redirectTo))
            url += $"?redirect_to={Uri.EscapeDataString($"{redirectTo}/nd/auth/reset-password")}";

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await client.PostAsync(url, content, ct);
        if (res.IsSuccessStatusCode) return null;

        var responseBody = await res.Content.ReadAsStringAsync(ct);
        return ParseSupabaseError(responseBody) ?? responseBody;
    }

    private static string? ParseSupabaseError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("msg", out var msgEl))
                return msgEl.GetString();
            if (doc.RootElement.TryGetProperty("message", out var messageEl))
                return messageEl.GetString();
            if (doc.RootElement.TryGetProperty("error_description", out var descEl))
                return descEl.GetString();
        }
        catch (JsonException)
        {
            // fall through
        }

        return null;
    }

    private HttpClient CreateAdminClient(SupabaseJwtOptions opts)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ServiceRoleKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("apikey", opts.ServiceRoleKey);
        return client;
    }
}
