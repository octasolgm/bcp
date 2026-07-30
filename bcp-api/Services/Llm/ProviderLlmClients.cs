using System.Text;
using System.Text.Json;
using Reguliq.Api.Infrastructure;

namespace Reguliq.Api.Services.Llm;

public class OpenAiCompatibleLlmClient(HttpClient http, IConfiguration config)
{
    private string ApiKey =>
        BcpConfiguration.GetString(config, "OpenAI:ApiKey", "OPENAI_API_KEY") ?? "";

    private string BaseUrl =>
        BcpConfiguration.GetString(config, "OpenAI:BaseUrl", "OPENAI_API_BASE") ?? "https://api.openai.com/v1";

    public async Task<string> AnalyzeTextAsync(string prompt, string model, CancellationToken ct = default)
    {
        EnsureApiKey("OpenAI");
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 8192,
            messages = new[] { new { role = "user", content = prompt } },
        };
        return await PostChatAsync(body, ct);
    }

    public async Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        string model,
        CancellationToken ct = default)
    {
        _ = pdfs;
        return await AnalyzeTextAsync(prompt, model, ct);
    }

    private async Task<string> PostChatAsync(object body, CancellationToken ct)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI-compatible API error ({res.StatusCode}): {ExtractError(text)}");

        return ParseChatResponse(text);
    }

    private void EnsureApiKey(string label)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException($"{label} API key is not configured.");
    }

    private static string ParseChatResponse(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("choices", out var choices))
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message)) continue;
                if (message.TryGetProperty("content", out var content))
                {
                    var value = content.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        throw new InvalidOperationException("OpenAI-compatible API returned empty response.");
    }

    private static string ExtractError(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? responseText[..Math.Min(300, responseText.Length)];
            }
        }
        catch { /* ignore */ }

        return responseText[..Math.Min(300, responseText.Length)];
    }
}

public class AnthropicLlmClient(HttpClient http, IConfiguration config)
{
    private string ApiKey =>
        BcpConfiguration.GetString(config, "Anthropic:ApiKey", "ANTHROPIC_API_KEY") ?? "";

    private string BaseUrl =>
        BcpConfiguration.GetString(config, "Anthropic:BaseUrl", "ANTHROPIC_API_BASE") ?? "https://api.anthropic.com/v1";

    /// <summary>
    /// Anthropic Messages API: do not send temperature, top_p, or top_k when extended thinking is enabled,
    /// and many current models reject temperature entirely (400 deprecated). Dual-verify uses plain messages only.
    /// </summary>
    private static Dictionary<string, object> BuildMessagesBody(string model, object[] messages) =>
        new()
        {
            ["model"] = model,
            ["max_tokens"] = 8192,
            ["messages"] = messages,
        };

    private static void StripUnsupportedAnthropicSamplingParams(Dictionary<string, object> body)
    {
        body.Remove("temperature");
        body.Remove("top_p");
        body.Remove("top_k");
    }

    public async Task<string> AnalyzeTextAsync(string prompt, string model, CancellationToken ct = default)
    {
        EnsureApiKey();
        var messages = new[]
        {
            new { role = "user", content = new object[] { new { type = "text", text = prompt } } },
        };
        return await PostMessagesAsync(BuildMessagesBody(model, messages), ct);
    }

    public async Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        string model,
        CancellationToken ct = default)
    {
        EnsureApiKey();
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var (pdf, _) in pdfs)
        {
            if (pdf is not { Length: > 0 }) continue;
            content.Add(new
            {
                type = "document",
                source = new
                {
                    type = "base64",
                    media_type = "application/pdf",
                    data = Convert.ToBase64String(pdf),
                },
            });
        }

        var messages = new[] { new { role = "user", content = content.ToArray() } };
        return await PostMessagesAsync(BuildMessagesBody(model, messages), ct);
    }

    private async Task<string> PostMessagesAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        StripUnsupportedAnthropicSamplingParams(body);
        var url = $"{BaseUrl.TrimEnd('/')}/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API error ({res.StatusCode}): {text[..Math.Min(300, text.Length)]}");

        return ParseMessagesResponse(text);
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Anthropic API key is not configured.");
    }

    private static string ParseMessagesResponse(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("content", out var blocks))
        {
            var sb = new StringBuilder();
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text))
                    sb.AppendLine(text.GetString());
            }

            var result = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }

        throw new InvalidOperationException("Anthropic API returned empty response.");
    }
}

public class XAiLlmClient(IHttpClientFactory httpFactory, IConfiguration config)
{
    private string ApiKey =>
        BcpConfiguration.GetString(config, "XAi:ApiKey", "XAI_API_KEY") ?? "";

    private string BaseUrl =>
        BcpConfiguration.GetString(config, "XAi:BaseUrl", "XAI_API_BASE") ?? "https://api.x.ai/v1";

    public Task<string> AnalyzeTextAsync(string prompt, string model, CancellationToken ct = default) =>
        PostChatAsync(prompt, model, ct);

    public Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        string model,
        CancellationToken ct = default)
    {
        _ = pdfs;
        return PostChatAsync(prompt, model, ct);
    }

    private async Task<string> PostChatAsync(string prompt, string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("xAI API key is not configured.");

        var http = httpFactory.CreateClient(nameof(XAiLlmClient));
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 8192,
            messages = new[] { new { role = "user", content = prompt } },
        };
        var url = $"{BaseUrl.TrimEnd('/')}/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"xAI API error ({res.StatusCode}): {text[..Math.Min(300, text.Length)]}");

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("choices", out var choices))
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content))
                {
                    var value = content.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }

        throw new InvalidOperationException("xAI API returned empty response.");
    }
}
