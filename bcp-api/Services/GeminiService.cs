using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Reguliq.Api.Services;

public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = "gemini-3.5-flash";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    /// <summary>Used when the requested model returns 403 (not enabled for this API key).</summary>
    public string[] FallbackModels { get; set; } =
        ["gemini-2.0-flash", "gemini-2.0-flash-lite"];
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
        => await AnalyzeWithPdfsAsync([(pdfBytes, fileName)], prompt, model, ct);

    public async Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        string model,
        CancellationToken ct = default)
    {
        EnsureApiKey();
        var parts = new List<object> { new { text = prompt } };
        foreach (var (pdf, _) in pdfs)
        {
            if (pdf is not { Length: > 0 }) continue;
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = "application/pdf",
                    data = Convert.ToBase64String(pdf)
                }
            });
        }

        var body = new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig = new { temperature = 0.1, maxOutputTokens = 8192 }
        };

        return await GenerateWithModelFallbackAsync(model, body, ct);
    }

    public async Task<string> AnalyzeTextAsync(string prompt, string model, CancellationToken ct = default)
    {
        EnsureApiKey();
        var body = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.1, maxOutputTokens = 8192 }
        };

        return await GenerateWithModelFallbackAsync(model, body, ct);
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("GEMINI_API_KEY is not configured");
    }

    private async Task<string> GenerateWithModelFallbackAsync(
        string requestedModel,
        object body,
        CancellationToken ct)
    {
        var models = BuildModelChain(requestedModel);
        Exception? lastError = null;

        foreach (var modelId in models)
        {
            var url = $"{_opts.BaseUrl}/models/{modelId}:generateContent?key={_opts.ApiKey}";
            try
            {
                var responseText = await PostWithRetryAsync(url, body, modelId, ct);
                if (!string.Equals(modelId, models[0], StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Gemini model {Requested} not available for this API key — used fallback {Used}",
                        models[0],
                        modelId);
                }
                return ParseTextResponse(responseText);
            }
            catch (HttpRequestException ex) when (IsModelAccessDenied(ex))
            {
                logger.LogWarning(
                    "Gemini model {Model} forbidden for this API key ({Message})",
                    modelId,
                    ex.Message);
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException(
            $"Gemini API error: none of the configured models are available ({string.Join(", ", models)}). " +
            "Check Gemini:ApiKey in appsettings and enable the model in Google AI Studio.");
    }

    private List<string> BuildModelChain(string requestedModel)
    {
        var chain = new List<string>();
        var primary = string.IsNullOrWhiteSpace(requestedModel) ? _opts.DefaultModel : requestedModel.Trim();
        if (primary.Length > 0) chain.Add(primary);

        foreach (var fb in _opts.FallbackModels ?? [])
        {
            var id = fb.Trim();
            if (id.Length > 0 && !chain.Contains(id, StringComparer.OrdinalIgnoreCase))
                chain.Add(id);
        }

        if (!chain.Contains(_opts.DefaultModel, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_opts.DefaultModel))
        {
            chain.Add(_opts.DefaultModel);
        }

        return chain;
    }

    private async Task<string> PostWithRetryAsync(
        string url,
        object body,
        string modelId,
        CancellationToken ct)
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
                "Gemini error {Status} model={Model} (attempt {Attempt}/{Max}): {Body}",
                res.StatusCode,
                modelId,
                attempt,
                MaxAttempts,
                responseText[..Math.Min(500, responseText.Length)]);

            lastError = BuildHttpError(res.StatusCode, responseText);
            if (IsModelAccessDenied(res.StatusCode) || !IsTransientStatus(res.StatusCode) || attempt >= MaxAttempts)
                throw lastError;

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }

        throw lastError ?? new HttpRequestException("Gemini API request failed");
    }

    private static HttpRequestException BuildHttpError(HttpStatusCode status, string responseText)
    {
        var detail = TryExtractApiError(responseText);
        var suffix = string.IsNullOrWhiteSpace(detail) ? status.ToString() : $"{status}: {detail}";
        return new HttpRequestException($"Gemini API error: {suffix}");
    }

    private static string? TryExtractApiError(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString();
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    private static bool IsModelAccessDenied(HttpRequestException ex) =>
        ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase);

    private static bool IsModelAccessDenied(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

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
