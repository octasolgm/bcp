using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Controllers.NewDashboard;

[ApiController]
[Route("nd/auth")]
public class AuthController(
    AppDbContext db,
    SupabaseJwtValidator jwt,
    IOptions<SupabaseJwtOptions> jwtOptions,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env) : NdControllerBase
{
    public record ProfileUpsertRequest(string? FullName, string? Role, Guid? DepartmentId);
    public record ForgotPasswordRequest(string Email, string? RedirectTo);

    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var user = ValidateJwt(jwt);
        if (user == null)
            return Unauthorized(new { success = false, message = "Unauthorized" });

        var profile = await GetOrCreateProfileAsync(db, user, user.Email?.Split('@')[0], null, null, ct);
        if (!profile.IsActive)
            return StatusCode(403, new { success = false, message = "Account deactivated" });

        return Ok(new { success = true, data = MapProfile(profile) });
    }

    [HttpPost("profile")]
    public async Task<IActionResult> UpsertProfile([FromBody] ProfileUpsertRequest body, CancellationToken ct)
    {
        var user = ValidateJwt(jwt);
        if (user == null)
            return Unauthorized(new { success = false, message = "Unauthorized" });

        var existing = await db.NdProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == user.UserId, ct);

        string? roleToApply = null;
        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            if (existing == null)
                roleToApply = body.Role;
            else if (existing.Role == "super_admin")
                roleToApply = body.Role;
        }

        var profile = await GetOrCreateProfileAsync(
            db,
            user,
            body.FullName,
            roleToApply,
            body.DepartmentId,
            ct);

        if (!profile.IsActive)
            return StatusCode(403, new { success = false, message = "Account deactivated" });

        return Ok(new { success = true, data = MapProfile(profile), email = user.Email });
    }

    [HttpPost("forgot-password")]
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
        if (!string.IsNullOrWhiteSpace(redirectTo))
            payload["redirect_to"] = $"{redirectTo}/nd/auth/reset-password";

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await client.PostAsync($"{opts.Url.TrimEnd('/')}/auth/v1/recover", content, ct);
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
