using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services.Storage;

public class SupabaseStorageService(
    HttpClient http,
    IOptions<SupabaseStorageOptions> options,
    ILogger<SupabaseStorageService> logger)
{
    private readonly SupabaseStorageOptions _opts = options.Value;

    public bool IsConfigured => _opts.IsConfigured;

    public string Bucket => string.IsNullOrWhiteSpace(_opts.Bucket) ? "doc" : _opts.Bucket.Trim();

    public async Task UploadAsync(
        string objectPath,
        Stream content,
        string contentType,
        bool upsert,
        CancellationToken ct = default)
    {
        EnsureConfigured();
        var url = $"{TrimUrl(_opts.Url)}/storage/v1/object/{Bucket}/{TrimPath(objectPath)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(req);
        if (upsert) req.Headers.TryAddWithoutValidation("x-upsert", "true");
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            logger.LogError("Supabase Storage upload failed ({Status}): {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException(
                $"Supabase Storage upload failed ({(int)res.StatusCode}). " +
                "Check bucket name 'doc' is private and ServiceRoleKey is valid.");
        }
    }

    public async Task<byte[]> DownloadAsync(string objectPath, CancellationToken ct = default)
    {
        EnsureConfigured();
        var url = $"{TrimUrl(_opts.Url)}/storage/v1/object/{Bucket}/{TrimPath(objectPath)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            logger.LogError("Supabase Storage download failed ({Status}): {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException($"Supabase Storage download failed ({(int)res.StatusCode}).");
        }
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<string> CreateSignedUrlAsync(
        string objectPath,
        int expiresInSeconds = 3600,
        CancellationToken ct = default)
    {
        EnsureConfigured();
        var url = $"{TrimUrl(_opts.Url)}/storage/v1/object/sign/{Bucket}/{TrimPath(objectPath)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyAuth(req);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { expiresIn = expiresInSeconds }),
            Encoding.UTF8,
            "application/json");

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Supabase signed URL failed ({Status}): {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException($"Could not create signed URL ({(int)res.StatusCode}).");
        }

        using var doc = JsonDocument.Parse(body);
        var signed = doc.RootElement.TryGetProperty("signedURL", out var s)
            ? s.GetString()
            : doc.RootElement.TryGetProperty("signedUrl", out var s2)
                ? s2.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(signed))
            throw new InvalidOperationException("Supabase signed URL response missing signedURL.");

        if (signed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return signed;

        return $"{TrimUrl(_opts.Url)}/storage/v1{signed}";
    }

    public async Task DeleteAsync(string objectPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectPath) || !IsConfigured) return;
        var url = $"{TrimUrl(_opts.Url)}/storage/v1/object/{Bucket}/{TrimPath(objectPath)}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        ApplyAuth(req);
        using var res = await http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode || (int)res.StatusCode == 404) return;

        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogWarning("Supabase Storage delete failed ({Status}): {Body}", (int)res.StatusCode, body);
    }

    private void EnsureConfigured()
    {
        if (!_opts.IsConfigured)
        {
            throw new InvalidOperationException(
                "Supabase Storage is not configured. Replace PASTE_SERVICE_ROLE_KEY_HERE with your " +
                "real service_role key in appsettings.Development.json / appsettings.Secrets.json " +
                "(Project Settings → API), then restart the API.");
        }
    }

    private void ApplyAuth(HttpRequestMessage req)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ServiceRoleKey);
        req.Headers.TryAddWithoutValidation("apikey", _opts.ServiceRoleKey);
    }

    private static string TrimUrl(string url) => url.Trim().TrimEnd('/');

    private static string TrimPath(string path) => path.Trim().TrimStart('/');
}
