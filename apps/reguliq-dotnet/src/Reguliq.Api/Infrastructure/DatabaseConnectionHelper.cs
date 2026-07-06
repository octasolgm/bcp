using Npgsql;

namespace Reguliq.Api.Infrastructure;

/// <summary>Builds Npgsql connection strings from postgres:// URIs (handles @ in passwords).</summary>
public static class DatabaseConnectionHelper
{
    public static string? ResolvePostgresConnection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        var userInfo = uri.UserInfo;
        var colon = userInfo.IndexOf(':');
        var username = colon >= 0 ? Uri.UnescapeDataString(userInfo[..colon]) : Uri.UnescapeDataString(userInfo);
        var password = colon >= 0 ? Uri.UnescapeDataString(userInfo[(colon + 1)..]) : "";

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/').Split('?')[0],
            Username = username,
            Password = password,
            SslMode = SslMode.Require
        };

        var query = uri.Query.TrimStart('?');
        if (!string.IsNullOrEmpty(query))
        {
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

        return builder.ConnectionString;
    }
}
