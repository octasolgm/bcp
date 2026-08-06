using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Reguliq.Api.Infrastructure;

/// <summary>Builds Npgsql connection strings from postgres:// URIs (handles @ in passwords).</summary>
public static class DatabaseConnectionHelper
{
    private static readonly ConcurrentDictionary<string, (string Ip, long Ticks)> Ipv4Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long Ipv4CacheTtlTicks = TimeSpan.FromMinutes(10).Ticks;

    public static string? ResolvePostgresConnection(string? raw, IConfiguration? config = null)
    {
        var fromParts = BuildFromConfiguration(config);
        if (!string.IsNullOrWhiteSpace(fromParts)) return fromParts;

        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Trim('"');

        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return ApplySupabaseClientLimits(trimmed, config);
        }

        if (!TryParsePostgresUri(trimmed, out var username, out var password, out var host, out var port, out var database, out var query))
            return ApplySupabaseClientLimits(trimmed, config);

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
        return ApplySupabaseClientLimits(builder.ConnectionString, config);
    }

    /// <summary>
    /// Supabase session pooler (port 5432) caps concurrent clients (~15). Without a client-side cap,
    /// Npgsql defaults (100) exhaust the pool and auth/API calls fail with EMAXCONNSESSION.
    /// </summary>
    public static string ApplySupabaseClientLimits(string? connectionString, IConfiguration? config = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString ?? "";

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch
        {
            return connectionString;
        }

        var host = builder.Host ?? "";
        if (!host.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase))
            return builder.ConnectionString;

        // Windows often fails mid-request with SocketException 11004 (WSANO_DATA) while resolving
        // pooler CNAME→ELB. Pin to IPv4 so new pooled connections skip flaky DNS lookups.
        if (TryResolveIpv4(host, out var ipv4))
        {
            builder.Host = ipv4;
            Console.WriteLine($"PostgreSQL pooler IPv4 pin: {host} -> {ipv4}");
        }

        var configuredMax = config?.GetValue("Bcp:PostgresMaxPoolSize", 0) ?? 0;
        if (configuredMax <= 0)
            configuredMax = config?.GetValue("POSTGRES_MAX_POOL_SIZE", 0) ?? 0;

        var sessionMax = configuredMax > 0 ? Math.Clamp(configuredMax, 2, 30) : 8;
        var transactionMax = configuredMax > 0 ? Math.Clamp(configuredMax, 2, 20) : 12;

        builder.MinPoolSize = 0;
        builder.ConnectionIdleLifetime = 15;
        builder.Timeout = Math.Clamp(builder.Timeout, 5, 30);

        if (builder.Port == 5432)
            builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, sessionMax);
        else if (builder.Port == 6543)
        {
            builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, transactionMax);
            // Supavisor transaction mode — prepared statements break EF saves (timeouts on SaveChanges).
            builder.MaxAutoPrepare = 0;
        }

        builder.CommandTimeout = Math.Clamp(builder.CommandTimeout, 15, 60);

        return builder.ConnectionString;
    }

    private static bool TryResolveIpv4(string host, out string ipv4)
    {
        ipv4 = "";
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
            return false;

        var now = DateTime.UtcNow.Ticks;
        if (Ipv4Cache.TryGetValue(host, out var cached) && now - cached.Ticks < Ipv4CacheTtlTicks)
        {
            ipv4 = cached.Ip;
            return true;
        }

        try
        {
            var addrs = Dns.GetHostAddresses(host);
            var v4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 is null) return false;
            ipv4 = v4.ToString();
            Ipv4Cache[host] = (ipv4, now);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prefer Supabase:Db* settings — avoids URL-encoding passwords with @ # etc.</summary>
    public static string? BuildFromConfiguration(IConfiguration? config)
    {
        if (config is null) return null;

        var host = BcpConfiguration.GetString(config, "Supabase:DbHost", "SUPABASE_DB_HOST");
        var user = BcpConfiguration.GetString(config, "Supabase:DbUser", "SUPABASE_DB_USER");
        var pass = BcpConfiguration.GetString(config, "Supabase:DbPassword", "SUPABASE_DB_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            return null;

        var defaultPort = host.Contains("pooler.supabase.com", StringComparison.OrdinalIgnoreCase)
            ? 6543
            : 5432;
        var port = BcpConfiguration.GetInt(config, defaultPort, "Supabase:DbPort", "SUPABASE_DB_PORT");
        var database = BcpConfiguration.GetString(config, "Supabase:DbName", "SUPABASE_DB_NAME") ?? "postgres";

        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = host.Trim(),
            Port = port,
            Username = user.Trim(),
            Password = pass,
            Database = database.Trim(),
            SslMode = SslMode.Require,
        }.ConnectionString;

        return ApplySupabaseClientLimits(cs, config);
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

