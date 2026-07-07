namespace Reguliq.Api.Infrastructure;

/// <summary>Last Postgres connection error for health diagnostics.</summary>
public static class PostgresConnectionDiagnostics
{
    private static string? _lastError;

    public static string? LastError => _lastError;

    public static void SetSuccess() => _lastError = null;

    public static void SetError(Exception ex) =>
        _lastError = ex switch
        {
            Npgsql.PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
            _ => ex.Message,
        };
}
