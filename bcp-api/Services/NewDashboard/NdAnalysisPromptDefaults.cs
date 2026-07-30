using Microsoft.Extensions.Configuration;
using Reguliq.Api.Services.LandingAi;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// ND analysis (Analyse v8, gap analysis, reruns) uses V2 compare + Pass 2 prompts by default.
/// Analyse-v9 (Analysis Version V2) sends ComparePromptVersion=v3 (Regul.ai judgment rules).
/// Legacy dual-verify / LandingAiController stay on ComparePromptVersion.V1 unless explicitly passed.
/// Set LandingAi:ComparePromptVersion to "v1" to roll back ND default only.
/// </summary>
public static class NdAnalysisPromptDefaults
{
    public const ComparePromptVersion DefaultVersion = ComparePromptVersion.V2;

    public static ComparePromptVersion Resolve(IConfiguration configuration)
    {
        var raw = configuration["LandingAi:ComparePromptVersion"]?.Trim();
        if (string.Equals(raw, "v1", StringComparison.OrdinalIgnoreCase))
            return ComparePromptVersion.V1;
        if (string.Equals(raw, "v3", StringComparison.OrdinalIgnoreCase))
            return ComparePromptVersion.V3;
        return DefaultVersion;
    }
}
