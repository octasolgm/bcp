namespace Reguliq.Api.Infrastructure;

/// <summary>Azure App Service hosting detection and Kestrel port resolution.</summary>
public static class AzureHosting
{
    public static bool IsAppService =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));

    private static bool IsLinuxAppService =>
        string.Equals(
            Environment.GetEnvironmentVariable("WEBSITE_OS"),
            "linux",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Local dev: bind Bcp:ApiPort (5100).
    /// Azure Windows (IIS): ANCM sets ASPNETCORE_PORT — do not override.
    /// Azure Linux: bind PORT / WEBSITES_PORT (8080).
    /// </summary>
    public static void ConfigureWebHost(WebApplicationBuilder builder)
    {
        if (IsAppService)
        {
            if (IsLinuxAppService)
            {
                var port = BcpConfiguration.GetString(
                    builder.Configuration,
                    "PORT",
                    "WEBSITES_PORT",
                    "ASPNETCORE_HTTP_PORTS")
                    ?? "8080";
                builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            }

            return;
        }

        var localPort = BcpConfiguration.GetString(
            builder.Configuration,
            "Bcp:ApiPort",
            "BCP_API_PORT",
            "REGULIQ_API_PORT",
            "PORT")
            ?? "5100";
        builder.WebHost.UseUrls($"http://0.0.0.0:{localPort}");
    }
}
