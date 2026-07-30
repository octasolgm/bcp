using System.Text.Json.Nodes;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Services.Llm;

/// <summary>Routes Regul workflow analysis to the admin-selected LLM provider/model.</summary>
public class RegulWorkflowLlmService(
    RegulWorkflowLlmSettingsService settings,
    GeminiService gemini,
    OpenAiCompatibleLlmClient openAi,
    AnthropicLlmClient anthropic,
    XAiLlmClient xAi,
    ILogger<RegulWorkflowLlmService> logger)
{
  private const string JudgmentJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "design_status, operating_status, overall_status, confidence, interpretation, " +
        "policy_extract (array of strings), document_reference, gap_description, suggested_action, gap_direction.";

    public async Task<string> AnalyzeTextAsync(string prompt, CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation("Regul workflow LLM using {Provider}/{Model}", cfg.Provider, cfg.Model);
        return cfg.Provider.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "openai" => await openAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "anthropic" => await anthropic.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "xai" => await xAi.AnalyzeTextAsync(prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }

    /// <summary>
    /// Regul.ai forward judgment: system prompt + cacheable policy context + per-clause query.
    /// Anthropic uses structured tool <c>record_judgment</c>; other providers fall back to JSON text.
    /// </summary>
    public async Task<string> CallJudgmentAsync(
        string contextBlock,
        string queryBlock,
        bool cacheContextBlock,
        CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation(
            "Regul judgment LLM using {Provider}/{Model} (structured={Structured})",
            cfg.Provider,
            cfg.Model,
            cfg.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase));

        if (cfg.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return await anthropic.StructuredToolCallAsync(
                NdRegulPromptDefaults.JudgmentSystemPrompt.Trim(),
                contextBlock,
                queryBlock,
                NdRegulLlmSchemas.JudgmentToolName,
                NdRegulLlmSchemas.JudgmentToolSchema(),
                cfg.Model,
                cacheContextBlock,
                ct);
        }

        var prompt = string.Join("\n\n", new[]
        {
            NdRegulPromptDefaults.JudgmentSystemPrompt.Trim(),
            contextBlock,
            queryBlock,
            JudgmentJsonInstruction,
        });
        return await AnalyzeTextAsync(prompt, ct);
    }

    public async Task<string> AnalyzeWithPdfsAsync(
        IReadOnlyList<(byte[] Pdf, string FileName)> pdfs,
        string prompt,
        CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        logger.LogInformation("Regul workflow LLM (PDF) using {Provider}/{Model}", cfg.Provider, cfg.Model);
        return cfg.Provider.ToLowerInvariant() switch
        {
            "google" => await gemini.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "openai" => await openAi.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "anthropic" => await anthropic.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "xai" => await xAi.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }
}
