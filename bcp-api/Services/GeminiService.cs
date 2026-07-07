using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services;

public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = "gemini-2.5-flash-lite";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}

public class GeminiService(HttpClient http, IOptions<GeminiOptions> options, ILogger<GeminiService> logger)
{
    private readonly GeminiOptions _opts = options.Value;
    private const int MaxAttempts = 4;

    public async Task<string> AnalyzeWithPdfAsync(
        byte[] pdfBytes,
        string fileName,
        string prompt,
        string model,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("GEMINI_API_KEY is not configured");

        var modelId = string.IsNullOrWhiteSpace(model) ? _opts.DefaultModel : model;
        var url = $"{_opts.BaseUrl}/models/{modelId}:generateContent?key={_opts.ApiKey}";

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "application/pdf",
                                data = Convert.ToBase64String(pdfBytes)
                            }
                        }
                    }
                }
            },
            generationConfig = new { temperature = 0.1, maxOutputTokens = 8192 }
        };

        var responseText = await PostWithRetryAsync(url, body, ct);
        return ParseTextResponse(responseText);
    }

    public async Task<string> AnalyzeTextAsync(string prompt, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("GEMINI_API_KEY is not configured");

        var modelId = string.IsNullOrWhiteSpace(model) ? _opts.DefaultModel : model;
        var url = $"{_opts.BaseUrl}/models/{modelId}:generateContent?key={_opts.ApiKey}";

        var body = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.1, maxOutputTokens = 8192 }
        };

        var responseText = await PostWithRetryAsync(url, body, ct);
        return ParseTextResponse(responseText);
    }

    private async Task<string> PostWithRetryAsync(string url, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var res = await http.SendAsync(req, ct);
            var responseText = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                return responseText;

            logger.LogWarning(
                "Gemini error {Status} (attempt {Attempt}/{Max}): {Body}",
                res.StatusCode,
                attempt,
                MaxAttempts,
                responseText[..Math.Min(500, responseText.Length)]);

            lastError = new HttpRequestException($"Gemini API error: {res.StatusCode}");
            if (!IsTransientStatus(res.StatusCode) || attempt >= MaxAttempts)
                throw lastError;

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }

        throw lastError ?? new HttpRequestException("Gemini API request failed");
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError;

    private static string ParseTextResponse(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var candidates))
        {
            foreach (var c in candidates.EnumerateArray())
            {
                if (!c.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                        sb.AppendLine(text.GetString());
                }
            }
        }

        var result = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("Gemini returned empty response");
        return result;
    }
}
