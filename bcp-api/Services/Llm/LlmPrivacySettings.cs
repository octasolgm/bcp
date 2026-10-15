namespace Reguliq.Api.Services.Llm;

/// <summary>
/// What this installation allows to leave the server, read from the <c>Privacy</c> configuration section. One place
/// for the rules a bank's security team asks about: which model routes may keep or train on data, whether the
/// optional full-text qualitative review may run, and which outside hosts the API may call at all.
/// Defaults are the strict ones, so a missing section means "protected", not "open".
/// </summary>
public sealed record LlmPrivacySettings(
    bool OpenRouterZeroDataRetention,
    bool OpenRouterDenyDataCollection,
    bool DisableQualitativeAssessment,
    bool RequireHttpsOutbound,
    IReadOnlyList<string> AllowedOutboundHosts,
    IReadOnlyList<string> AllowInsecureHosts)
{
    public static LlmPrivacySettings From(IConfiguration config) => new(
        config.GetValue<bool?>("Privacy:OpenRouter:ZeroDataRetention") ?? true,
        config.GetValue<bool?>("Privacy:OpenRouter:DenyDataCollection") ?? true,
        config.GetValue<bool?>("Privacy:DisableQualitativeAssessment") ?? false,
        config.GetValue<bool?>("Privacy:RequireHttpsOutbound") ?? false,
        Clean(config.GetSection("Privacy:AllowedOutboundHosts").Get<string[]>()),
        Clean(config.GetSection("Privacy:AllowInsecureHosts").Get<string[]>()));

    private static IReadOnlyList<string> Clean(string[]? values) =>
        (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToList();

    /// <summary>True when an allow-list is set, so calls to any other host are refused.</summary>
    public bool OutboundRestricted => AllowedOutboundHosts.Count > 0;

    /// <summary>
    /// The OpenRouter "provider" request block that makes OpenRouter route only to endpoints that keep no data
    /// (<c>zdr</c>) and do not train on it (<c>data_collection: deny</c>). Null when both rules are switched off.
    /// </summary>
    public Dictionary<string, object?>? OpenRouterProviderPreferences()
    {
        if (!OpenRouterZeroDataRetention && !OpenRouterDenyDataCollection) return null;
        var provider = new Dictionary<string, object?>();
        if (OpenRouterZeroDataRetention) provider["zdr"] = true;
        if (OpenRouterDenyDataCollection) provider["data_collection"] = "deny";
        return provider;
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.Ordinal)
        || host.Equals("::1", StringComparison.Ordinal)
        || host.Equals("[::1]", StringComparison.Ordinal);

    private static bool Matches(IReadOnlyList<string> patterns, string host)
    {
        foreach (var p in patterns)
        {
            if (p.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (host.Equals(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Null when the call may go ahead, otherwise why it is refused. This machine itself is always allowed.</summary>
    public string? CheckOutbound(Uri uri)
    {
        var host = uri.Host;
        if (IsLoopback(host)) return null;

        if (OutboundRestricted && !Matches(AllowedOutboundHosts, host))
            return $"host '{host}' is not in Privacy:AllowedOutboundHosts";

        if (RequireHttpsOutbound
            && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !Matches(AllowInsecureHosts, host))
            return $"plain http to '{host}' is not allowed (Privacy:RequireHttpsOutbound)";

        return null;
    }
}

/// <summary>
/// Applied to every outgoing HTTP call the API makes (AI models, document reading, storage, sign-in). With
/// Privacy:AllowedOutboundHosts empty it does nothing. With a list, a call to any other host is refused before
/// it leaves the server. This is a second lock behind the client's firewall, not a replacement for it.
/// </summary>
public sealed class OutboundPolicyHandler(IConfiguration config, ILogger<OutboundPolicyHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
        {
            var reason = LlmPrivacySettings.From(config).CheckOutbound(uri);
            if (reason != null)
            {
                logger.LogWarning("Blocked outbound call: {Reason}", reason);
                throw new HttpRequestException(
                    $"This installation's privacy settings block this outbound call: {reason}. Ask your administrator to add the host if it is approved.");
            }
        }
        return base.SendAsync(request, cancellationToken);
    }
}
