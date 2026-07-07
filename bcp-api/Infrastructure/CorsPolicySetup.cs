namespace Reguliq.Api.Infrastructure;

/// <summary>Shared CORS policy for local Angular and Azure App Service web apps.</summary>
public static class CorsPolicySetup
{
    private static readonly string[] DefaultOrigins =
    [
        "http://localhost:3002",
        "http://localhost:4200",
        "https://bcp-web-dev.azurewebsites.net",
    ];

    public static void AddBcpCors(IServiceCollection services, IConfiguration config)
    {
        var raw = BcpConfiguration.GetString(
            config,
            "Bcp:CorsOrigins",
            "BCP_CORS_ORIGINS",
            "REGULIQ_CORS_ORIGINS");

        var configured = ParseOrigins(raw);
        Console.WriteLine(
            $"CORS allow-list: {string.Join(", ", configured)}"
            + (string.IsNullOrWhiteSpace(raw) ? " (defaults)" : ""));

        services.AddCors(o => o.AddDefaultPolicy(p => p
            .SetIsOriginAllowed(origin => IsOriginAllowed(origin, configured))
            .AllowAnyHeader()
            .AllowAnyMethod()));
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

    private static bool IsOriginAllowed(string? origin, HashSet<string> configured)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (configured.Contains(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            return true;

        return uri.Host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase);
    }
}
