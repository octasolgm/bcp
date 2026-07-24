using Reguliq.Api.Services.Llm;

namespace Reguliq.Api.Services;

/// <summary>Routes dual-verify Pass 2 to the admin-selected LLM provider/model.</summary>
public class DualVerifyLlmService(
    DualVerifyLlmSettingsService settings,
    GeminiService gemini,
    OpenAiCompatibleLlmClient openAi,
    AnthropicLlmClient anthropic,
    XAiLlmClient xAi,
    ILogger<DualVerifyLlmService> logger)
{
    public async Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation("Dual verify Pass 2 using {Provider}/{Model}", cfg.Provider, cfg.Model);
        return cfg.Provider.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "openai" => await openAi.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "anthropic" => await anthropic.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "xai" => await xAi.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }

    public async Task<string> AnalyzeTextAsync(string prompt, CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation("Dual verify Pass 2 (text) using {Provider}/{Model}", cfg.Provider, cfg.Model);
        return cfg.Provider.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "openai" => await openAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "anthropic" => await anthropic.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "xai" => await xAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }

    public async Task<(string Provider, string Model)> GetActiveConfigAsync(CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        return (cfg.Provider, cfg.Model);
    }
}
