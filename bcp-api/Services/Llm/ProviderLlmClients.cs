using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var messages = new[] { new { role = "user", content = prompt } };
        // GPT-5 / GPT-6 / o-series are reasoning models: they reject `max_tokens` and a custom
        // temperature, and reasoning tokens share the completion budget, so give them more headroom.
        object body = IsReasoningModel(model)
            ? new { model, max_completion_tokens = 16384, messages }
            : new { model, temperature = 0.1, max_tokens = 8192, messages };
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

    private static bool IsReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

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

public class AnthropicLlmClient(HttpClient http, IConfiguration config, ILogger<AnthropicLlmClient> logger)
{
    /// <summary>Logs the token usage Anthropic reports for each call, so spend per run is visible
    /// in the API log (input / output / cache-write / cache-read tokens) — nothing else records it.</summary>
    private void LogUsage(string model, string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("usage", out var u)) return;
            long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            logger.LogInformation(
                "LLM usage model={Model} input={Input} output={Output} cacheWrite={CacheWrite} cacheRead={CacheRead}",
                model, Get("input_tokens"), Get("output_tokens"),
                Get("cache_creation_input_tokens"), Get("cache_read_input_tokens"));
        }
        catch
        {
            /* usage logging must never break a judgment call */
        }
    }

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

    /// <summary>
    /// Regul.ai-style structured tool call: system prompt + cacheable context block + per-clause query block.
    /// Returns serialized tool input JSON.
    /// </summary>
    public async Task<string> StructuredToolCallAsync(
        string system,
        string contextBlock,
        string queryBlock,
        string toolName,
        JsonObject inputSchema,
        string model,
        bool cacheContextBlock,
        CancellationToken ct = default)
    {
        EnsureApiKey();

        var contextContent = new Dictionary<string, object>
        {
            ["type"] = "text",
            ["text"] = contextBlock,
        };
        if (cacheContextBlock)
            contextContent["cache_control"] = new { type = "ephemeral" };

        var userContent = new object[]
        {
            contextContent,
            new { type = "text", text = queryBlock },
        };

        var messages = new object[] { new { role = "user", content = userContent } };
        var body = BuildMessagesBody(model, messages);
        body["system"] = system;
        body["tools"] = new object[]
        {
            new
            {
                name = toolName,
                description = $"Structured output for {toolName}",
                input_schema = inputSchema,
            },
        };
        body["tool_choice"] = new { type = "tool", name = toolName };

        return await PostMessagesAsync(body, ct, expectToolUse: true);
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

    private async Task<string> PostMessagesAsync(
        Dictionary<string, object> body,
        CancellationToken ct,
        bool expectToolUse = false)
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

        LogUsage(body.TryGetValue("model", out var m) ? m?.ToString() ?? "" : "", text);
        return expectToolUse
            ? ParseToolUseResponse(text)
            : ParseMessagesResponse(text);
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

    private static string ParseToolUseResponse(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("content", out var blocks))
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "tool_use"
                    && block.TryGetProperty("input", out var input))
                {
                    return input.GetRawText();
                }
            }
        }

        throw new InvalidOperationException("Anthropic API returned no tool_use block.");
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

/// <summary>Shared chat-completions client for providers that expose an OpenAI-compatible API.</summary>
public abstract class ChatCompletionsClientBase(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    string label,
    string apiKeyConfig,
    string apiKeyEnv,
    string baseUrlConfig,
    string baseUrlEnv,
    string defaultBaseUrl,
    ILogger logger)
{
    private string ApiKey => BcpConfiguration.GetString(config, apiKeyConfig, apiKeyEnv) ?? "";

    private string BaseUrl => BcpConfiguration.GetString(config, baseUrlConfig, baseUrlEnv) ?? defaultBaseUrl;

    /// <summary>Extra provider-specific request fields for this model (e.g. a reasoning-effort setting).</summary>
    protected virtual IReadOnlyDictionary<string, object?> ExtraFields(string model) =>
        new Dictionary<string, object?>();

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
            throw new InvalidOperationException($"{label} API key is not configured.");

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 16384,
            ["messages"] = new[] { new { role = "user", content = prompt } },
        };
        foreach (var (key, value) in ExtraFields(model))
            body[key] = value;

        var http = httpFactory.CreateClient(GetType().Name);
        var url = $"{BaseUrl.TrimEnd('/')}/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{label} API error ({res.StatusCode}): {text[..Math.Min(300, text.Length)]}");

        using var doc = JsonDocument.Parse(text);
        LogUsage(model, doc.RootElement);
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

        throw new InvalidOperationException($"{label} API returned empty response.");
    }

    private void LogUsage(string model, JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("usage", out var u)) return;
            long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            logger.LogInformation(
                "LLM usage provider={Provider} model={Model} input={Input} output={Output}",
                label, model, Get("prompt_tokens"), Get("completion_tokens"));
        }
        catch { /* usage logging must never break a call */ }
    }
}

/// <summary>Moonshot Kimi (OpenAI-compatible API).</summary>
public class MoonshotLlmClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<MoonshotLlmClient> logger)
    : ChatCompletionsClientBase(
        httpFactory, config, "Moonshot Kimi",
        "Moonshot:ApiKey", "MOONSHOT_API_KEY",
        "Moonshot:BaseUrl", "MOONSHOT_API_BASE",
        "https://api.moonshot.ai/v1", logger)
{
    // K3 defaults to maximum reasoning effort, which inflates output tokens and latency;
    // "high" is plenty for clause judgment.
    protected override IReadOnlyDictionary<string, object?> ExtraFields(string model) =>
        model.StartsWith("kimi-k3", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, object?> { ["reasoning_effort"] = "high" }
            : new Dictionary<string, object?>();
}

/// <summary>DeepSeek (OpenAI-compatible API).</summary>
public class DeepSeekLlmClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<DeepSeekLlmClient> logger)
    : ChatCompletionsClientBase(
        httpFactory, config, "DeepSeek",
        "DeepSeek:ApiKey", "DEEPSEEK_API_KEY",
        "DeepSeek:BaseUrl", "DEEPSEEK_API_BASE",
        "https://api.deepseek.com", logger);

/// <summary>Zhipu AI / GLM (OpenAI-compatible API, BigModel platform).</summary>
public class ZhipuLlmClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ZhipuLlmClient> logger)
    : ChatCompletionsClientBase(
        httpFactory, config, "Zhipu AI (GLM)",
        "Zhipu:ApiKey", "ZHIPU_API_KEY",
        "Zhipu:BaseUrl", "ZHIPU_API_BASE",
        "https://open.bigmodel.cn/api/paas/v4", logger);

/// <summary>Alibaba Qwen (OpenAI-compatible API via DashScope, international/Singapore endpoint).</summary>
public class QwenLlmClient(IHttpClientFactory httpFactory, IConfiguration config, ILogger<QwenLlmClient> logger)
    : ChatCompletionsClientBase(
        httpFactory, config, "Alibaba (Qwen)",
        "Qwen:ApiKey", "QWEN_API_KEY",
        "Qwen:BaseUrl", "QWEN_API_BASE",
        "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", logger);
