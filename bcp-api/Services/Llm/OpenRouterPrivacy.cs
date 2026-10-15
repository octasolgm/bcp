namespace Reguliq.Api.Services.Llm;

/// <summary>
/// Data-protection rules sent with every OpenRouter request. OpenRouter routes a request only to endpoints that satisfy
/// the rules: <c>zdr</c> = the endpoint keeps no data, <c>data_collection: "deny"</c> = the endpoint does not store
/// data to train on. Both are on unless switched off in configuration:
/// <c>Privacy:OpenRouter:ZeroDataRetention</c> and <c>Privacy:OpenRouter:DenyDataCollection</c> (true or false).
/// </summary>
public static class OpenRouterPrivacy
{
    /// <summary>The "provider" block for the request body, or null when both rules are switched off.</summary>
    public static Dictionary<string, object?>? ProviderPreferences(IConfiguration config)
    {
        var zeroRetention = config.GetValue<bool?>("Privacy:OpenRouter:ZeroDataRetention") ?? true;
        var denyCollection = config.GetValue<bool?>("Privacy:OpenRouter:DenyDataCollection") ?? true;
        if (!zeroRetention && !denyCollection) return null;

        var provider = new Dictionary<string, object?>();
        if (zeroRetention) provider["zdr"] = true;
        if (denyCollection) provider["data_collection"] = "deny";
        return provider;
    }
}
