using System.Text.Json.Nodes;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Services.Llm;

/// <summary>Routes Regul workflow analysis to the admin-selected LLM provider/model.</summary>
public class RegulWorkflowLlmService(
    RegulWorkflowLlmSettingsService settings,
    NdAnalysisPromptVersionService promptVersions,
    GeminiService gemini,
    OpenAiCompatibleLlmClient openAi,
    AnthropicLlmClient anthropic,
    XAiLlmClient xAi,
    MoonshotLlmClient moonshot,
    DeepSeekLlmClient deepSeek,
    ZhipuLlmClient zhipu,
    QwenLlmClient qwen,
    OpenRouterLlmClient openRouter,
    ILogger<RegulWorkflowLlmService> logger)
{
  private const string JudgmentJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "design_status, operating_status, overall_status, confidence, interpretation, " +
        "policy_extract (array of strings), document_reference, gap_description, suggested_action, gap_direction.";

    // V5 (hybrid pipeline) only. Plain-text providers get no per-field descriptions, so the evidence rule is
    // restated right where the output format is requested. Other engines keep JudgmentJsonInstruction unchanged.
    private const string HybridJudgmentJsonInstruction =
        "Respond with ONLY a JSON object (no markdown fences) with keys: " +
        "design_status, operating_status, overall_status, confidence (0 to 1), interpretation, " +
        "policy_extract, document_reference, gap_description, suggested_action, gap_direction. " +
        "policy_extract MUST be an array of strings: quotes copied VERBATIM, character for character, from the internal policy " +
        "excerpts above (keep OCR artifacts, never paraphrase), one item per supporting passage; return an empty array only if no " +
        "excerpt is relevant at all. document_reference must use the exact [bracket label] of the excerpts you quoted. " +
        "If overall_status is compliant, gap_description and suggested_action MUST both be \"N/A\" (a compliant clause has no gap). If it is partial or non_compliant, BOTH gap_description and suggested_action are REQUIRED and non-empty.";

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
            "moonshot" => await moonshot.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "deepseek" => await deepSeek.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "zhipu" => await zhipu.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "qwen" => await qwen.AnalyzeTextAsync(prompt, cfg.Model, ct),
            "openrouter" => await openRouter.AnalyzeTextAsync(prompt, cfg.Model, ct),
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
        string? workflowEngine = null,
        CancellationToken ct = default)
    {
        var cfg = await settings.GetConfigAsync(ct);
        var isHybrid = AnalysisWorkflowEngine.IsRegulPipelineHybrid(workflowEngine);
        var systemPrompt = await promptVersions.GetJudgmentSystemPromptAsync(workflowEngine, ct);
        // Hybrid engine only: each clause sends different retrieved chunks, so caching just adds the
        // cache-write surcharge with no reads. Admin-switchable (default off); full-markdown engines
        // keep caching unconditionally because their context repeats across clauses.
        if (cacheContextBlock && AnalysisWorkflowEngine.IsRegulPipelineHybrid(workflowEngine))
            cacheContextBlock = await settings.IsRetrievalPromptCacheEnabledAsync(ct);
        logger.LogInformation(
            "Regul judgment LLM using {Provider}/{Model} (structured={Structured})",
            cfg.Provider,
            cfg.Model,
            cfg.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase));

        if (cfg.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return await anthropic.StructuredToolCallAsync(
                systemPrompt,
                contextBlock,
                queryBlock,
                NdRegulLlmSchemas.JudgmentToolName,
                isHybrid ? NdRegulLlmSchemas.HybridJudgmentToolSchema() : NdRegulLlmSchemas.JudgmentToolSchema(),
                cfg.Model,
                cacheContextBlock,
                ct);
        }

        var prompt = string.Join("\n\n", new[]
        {
            systemPrompt,
            contextBlock,
            queryBlock,
            isHybrid ? HybridJudgmentJsonInstruction : JudgmentJsonInstruction,
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
            "moonshot" => await moonshot.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "deepseek" => await deepSeek.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "zhipu" => await zhipu.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "qwen" => await qwen.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            "openrouter" => await openRouter.AnalyzeWithPdfsAsync(pdfs, prompt, cfg.Model, ct),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{cfg.Provider}'."),
        };
    }
}
