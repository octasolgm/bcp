using Npgsql;

namespace Reguliq.Api.Infrastructure;

/// <summary>Builds Npgsql connection strings from postgres:// URIs (handles @ in passwords).</summary>
public static class DatabaseConnectionHelper
{
    public static string? ResolvePostgresConnection(string? raw)
    {
        var fromParts = BuildFromComponentEnv();
        if (!string.IsNullOrWhiteSpace(fromParts)) return fromParts;

        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Trim('"');

        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!TryParsePostgresUri(trimmed, out var username, out var password, out var host, out var port, out var database, out var query))
            return trimmed;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
        };

        ApplyQuerySsl(builder, query);
        return builder.ConnectionString;
    }

    /// <summary>Prefer SUPABASE_DB_* vars — avoids URL-encoding passwords with @ # etc.</summary>
    public static string? BuildFromComponentEnv()
    {
        var host = Environment.GetEnvironmentVariable("SUPABASE_DB_HOST");
        var user = Environment.GetEnvironmentVariable("SUPABASE_DB_USER");
        var pass = Environment.GetEnvironmentVariable("SUPABASE_DB_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            return null;

        var port = int.TryParse(Environment.GetEnvironmentVariable("SUPABASE_DB_PORT"), out var p) ? p : 5432;
        var database = Environment.GetEnvironmentVariable("SUPABASE_DB_NAME") ?? "postgres";

        return new NpgsqlConnectionStringBuilder
        {
            Host = host.Trim(),
            Port = port,
            Username = user.Trim(),
            Password = pass,
            Database = database.Trim(),
            SslMode = SslMode.Require,
        }.ConnectionString;
    }

    private static bool TryParsePostgresUri(
        string trimmed,
        out string username,
        out string password,
        out string host,
        out int port,
        out string database,
        out string query)
    {
        username = password = host = database = query = "";
        port = 5432;

        // Handle passwords containing @ — split on last @ before host
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        var authorityStart = schemeEnd + 3;
        var at = trimmed.LastIndexOf('@');
        if (at <= authorityStart) return false;

        var userInfo = trimmed[authorityStart..at];
        var hostAndRest = trimmed[(at + 1)..];
        var pathStart = hostAndRest.IndexOf('/');
        var hostPort = pathStart >= 0 ? hostAndRest[..pathStart] : hostAndRest;
        var pathAndQuery = pathStart >= 0 ? hostAndRest[pathStart..] : "/postgres";

        var colon = userInfo.IndexOf(':');
        username = colon >= 0 ? Uri.UnescapeDataString(userInfo[..colon]) : Uri.UnescapeDataString(userInfo);
        password = colon >= 0 ? Uri.UnescapeDataString(userInfo[(colon + 1)..]) : "";

        var portSep = hostPort.LastIndexOf(':');
        if (portSep > 0 && int.TryParse(hostPort[(portSep + 1)..], out var parsedPort))
        {
            host = hostPort[..portSep];
            port = parsedPort;
        }
        else
        {
            host = hostPort;
        }

        var q = pathAndQuery.IndexOf('?');
        database = (q >= 0 ? pathAndQuery[..q] : pathAndQuery).TrimStart('/');
        if (string.IsNullOrEmpty(database)) database = "postgres";
        query = q >= 0 ? pathAndQuery[(q + 1)..] : "";
        return !string.IsNullOrEmpty(host);
    }

    private static void ApplyQuerySsl(NpgsqlConnectionStringBuilder builder, string query)
    {
        if (string.IsNullOrEmpty(query)) return;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<SslMode>(val, true, out var ssl))
                builder.SslMode = ssl;
        }
    }
}

