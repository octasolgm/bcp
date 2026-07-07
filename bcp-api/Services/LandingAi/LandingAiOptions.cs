namespace Reguliq.Api.Services.LandingAi;

/// <summary>Landing AI ADE (Vision Agent) configuration.</summary>
public sealed class LandingAiOptions
{
    public string ApiKey { get; set; } = "";
    public string ApiBase { get; set; } = "https://api.va.landing.ai";
    public string ParseModel { get; set; } = "dpt-2-latest";
    public string ExtractModel { get; set; } = "extract-latest";
    public string ComparePromptVersion { get; set; } = "v2";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
