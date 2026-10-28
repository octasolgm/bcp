using System.Text;
using System.Text.Json;

namespace Reguliq.Api.Services.NewDashboard.Ai;

/// <summary>
/// Captures token/cost usage from every LLM response in one place, instead of in each provider client.
///
/// It is attached to all HttpClients (one line in Program.cs) but only touches requests whose URL is a
/// known chat-completions path, so document downloads and other traffic are never buffered. OpenRouter
/// reports the real <c>usage.cost</c> per call; OpenAI-compatible providers report tokens
/// (prompt_tokens/completion_tokens) and Anthropic reports input_tokens/output_tokens, both of which are
/// priced from the model price list.
/// </summary>
public sealed class LlmUsageHandler(NdAiUsageRecorder recorder, ILogger<LlmUsageHandler> logger) : DelegatingHandler
{
    private static readonly string[] ChatPaths =
    [
        "/chat/completions",   // OpenAI-compatible: OpenAI, xAI, DeepSeek, Moonshot, Zhipu, Qwen, OpenRouter
        "/v1/messages",        // Anthropic
        ":generatecontent",    // Google Gemini
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (!IsChatCall(request) || !response.IsSuccessStatusCode)
            return response;

        try
        {
            // The clients read the body as a string anyway, so buffering it here costs nothing extra.
            var body = await response.Content.ReadAsStringAsync(ct);
            var buffered = new StringContent(body, Encoding.UTF8);
            foreach (var header in response.Content.Headers)
                buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = buffered;

            var usage = Parse(request, body);
            if (usage != null) await recorder.RecordAsync(usage, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading AI usage from the response failed (the call itself succeeded).");
        }

        return response;
    }

    private static bool IsChatCall(HttpRequestMessage request)
    {
        var url = request.RequestUri?.AbsoluteUri.ToLowerInvariant();
        return url != null && ChatPaths.Any(p => url.Contains(p, StringComparison.Ordinal));
    }

    internal static NdAiCallUsage? Parse(HttpRequestMessage request, string body)
    {
        // OpenRouter sends blank keep-alive lines before the JSON while it routes the request, so the body does
        // not start with '{'. Without this trim its calls were never read, and so never billed.
        body = body?.TrimStart() ?? "";
        if (body.Length == 0 || body[0] != '{') return null;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("usage", out var usage) && !root.TryGetProperty("usageMetadata", out usage))
            return null;

        var host = request.RequestUri?.Host ?? "unknown";
        var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;

        long prompt = Long(usage, "prompt_tokens") + Long(usage, "input_tokens") + Long(usage, "promptTokenCount");
        long completion = Long(usage, "completion_tokens") + Long(usage, "output_tokens") + Long(usage, "candidatesTokenCount");
        long cached = Long(usage, "cache_read_input_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out var details))
            cached += Long(details, "cached_tokens");

        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
            cost = c.GetDecimal();

        var generationId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

        return new NdAiCallUsage(ProviderFromHost(host), model, prompt, completion, cached, cost, generationId);
    }

    private static long Long(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string ProviderFromHost(string host) => host switch
    {
        var h when h.Contains("openrouter", StringComparison.OrdinalIgnoreCase) => "openrouter",
        var h when h.Contains("anthropic", StringComparison.OrdinalIgnoreCase) => "anthropic",
        var h when h.Contains("openai.azure", StringComparison.OrdinalIgnoreCase) => "azure-openai",
        var h when h.Contains("openai", StringComparison.OrdinalIgnoreCase) => "openai",
        var h when h.Contains("googleapis", StringComparison.OrdinalIgnoreCase) => "google",
        var h when h.Contains("x.ai", StringComparison.OrdinalIgnoreCase) => "xai",
        var h when h.Contains("deepseek", StringComparison.OrdinalIgnoreCase) => "deepseek",
        var h when h.Contains("moonshot", StringComparison.OrdinalIgnoreCase) => "moonshot",
        var h when h.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) => "zhipu",
        var h when h.Contains("aliyuncs", StringComparison.OrdinalIgnoreCase) => "qwen",
        _ => host,
    };
}
