using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services.LandingAi;

/// <summary>HTTP client for Landing AI ADE parse + extract APIs.</summary>
public class LandingAiHttpClient(HttpClient http, IOptions<LandingAiOptions> options, IWebHostEnvironment env)
{
    private readonly LandingAiOptions _opts = options.Value;

    public bool IsConfigured => _opts.IsConfigured;

    public async Task<string> ParseDocumentAsync(byte[] documentBytes, string fileName, CancellationToken ct = default)
    {
        var body = await ParseDocumentRawAsync(documentBytes, fileName, ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("markdown", out var md))
        {
            var markdown = md.GetString() ?? throw new InvalidOperationException("Landing AI parse returned empty markdown");
            return PolicyPageResolver.InjectPageMarkersFromParseJson(body, markdown);
        }
        throw new InvalidOperationException("Landing AI parse response missing markdown");
    }

    public async Task<string> ParseDocumentRawAsync(byte[] documentBytes, string fileName, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(documentBytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            LandingAiDocumentFormats.ContentTypeForFileName(fileName));
        form.Add(file, "document", SanitizeUploadFileName(fileName));
        form.Add(new StringContent(_opts.ParseModel), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.ApiBase.TrimEnd('/')}/v1/ade/parse") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Landing AI parse failed ({(int)res.StatusCode}): {Trim(body)}");
        return body;
    }

    public async Task<JsonElement> ExtractComparisonAsync(string markdown, CancellationToken ct = default)
    {
        EnsureConfigured();
        var schemaPath = Path.Combine(env.ContentRootPath, "Schemas", "compliance-comparison-v2.schema.json");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("Missing compliance-comparison-v2.schema.json", schemaPath);

        var schema = await File.ReadAllTextAsync(schemaPath, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(schema, Encoding.UTF8, "application/json"), "schema");
        form.Add(new StringContent(markdown, Encoding.UTF8, "text/markdown"), "markdown", "document.md");
        form.Add(new StringContent(_opts.ExtractModel), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.ApiBase.TrimEnd('/')}/v1/ade/extract") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Landing AI extract failed ({(int)res.StatusCode}): {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("extraction", out var extraction))
            return extraction.Clone();
        throw new InvalidOperationException("Landing AI extract response missing extraction");
    }

    /// <summary>Extract numbered government requirement points from markdown.</summary>
    public async Task<JsonElement> ExtractGovRequirementPointsAsync(string markdown, CancellationToken ct = default)
    {
        EnsureConfigured();
        var schemaPath = Path.Combine(env.ContentRootPath, "Schemas", "gov-requirement-points.schema.json");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("Missing gov-requirement-points.schema.json", schemaPath);

        var schema = await File.ReadAllTextAsync(schemaPath, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(schema, Encoding.UTF8, "application/json"), "schema");
        form.Add(new StringContent(markdown, Encoding.UTF8, "text/markdown"), "markdown", "document.md");
        form.Add(new StringContent(_opts.ExtractModel), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.ApiBase.TrimEnd('/')}/v1/ade/extract") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Landing AI gov extract failed ({(int)res.StatusCode}): {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("extraction", out var extraction))
            return extraction.Clone();
        throw new InvalidOperationException("Landing AI extract response missing extraction");
    }

    /// <summary>Extract policy sections/clauses — Regul.ai EXTRACTION_TOOL_SCHEMA shape.</summary>
    public async Task<JsonElement> ExtractPolicyClausesAsync(string markdown, CancellationToken ct = default)
    {
        EnsureConfigured();
        var schemaPath = Path.Combine(env.ContentRootPath, "Schemas", "policy-clauses.schema.json");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException("Missing policy-clauses.schema.json", schemaPath);

        var schema = await File.ReadAllTextAsync(schemaPath, ct);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(schema, Encoding.UTF8, "application/json"), "schema");
        form.Add(new StringContent(markdown, Encoding.UTF8, "text/markdown"), "markdown", "document.md");
        form.Add(new StringContent(_opts.ExtractModel), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.ApiBase.TrimEnd('/')}/v1/ade/extract") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Landing AI policy extract failed ({(int)res.StatusCode}): {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("extraction", out var extraction))
            return extraction.Clone();
        throw new InvalidOperationException("Landing AI policy extract response missing extraction");
    }

    private void EnsureConfigured()
    {
        if (!_opts.IsConfigured)
            throw new InvalidOperationException(
                "Landing AI is not configured. Set LandingAi:ApiKey in appsettings.Development.json (VISION_AGENT_API_KEY).");
    }

    private static string Trim(string body) =>
        body.Length <= 300 ? body : body[..300];

    private static string SanitizeUploadFileName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "document.pdf" : Path.GetFileName(name);
}
