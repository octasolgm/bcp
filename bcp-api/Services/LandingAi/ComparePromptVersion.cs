namespace Reguliq.Api.Services.LandingAi;

/// <summary>Compare / dual-verify prompt revisions. V1 = original; V2 = ND v8; V3 = Regul.ai judgment rules (analyse-v9 only).</summary>
public enum ComparePromptVersion
{
    V1 = 1,
    V2 = 2,
    /// <summary>Regul.ai-inspired judgment rules; used by Analysis Version V2 (analyse-v9).</summary>
    V3 = 3,
}

public static class ComparePromptVersionExtensions
{
    public static string ToCacheKey(this ComparePromptVersion version, int internalDocCount)
    {
        if (version == ComparePromptVersion.V1)
            return "v1";

        if (version == ComparePromptVersion.V3)
            return internalDocCount > 1 ? "v3-multi" : "v3";

        return internalDocCount > 1 ? "v2-multi" : "v2";
    }

    public static ComparePromptVersion ParseOrDefault(string? raw, ComparePromptVersion fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return raw.Trim().ToLowerInvariant() switch
        {
            "v1" or "1" => ComparePromptVersion.V1,
            "v3" or "3" => ComparePromptVersion.V3,
            "v2" or "2" => ComparePromptVersion.V2,
            _ => fallback,
        };
    }

    public static string ToApiValue(this ComparePromptVersion version) => version switch
    {
        ComparePromptVersion.V1 => "v1",
        ComparePromptVersion.V3 => "v3",
        _ => "v2",
    };
}
