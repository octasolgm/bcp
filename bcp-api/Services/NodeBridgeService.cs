using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Reguliq.Api.Models;

namespace Reguliq.Api.Services;

public class NodeBridgeOptions
{
    public string BaseUrl { get; set; } = "http://localhost:4000";
    public bool Enabled { get; set; } = true;
}

/// <summary>Calls existing NestJS API for Landing AI Phase 1 compare (reuses cache + ADE).</summary>
public class NodeBridgeService(HttpClient http, IOptions<NodeBridgeOptions> options, ILogger<NodeBridgeService> logger)
{
    private readonly NodeBridgeOptions _opts = options.Value;

    public async Task<string> ComparePointAsync(
        GovPoint point,
        string internalFileHash,
        string internalFileName,
        byte[]? internalPdf,
        bool forceRefresh,
        CancellationToken ct = default)
    {
        if (!_opts.Enabled)
            throw new InvalidOperationException("Node bridge disabled — start NestJS API on port 4000");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            point_id = point.PointId,
            title = point.Title,
            text = point.Text,
            point_type = "mandatory"
        }), Encoding.UTF8, "application/json"), "point");
        form.Add(new StringContent(internalFileHash), "internalFileHash");
        form.Add(new StringContent(forceRefresh.ToString().ToLowerInvariant()), "forceCompare");

        if (internalPdf is { Length: > 0 })
        {
            var fileContent = new ByteArrayContent(internalPdf);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
            form.Add(fileContent, "internalFile", internalFileName);
        }

        var res = await http.PostAsync($"{_opts.BaseUrl}/landing-ai/compare-point", form, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("Node compare-point failed: {Status} {Body}", res.StatusCode, text[..Math.Min(300, text.Length)]);
            throw new HttpRequestException($"Landing AI compare failed: {res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("message", out var msg))
            return msg.GetString() ?? throw new InvalidOperationException("Empty Landing AI message");
        throw new InvalidOperationException("Invalid compare-point response");
    }

    public async Task<string?> GetStoredParseAsync(string fileHash, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return null;
        try
        {
            var res = await http.GetAsync(
                $"{_opts.BaseUrl}/landing-ai/stored-parse?fileHash={Uri.EscapeDataString(fileHash)}", ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("markdown", out var md))
                return md.GetString();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stored parse unavailable for {Hash}", fileHash);
        }
        return null;
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (!_opts.Enabled) return false;
        try
        {
            var res = await http.GetAsync($"{_opts.BaseUrl}/landing-ai/status", ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
