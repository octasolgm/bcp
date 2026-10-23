using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reguliq.Api.Infrastructure;

namespace Reguliq.Api.Infrastructure.NewDashboard;

public class SupabaseJwtValidator(
    IOptions<SupabaseJwtOptions> options,
    ILogger<SupabaseJwtValidator> logger,
    IHttpClientFactory httpClientFactory,
    IHostEnvironment hostEnvironment)
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
            // Fast local path when JwtSecret is not set — parse claims without signature check. Local Development
            // only: on any other host (Azure, an in-house server) this would accept a hand-made token for any user.
            if (hostEnvironment.IsDevelopment() && !AzureHosting.IsAppService)
            {
                logger.LogWarning(
                    "[ND JWT] Supabase:JwtSecret is not set — using payload parse (no signature check). " +
                    "Set Supabase:JwtSecret for production-like validation.");
                var parsed = ParseJwtPayloadWithoutSignature(bearerToken);
                if (parsed != null)
                    return parsed;
            }

        }

        // New Supabase projects sign user access tokens with JWT Signing Keys (ES256/RS256) instead of the legacy
        // HS256 secret. Verify those here against the project's published public keys (JWKS) - asking the Supabase
        // Auth API on every request was slow and sometimes timed out. The Auth API stays the fallback.
        var header = ReadHeader(bearerToken);
        if (header is { Alg: "ES256" or "RS256" } && !string.IsNullOrWhiteSpace(_opts.Url))
        {
            var key = await GetSigningKeyAsync(header.Kid, ct);
            if (key != null)
            {
                var verified = ValidateJwtCore(bearerToken, (alg, input, sig) => key.Verify(alg, input, sig));
                if (verified != null)
                    return verified;
            }
            logger.LogInformation("[ND JWT] Signing-key validation failed — trying Supabase Auth API");
            return await ValidateViaSupabaseAuthApiAsync(bearerToken, ct);
        }

        if (!_opts.IsConfigured)
        {
            logger.LogWarning(
                "[ND JWT] Supabase:JwtSecret is not configured — falling back to Supabase Auth API validation");
            return await ValidateViaSupabaseAuthApiAsync(bearerToken, ct);
        }

        var local = ValidateLocally(bearerToken);
        if (local != null)
            return local;

        logger.LogInformation(
            "[ND JWT] Local legacy-secret validation failed — trying Supabase Auth API");
        return await ValidateViaSupabaseAuthApiAsync(bearerToken, ct);
    }

    private JwtUser? ValidateLocally(string bearerToken) =>
        ValidateJwtCore(bearerToken, VerifyLegacySecret);

    private JwtUser? ParseJwtPayloadWithoutSignature(string bearerToken) =>
        ValidateJwtCore(bearerToken, verifySignature: null);

    private bool VerifyLegacySecret(string? alg, byte[] signatureInput, byte[] signature)
    {
        if (!string.Equals(alg, "HS256", StringComparison.Ordinal))
        {
            logger.LogWarning("[ND JWT] Unsupported algorithm: {Alg} (expected HS256)", alg);
            return false;
        }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_opts.JwtSecret));
        var actualSig = hmac.ComputeHash(signatureInput);
        if (CryptographicOperations.FixedTimeEquals(actualSig, signature))
            return true;
        logger.LogWarning(
            "[ND JWT] Signature verification failed (check Supabase:JwtSecret matches dashboard JWT secret)");
        return false;
    }

    private sealed record JwtHeader(string? Alg, string? Kid);

    private static JwtHeader? ReadHeader(string bearerToken)
    {
        try
        {
            var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken[7..].Trim()
                : bearerToken.Trim();
            var dot = token.IndexOf('.');
            if (dot <= 0) return null;
            using var header = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(token[..dot])));
            var root = header.RootElement;
            return new JwtHeader(
                root.TryGetProperty("alg", out var a) ? a.GetString() : null,
                root.TryGetProperty("kid", out var k) ? k.GetString() : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --- JWT Signing Keys (JWKS) ---------------------------------------------------------------------------

    /// <summary>One public key from the project's JWKS. Parameters only; a fresh ECDsa/RSA per verification
    /// keeps concurrent requests off a shared crypto object.</summary>
    private sealed record SigningKey(string Alg, ECParameters? Ec, RSAParameters? Rsa)
    {
        public bool Verify(string? alg, byte[] input, byte[] signature)
        {
            if (!string.Equals(alg, Alg, StringComparison.Ordinal)) return false;
            if (Ec is { } ec)
            {
                using var ecdsa = ECDsa.Create(ec);
                return ecdsa.VerifyData(input, signature, HashAlgorithmName.SHA256);
            }
            if (Rsa is { } rsaParams)
            {
                using var rsa = RSA.Create(rsaParams);
                return rsa.VerifyData(input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            return false;
        }
    }

    private static readonly TimeSpan SigningKeysTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SigningKeysMinRefetch = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _signingKeysLock = new(1, 1);
    private Dictionary<string, SigningKey> _signingKeys = new(StringComparer.Ordinal);
    private DateTimeOffset _signingKeysFetchedAt = DateTimeOffset.MinValue;

    private async Task<SigningKey?> GetSigningKeyAsync(string? kid, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(kid)) return null;
        var keys = _signingKeys;
        var age = DateTimeOffset.UtcNow - _signingKeysFetchedAt;
        if (age < SigningKeysTtl && keys.TryGetValue(kid, out var cached))
            return cached;

        await _signingKeysLock.WaitAsync(ct);
        try
        {
            age = DateTimeOffset.UtcNow - _signingKeysFetchedAt;
            // A key rotated in since the last fetch gets one refetch; an unknown kid cannot force a fetch per request.
            var stale = age >= SigningKeysTtl || (!_signingKeys.ContainsKey(kid) && age >= SigningKeysMinRefetch);
            if (stale)
            {
                var fetched = await FetchSigningKeysAsync(ct);
                if (fetched != null)
                {
                    _signingKeys = fetched;
                    _signingKeysFetchedAt = DateTimeOffset.UtcNow;
                }
                else if (_signingKeysFetchedAt == DateTimeOffset.MinValue || age >= SigningKeysTtl)
                {
                    // Keep serving the last good keys, but retry no sooner than the refetch floor.
                    _signingKeysFetchedAt = DateTimeOffset.UtcNow - SigningKeysTtl + SigningKeysMinRefetch;
                }
            }
            return _signingKeys.TryGetValue(kid, out var key) ? key : null;
        }
        finally
        {
            _signingKeysLock.Release();
        }
    }

    private async Task<Dictionary<string, SigningKey>?> FetchSigningKeysAsync(CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(SupabaseJwtValidator));
            var url = $"{_opts.Url.TrimEnd('/')}/auth/v1/.well-known/jwks.json";
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[ND JWT] Could not load signing keys ({Status}) from {Url}", (int)response.StatusCode, url);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = new Dictionary<string, SigningKey>(StringComparer.Ordinal);
            foreach (var jwk in doc.RootElement.GetProperty("keys").EnumerateArray())
            {
                string? Str(string name) => jwk.TryGetProperty(name, out var v) ? v.GetString() : null;
                var kid = Str("kid");
                var kty = Str("kty");
                if (string.IsNullOrEmpty(kid)) continue;
                if (kty == "EC" && Str("crv") == "P-256" && Str("x") is { } x && Str("y") is { } y)
                {
                    result[kid] = new SigningKey("ES256", new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = Base64UrlDecode(x), Y = Base64UrlDecode(y) },
                    }, null);
                }
                else if (kty == "RSA" && Str("n") is { } n && Str("e") is { } e)
                {
                    result[kid] = new SigningKey("RS256", null, new RSAParameters
                    {
                        Modulus = Base64UrlDecode(n),
                        Exponent = Base64UrlDecode(e),
                    });
                }
            }
            logger.LogInformation("[ND JWT] Loaded {Count} signing key(s) from {Url}", result.Count, url);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ND JWT] Could not load signing keys: {Message}", ex.Message);
            return null;
        }
    }

    /// <param name="verifySignature">Checks (alg, signing input, signature); null parses claims without a signature
    /// check (local Development only, see <see cref="ValidateTokenAsync"/>).</param>
    private JwtUser? ValidateJwtCore(string bearerToken, Func<string?, byte[], byte[], bool>? verifySignature)
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
            var alg = header.RootElement.TryGetProperty("alg", out var algEl) ? algEl.GetString() : null;

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

            if (verifySignature != null)
            {
                var signatureInput = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
                if (!verifySignature(alg, signatureInput, Base64UrlDecode(parts[2])))
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
