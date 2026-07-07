using Npgsql;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Finds the correct Supavisor pooler host (aws-0/1/2) when direct IPv6 or wrong pooler URL fails.
/// </summary>
public static class SupabasePoolerResolver
{
    private static readonly string[] Prefixes = ["aws-0", "aws-1", "aws-2"];
    private static readonly string[] Regions =
    [
        "ap-southeast-1", "ap-south-1", "ap-northeast-1",
        "eu-west-1", "eu-central-1", "us-east-1", "us-east-2", "us-west-1",
    ];
    private static readonly int[] Ports = [5432, 6543];

    /// <summary>Try opening the connection; if it fails, probe pooler hosts and return a working URI.</summary>
    public static string? ResolveWorkingConnection(string? rawUri, IConfiguration? config = null)
    {
        if (config is not null
            && !string.IsNullOrWhiteSpace(BcpConfiguration.GetString(config, "Supabase:DbHost", "SUPABASE_DB_HOST")))
            return null;

        var resolved = DatabaseConnectionHelper.ResolvePostgresConnection(rawUri, config);
        if (resolved is null) return null;

        var builder = new NpgsqlConnectionStringBuilder(resolved);
        if (builder.Host?.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Already on pooler — one attempt only (avoid circuit breaker from host brute-force)
            return CanConnect(resolved) ? rawUri : null;
        }

        if (CanConnect(resolved)) return rawUri;

        if (!TryExtractProjectRef(rawUri, resolved, out var projectRef, out var password))
            return null;

        foreach (var prefix in Prefixes)
        foreach (var region in Regions)
        foreach (var port in Ports)
        {
            var probe = new NpgsqlConnectionStringBuilder
            {
                Host = $"{prefix}-{region}.pooler.supabase.com",
                Port = port,
                Database = "postgres",
                Username = $"postgres.{projectRef}",
                Password = password,
                SslMode = SslMode.Require,
                Timeout = 3,
            };

            if (!CanConnect(probe.ConnectionString)) continue;

            return
                $"postgresql://postgres.{projectRef}:{Uri.EscapeDataString(password)}@{prefix}-{region}.pooler.supabase.com:{port}/postgres";
        }

        return null;
    }

    private static bool TryExtractProjectRef(
        string? rawUri,
        string resolved,
        out string projectRef,
        out string password)
    {
        projectRef = "";
        password = "";

        if (!string.IsNullOrWhiteSpace(rawUri)
            && Uri.TryCreate(rawUri.Trim(), UriKind.Absolute, out var uri))
        {
            if (uri.Host.StartsWith("db.", StringComparison.OrdinalIgnoreCase)
                && uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
            {
                projectRef = uri.Host["db.".Length..^".supabase.co".Length];
            }

            var userInfo = uri.UserInfo;
            var colon = userInfo.IndexOf(':');
            var username = colon >= 0 ? Uri.UnescapeDataString(userInfo[..colon]) : Uri.UnescapeDataString(userInfo);
            if (username.StartsWith("postgres.", StringComparison.OrdinalIgnoreCase))
                projectRef = username["postgres.".Length..];
            password = colon >= 0 ? Uri.UnescapeDataString(userInfo[(colon + 1)..]) : "";
        }

        if (string.IsNullOrEmpty(projectRef) || string.IsNullOrEmpty(password))
        {
            var builder = new NpgsqlConnectionStringBuilder(resolved);
            if (builder.Username?.StartsWith("postgres.", StringComparison.OrdinalIgnoreCase) == true)
                projectRef = builder.Username["postgres.".Length..];
            password = builder.Password ?? "";
        }

        return !string.IsNullOrWhiteSpace(projectRef) && !string.IsNullOrEmpty(password);
    }

    private static bool CanConnect(string connectionString)
    {
        try
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
