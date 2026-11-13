namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Shared CORS policy. Only an explicit allow-list of known front-end origins is trusted: the built-in
/// defaults below plus anything added via configuration (Bcp:CorsOrigins / BCP_CORS_ORIGINS /
/// REGULIQ_CORS_ORIGINS, comma-separated). There is deliberately no wildcard: localhost is allowed only in
/// Development, and a blanket "*.azurewebsites.net" trust was removed (any attacker can host there) - add the
/// exact Azure hostname to configuration instead.
/// </summary>
public static class CorsPolicySetup
{
    private static readonly string[] DefaultOrigins =
    [
        "https://bcp-web-dev.azurewebsites.net",
        "https://app.comply-solutions.com",
    ];

    public static void AddBcpCors(IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var raw = BcpConfiguration.GetString(
            config,
            "Bcp:CorsOrigins",
            "BCP_CORS_ORIGINS",
            "REGULIQ_CORS_ORIGINS");

        var configured = ParseOrigins(raw);
        var allowLocalhost = environment.IsDevelopment();
        Console.WriteLine(
            $"CORS allow-list: {string.Join(", ", configured)}"
            + (string.IsNullOrWhiteSpace(raw) ? " (defaults)" : "")
            + (allowLocalhost ? " + localhost (Development)" : ""));

        services.AddCors(o => o.AddDefaultPolicy(p => p
            .SetIsOriginAllowed(origin => IsOriginAllowed(origin, configured, allowLocalhost))
            .AllowAnyHeader()
            .AllowAnyMethod()
            // Content-Disposition isn't in the browser's default exposed-headers safelist, so without
            // this the frontend's fetch() can never read the real download filename cross-origin and
            // always falls back to its hardcoded default name.
            .WithExposedHeaders("Content-Disposition")));
    }

    private static HashSet<string> ParseOrigins(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in DefaultOrigins)
            set.Add(origin);

        if (string.IsNullOrWhiteSpace(raw))
            return set;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);

        return set;
    }

    private static bool IsOriginAllowed(string? origin, HashSet<string> configured, bool allowLocalhost)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        // Exact match against the configured allow-list.
        if (configured.Contains(origin))
            return true;

        // Local dev servers (any port) are trusted only in the Development environment.
        if (allowLocalhost
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}
