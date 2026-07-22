using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Infrastructure.NewDashboard;

public class SupabaseJwtValidator(
    IOptions<SupabaseJwtOptions> options,
    ILogger<SupabaseJwtValidator> logger,
    IHttpClientFactory httpClientFactory)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly SupabaseJwtOptions _opts = options.Value;

    public JwtUser? ValidateToken(string? bearerToken)
    {
        return ValidateTokenAsync(bearerToken, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<JwtUser?> ValidateTokenAsync(string? bearerToken, CancellationToken ct = default)
    {
        LogConfigOnFirstValidation();

        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            logger.LogWarning("[ND JWT] Missing Authorization header");
            return null;
        }

        if (!_opts.IsConfigured)
        {
            logger.LogWarning(
                "[ND JWT] Supabase:JwtSecret is not configured — falling back to Supabase Auth API validation");
            return await ValidateViaSupabaseAuthApiAsync(bearerToken, ct);
        }

        return ValidateLocally(bearerToken);
    }

    private JwtUser? ValidateLocally(string bearerToken)
    {
        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken[7..].Trim()
            : bearerToken.Trim();

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            logger.LogWarning("[ND JWT] Token is not a valid JWT (expected 3 segments, got {Count})", parts.Length);
            return null;
        }

        try
        {
            var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            using var header = JsonDocument.Parse(headerJson);
            var alg = header.RootElement.GetProperty("alg").GetString();
            if (!string.Equals(alg, "HS256", StringComparison.Ordinal))
            {
                logger.LogWarning("[ND JWT] Unsupported algorithm: {Alg} (expected HS256)", alg);
                return null;
            }

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var skewSeconds = (long)ClockSkew.TotalSeconds;

            if (root.TryGetProperty("nbf", out var nbfEl))
            {
                var nbf = nbfEl.GetInt64();
                if (now + skewSeconds < nbf)
                {
                    logger.LogWarning("[ND JWT] Token not yet valid (nbf={Nbf}, now={Now})", nbf, now);
                    return null;
                }
            }

            if (root.TryGetProperty("exp", out var expEl))
            {
                var exp = expEl.GetInt64();
                if (now > exp + skewSeconds)
                {
                    logger.LogWarning("[ND JWT] Token expired (exp={Exp}, now={Now})", exp, now);
                    return null;
                }
            }

            if (!HasAudience(root, "authenticated"))
            {
                var aud = root.TryGetProperty("aud", out var audEl) ? audEl.ToString() : "(missing)";
                logger.LogWarning("[ND JWT] Invalid audience: {Aud} (expected \"authenticated\")", aud);
                return null;
            }

            var expectedIssuer = _opts.ExpectedIssuer;
            if (!string.IsNullOrEmpty(expectedIssuer))
            {
                var iss = root.TryGetProperty("iss", out var issEl) ? issEl.GetString() : null;
                if (!string.Equals(iss, expectedIssuer, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "[ND JWT] Invalid issuer: {Iss} (expected {Expected})",
                        iss ?? "(missing)",
                        expectedIssuer);
                    return null;
                }
            }

            var signatureInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
            var expectedSig = Base64UrlDecode(parts[2]);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opts.JwtSecret));
            var actualSig = hmac.ComputeHash(signatureInput);
            if (!CryptographicOperations.FixedTimeEquals(actualSig, expectedSig))
            {
                logger.LogWarning(
                    "[ND JWT] Signature verification failed (check Supabase:JwtSecret matches dashboard JWT secret)");
                return null;
            }

            var sub = root.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
            if (!Guid.TryParse(sub, out var userId))
            {
                logger.LogWarning("[ND JWT] Invalid or missing sub claim: {Sub}", sub ?? "(null)");
                return null;
            }

            var email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
            return new JwtUser(userId, email);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ND JWT] Local validation failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<JwtUser?> ValidateViaSupabaseAuthApiAsync(string bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.Url) || string.IsNullOrWhiteSpace(_opts.ServiceRoleKey))
        {
            logger.LogError(
                "[ND JWT] Cannot validate via Supabase Auth API — set Supabase:Url and Supabase:ServiceRoleKey");
            return null;
        }

        try
        {
            var authValue = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken
                : $"Bearer {bearerToken.Trim()}";

            var client = httpClientFactory.CreateClient(nameof(SupabaseJwtValidator));
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{_opts.Url.TrimEnd('/')}/auth/v1/user");
            request.Headers.Add("apikey", _opts.ServiceRoleKey);
            request.Headers.TryAddWithoutValidation("Authorization", authValue);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "[ND JWT] Supabase Auth API rejected token ({Status}): {Body}",
                    (int)response.StatusCode,
                    body.Length > 200 ? body[..200] : body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!Guid.TryParse(id, out var userId))
            {
                logger.LogWarning("[ND JWT] Supabase Auth API returned invalid user id: {Id}", id ?? "(null)");
                return null;
            }

            var email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
            return new JwtUser(userId, email);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ND JWT] Supabase Auth API validation failed: {Message}", ex.Message);
            return null;
        }
    }

    private bool _loggedConfig;

    private void LogConfigOnFirstValidation()
    {
        if (_loggedConfig) return;
        _loggedConfig = true;

        var secretPrefix = string.IsNullOrEmpty(_opts.JwtSecret)
            ? "(empty)"
            : _opts.JwtSecret[..Math.Min(10, _opts.JwtSecret.Length)] + "...";

        logger.LogInformation(
            "[ND JWT] JwtSecret prefix={SecretPrefix}, issuer={Issuer}",
            secretPrefix,
            string.IsNullOrWhiteSpace(_opts.ExpectedIssuer) ? "(set Supabase:Url)" : _opts.ExpectedIssuer);
    }

    private static bool HasAudience(JsonElement root, string expected)
    {
        if (!root.TryGetProperty("aud", out var audEl))
            return false;

        return audEl.ValueKind switch
        {
            JsonValueKind.String => string.Equals(audEl.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Array => audEl.EnumerateArray()
                .Any(item => string.Equals(item.GetString(), expected, StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

public sealed record JwtUser(Guid UserId, string? Email);
