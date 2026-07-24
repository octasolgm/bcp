namespace Reguliq.Api.Services.LandingAi;

/// <summary>Compare / dual-verify prompt revisions. V1 = original; V2 = ND v8 accuracy prompt.</summary>
public enum ComparePromptVersion
{
    V1 = 1,
    V2 = 2,
}

public static class ComparePromptVersionExtensions
{
    public static string ToCacheKey(this ComparePromptVersion version, int internalDocCount)
    {
        if (version == ComparePromptVersion.V1)
            return "v1";

        return internalDocCount > 1 ? "v2-multi" : "v2";
    }
}
