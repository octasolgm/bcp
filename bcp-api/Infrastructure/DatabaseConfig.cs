namespace Reguliq.Api.Infrastructure;

/// <summary>Resolved database mode — Supabase/PostgreSQL required for shared team storage.</summary>
public sealed class DatabaseConfig
{
    public bool UsePostgres { get; init; }
    public bool RequireSupabase { get; init; }
    public string? PostgresConnection { get; init; }
    public string SqlitePath { get; init; } = string.Empty;

    public static bool IsSupabaseRequired() =>
        string.Equals(
            Environment.GetEnvironmentVariable("BCP_REQUIRE_SUPABASE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static DatabaseConfig Resolve(IConfiguration config, IWebHostEnvironment env)
    {
        var pgRaw = Environment.GetEnvironmentVariable("REGULIQ_DATABASE_URL")
            ?? Environment.GetEnvironmentVariable("DIRECT_URL")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? config.GetConnectionString("PostgreSQL");
        var pgConn = DatabaseConnectionHelper.ResolvePostgresConnection(pgRaw);

        var allowSqlite = string.Equals(
            Environment.GetEnvironmentVariable("BCP_ALLOW_SQLITE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var requireSupabase = IsSupabaseRequired();

        var usePostgres = !string.IsNullOrWhiteSpace(pgConn)
            && !string.Equals(
                Environment.GetEnvironmentVariable("REGULIQ_USE_POSTGRES"),
                "false",
                StringComparison.OrdinalIgnoreCase);

        if (!usePostgres && !allowSqlite)
        {
            if (string.IsNullOrWhiteSpace(pgConn))
            {
                throw new InvalidOperationException(
                    "DATABASE_URL is required. Set Supabase PostgreSQL URI in bcp-api/.env " +
                    "(REGULIQ_USE_POSTGRES=true). Local SQLite is disabled unless BCP_ALLOW_SQLITE=true.");
            }

            usePostgres = true;
        }

        var sqlitePath = Path.Combine(env.ContentRootPath, "data", "reguliq.db");
        return new DatabaseConfig
        {
            UsePostgres = usePostgres,
            RequireSupabase = requireSupabase,
            PostgresConnection = pgConn,
            SqlitePath = sqlitePath,
        };
    }
}
