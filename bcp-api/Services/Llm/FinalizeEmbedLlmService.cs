namespace Reguliq.Api.Services.Llm;

/// <summary>Runs the admin-selected LLM for finalize-time policy embed text generation.</summary>
public class FinalizeEmbedLlmService(
    FinalizeEmbedLlmSettingsService settings,
    GeminiService gemini,
    OpenAiCompatibleLlmClient openAi,
    AnthropicLlmClient anthropic,
    XAiLlmClient xAi,
    MoonshotLlmClient moonshot,
    DeepSeekLlmClient deepSeek,
    ZhipuLlmClient zhipu,
    QwenLlmClient qwen,
    OpenRouterLlmClient openRouter,
    ILogger<FinalizeEmbedLlmService> logger)
{
    public async Task<string> AnalyzeTextAsync(string prompt, CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation("Finalize embed LLM using {Provider}/{Model}", cfg.Provider, cfg.Model);
        return cfg.Provider.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "openai" => await openAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "anthropic" => await anthropic.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "xai" => await xAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "moonshot" => await moonshot.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "deepseek" => await deepSeek.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "zhipu" => await zhipu.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "qwen" => await qwen.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "openrouter" => await openRouter.AnalyzeTextAsync(prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }
}
