using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reguliq.Api.Services.NewDashboard.Integrations;

/// <summary>What the sign-in round trip carries, signed so it cannot be forged or edited in the browser.</summary>
public sealed record CloudOAuthState(
    Guid TenantId,
    Guid UserId,
    string Provider,
    long ExpiresAtUnix,
    string Nonce,
    string ReturnOrigin);

/// <summary>
/// Encrypts the refresh tokens kept in the database and signs the OAuth <c>state</c>. Keys are derived (HKDF) from
/// <c>Integrations:EncryptionKey</c> when set, otherwise from the server's Supabase JWT secret, so no extra setting is
/// needed to start. Set <c>Integrations:EncryptionKey</c> to a long random value to separate the two.
/// </summary>
public sealed class CloudSecrets
{
    private const string Prefix = "v1.";
    private readonly byte[] _encryptionKey;
    private readonly byte[] _stateKey;

    public CloudSecrets(IConfiguration config)
    {
        var master = config["Integrations:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(master)) master = config["Supabase:JwtSecret"];
        if (string.IsNullOrWhiteSpace(master))
            throw new InvalidOperationException(
                "Cloud integrations need Integrations:EncryptionKey (or Supabase:JwtSecret) to be configured.");
        var bytes = Encoding.UTF8.GetBytes(master);
        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: Encoding.UTF8.GetBytes("nd-cloud-tokens"));
        _stateKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: Encoding.UTF8.GetBytes("nd-cloud-state"));
    }

    public string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        var all = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(all, 0);
        tag.CopyTo(all, nonce.Length);
        cipher.CopyTo(all, nonce.Length + tag.Length);
        return Prefix + Convert.ToBase64String(all);
    }

    /// <summary>Null when the value is empty, from another key, or tampered with.</summary>
    public string? Decrypt(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var all = Convert.FromBase64String(stored[Prefix.Length..]);
            if (all.Length < 28) return null;
            var nonce = all.AsSpan(0, 12);
            var tag = all.AsSpan(12, 16);
            var cipher = all.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_encryptionKey, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public string SignState(CloudOAuthState state)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(state));
        return $"{payload}.{Base64Url(HMACSHA256.HashData(_stateKey, Encoding.ASCII.GetBytes(payload)))}";
    }

    /// <summary>Null when the signature is wrong, the value is malformed, or it has expired.</summary>
    public CloudOAuthState? VerifyState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var expected = HMACSHA256.HashData(_stateKey, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1]))) return null;
            var state = JsonSerializer.Deserialize<CloudOAuthState>(FromBase64Url(parts[0]));
            if (state == null || state.ExpiresAtUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return state;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        return Convert.FromBase64String(t);
    }
}
