namespace Reguliq.Api.Services.Llm;

public static class LlmProviderCatalog
{
    public const string DualVerifySettingKey = "dual_verify_llm";
    public const string RegulWorkflowSettingKey = "regul_workflow_llm";

    public static readonly IReadOnlyDictionary<string, LlmProviderDefinition> Providers =
        new Dictionary<string, LlmProviderDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = new(
                "google",
                "Google Gemini",
                [
                    "gemini-3.8-flash",
                    "gemini-3.7-flash",
                    "gemini-3.6-flash",
                    "gemini-3.5-flash",
                    "gemini-3.5-flash-lite",
                    "gemini-3.1-pro-preview",
                    "gemini-3.1-flash-lite",
                    "gemini-2.5-pro",
                    "gemini-2.5-flash",
                    "gemini-2.5-flash-lite",
                    "gemini-2.0-flash",
                    "gemini-2.0-flash-lite",
                ],
                "gemini-3.5-flash",
                "Gemini:ApiKey",
                "GEMINI_API_KEY"),
            ["openai"] = new(
                "openai",
                "OpenAI",
                [
                    "gpt-6-astra",
                    "gpt-5.6-sol",
                    "gpt-5.6-terra",
                    "gpt-5.6-luna",
                    "gpt-5.5",
                    "gpt-5.4",
                    "gpt-5.4-mini",
                    "gpt-5.4-nano",
                    "gpt-5",
                    "gpt-5-mini",
                    "gpt-4o",
                    "gpt-4o-mini",
                    "o1-mini",
                ],
                "gpt-4o",
                "OpenAI:ApiKey",
                "OPENAI_API_KEY"),
            ["anthropic"] = new(
                "anthropic",
                "Anthropic",
                [
                    "claude-fable-5-1",
                    "claude-fable-5",
                    "claude-opus-5",
                    "claude-opus-4-8",
                    "claude-opus-4-7",
                    "claude-opus-4-6",
                    "claude-sonnet-5",
                    "claude-sonnet-4-6",
                    "claude-haiku-4-5",
                    "claude-sonnet-4-20250514",
                    "claude-3-5-sonnet-latest",
                    "claude-3-5-haiku-latest",
                ],
                "claude-sonnet-5",
                "Anthropic:ApiKey",
                "ANTHROPIC_API_KEY"),
            ["xai"] = new(
                "xai",
                "xAI (Grok)",
                [
                    "grok-4.6",
                    "grok-4.5",
                    "grok-4.3",
                    "grok-4.20-0309-reasoning",
                    "grok-4.20-0309-non-reasoning",
                    "grok-3-latest",
                    "grok-2-latest",
                    "grok-beta",
                ],
                "grok-2-latest",
                "XAi:ApiKey",
                "XAI_API_KEY"),
            ["moonshot"] = new(
                "moonshot",
                "Moonshot Kimi",
                ["kimi-k3", "kimi-k2.7-code", "kimi-k2.7-code-highspeed", "kimi-k2.6"],
                "kimi-k3",
                "Moonshot:ApiKey",
                "MOONSHOT_API_KEY"),
            ["deepseek"] = new(
                "deepseek",
                "DeepSeek",
                ["deepseek-flash", "deepseek-v4-pro"],
                "deepseek-flash",
                "DeepSeek:ApiKey",
                "DEEPSEEK_API_KEY"),
            ["zhipu"] = new(
                "zhipu",
                "Zhipu AI (GLM)",
                ["glm-4.6", "glm-4.6v-flash", "glm-4-flash"],
                "glm-4.6",
                "Zhipu:ApiKey",
                "ZHIPU_API_KEY"),
            ["qwen"] = new(
                "qwen",
                "Alibaba (Qwen)",
                ["qwen3.8-max", "qwen3.5-plus", "qwen3.5-flash"],
                "qwen3.5-plus",
                "Qwen:ApiKey",
                "QWEN_API_KEY"),
            // One key routes to every vendor below via OpenRouter's proxy — ids are OpenRouter's own
            // "vendor/model" form (confirmed live against https://openrouter.ai/api/v1/models on 23 Sep
            // 2026), not the vendor's native id. Picking an OpenRouter model here calls OpenRouter, not
            // the vendor directly, even though the same model is also listed under its own provider above.
            ["openrouter"] = new(
                "openrouter",
                "OpenRouter (all models)",
                [
                    "anthropic/claude-opus-5",
                    "anthropic/claude-sonnet-5",
                    "anthropic/claude-haiku-4.5",
                    "openai/gpt-5.6-sol",
                    "openai/gpt-5",
                    "openai/gpt-5-mini",
                    "openai/gpt-4o",
                    "openai/gpt-4o-mini",
                    "google/gemini-3.6-flash",
                    "google/gemini-3.5-flash",
                    "google/gemini-2.5-flash",
                    "google/gemini-2.5-flash-lite",
                    "x-ai/grok-4.6",
                    "x-ai/grok-4.3",
                    "deepseek/deepseek-v4-pro",
                    "deepseek/deepseek-v4.1-flash",
                    "moonshotai/kimi-k3",
                    "moonshotai/kimi-k2.6",
                    "z-ai/glm-4.6",
                    "z-ai/glm-4.5-air",
                    "qwen/qwen3.8-max-0902",
                    "qwen/qwen3.5-plus-20260420",
                    "qwen/qwen3.5-flash-02-23",
                    // Free tier ($0, rate-limited: ~50 requests/day, 1000/day with $10+ paid credit). Free
                    // models may log prompts, so use them for pipeline/config tests, not confidential documents.
                    "nvidia/nemotron-3-ultra-550b-a55b:free",
                    "nvidia/nemotron-3-super-120b-a12b:free",
                    "qwen/qwen3.8-27b:free",
                    "google/gemma-4-31b-it:free",
                    "z-ai/glm-5.2:free",
                    "openrouter/free",
                ],
                "anthropic/claude-sonnet-5",
                "OpenRouter:ApiKey",
                "OPENROUTER_API_KEY"),
        };

    public static LlmProviderDefinition Get(string provider)
    {
        if (Providers.TryGetValue(provider.Trim(), out var def)) return def;
        throw new InvalidOperationException($"Unknown LLM provider '{provider}'.");
    }

    public static bool TryGet(string provider, out LlmProviderDefinition definition) =>
        Providers.TryGetValue(provider.Trim(), out definition!);

    public static DualVerifyLlmConfig Normalize(DualVerifyLlmConfig? config)
    {
        var provider = config?.Provider?.Trim();
        if (string.IsNullOrWhiteSpace(provider) || !TryGet(provider, out var def))
            def = Providers["google"];

        var model = config?.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = def.DefaultModel;
        else if (!def.Models.Contains(model, StringComparer.OrdinalIgnoreCase) && !LooksLikeModelId(model))
            model = def.DefaultModel;

        return new DualVerifyLlmConfig(def.Id, model);
    }

    /// <summary>Allow saved IDs not yet added to the dropdown catalog (e.g. new Anthropic releases, or an
    /// OpenRouter "vendor/model" id beyond our curated list — OpenRouter has 400+).</summary>
    private static bool LooksLikeModelId(string model)
    {
        if (model.Length is < 3 or > 128) return false;
        foreach (var ch in model)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/' or ':') continue;
            return false;
        }

        return true;
    }
}

public record LlmProviderDefinition(
    string Id,
    string Label,
    string[] Models,
    string DefaultModel,
    string ConfigKeyPath,
    string EnvVarName);

public record DualVerifyLlmConfig(string Provider, string Model);
