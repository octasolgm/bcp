namespace Reguliq.Api.Services.Llm;

public static class LlmProviderCatalog
{
    public const string DualVerifySettingKey = "dual_verify_llm";

    public static readonly IReadOnlyDictionary<string, LlmProviderDefinition> Providers =
        new Dictionary<string, LlmProviderDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = new(
                "google",
                "Google Gemini",
                ["gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-3.5-flash"],
                "gemini-2.0-flash",
                "Gemini:ApiKey",
                "GEMINI_API_KEY"),
            ["openai"] = new(
                "openai",
                "OpenAI",
                ["gpt-4o", "gpt-4o-mini", "o1-mini"],
                "gpt-4o",
                "OpenAI:ApiKey",
                "OPENAI_API_KEY"),
            ["anthropic"] = new(
                "anthropic",
                "Anthropic",
                ["claude-sonnet-4-20250514", "claude-3-5-sonnet-latest", "claude-3-5-haiku-latest"],
                "claude-sonnet-4-20250514",
                "Anthropic:ApiKey",
                "ANTHROPIC_API_KEY"),
            ["xai"] = new(
                "xai",
                "xAI (Grok)",
                ["grok-2-latest", "grok-beta"],
                "grok-2-latest",
                "XAi:ApiKey",
                "XAI_API_KEY"),
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
        if (string.IsNullOrWhiteSpace(model) || !def.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            model = def.DefaultModel;

        return new DualVerifyLlmConfig(def.Id, model);
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
