namespace Reguliq.Api.Infrastructure;

/// <summary>Resolved database mode — Supabase/PostgreSQL required for shared team storage.</summary>
public sealed class DatabaseConfig
{
    public bool UsePostgres { get; init; }
    public bool RequireSupabase { get; init; }
    public string? PostgresConnection { get; init; }
    public string SqlitePath { get; init; } = string.Empty;

    public static bool IsSupabaseRequired(IConfiguration config) =>
        BcpConfiguration.IsTrue(config, "Bcp:RequireSupabase", "BCP_REQUIRE_SUPABASE");

    public static DatabaseConfig Resolve(IConfiguration config, IWebHostEnvironment env)
    {
        var pgRaw = BcpConfiguration.GetString(
            config,
            "ConnectionStrings:DirectUrl",
            "ConnectionStrings:PostgreSQL",
            "DATABASE_URL",
            "DIRECT_URL",
            "REGULIQ_DATABASE_URL",
            "Supabase:PoolerUrl");

        if (string.IsNullOrWhiteSpace(BcpConfiguration.GetString(config, "Supabase:DbHost", "SUPABASE_DB_HOST")))
        {
            var working = SupabasePoolerResolver.ResolveWorkingConnection(pgRaw, config);
            if (!string.IsNullOrWhiteSpace(working))
                pgRaw = working;
        }

        var pgConn = DatabaseConnectionHelper.ResolvePostgresConnection(pgRaw, config);

        var allowSqlite = BcpConfiguration.IsTrue(config, "Bcp:AllowSqlite", "BCP_ALLOW_SQLITE");
        var requireSupabase = IsSupabaseRequired(config);

        var usePostgres = !string.IsNullOrWhiteSpace(pgConn)
            && !BcpConfiguration.IsFalse(config, "Bcp:UsePostgres", "REGULIQ_USE_POSTGRES");

        if (!usePostgres && !allowSqlite)
        {
            if (string.IsNullOrWhiteSpace(pgConn))
            {
                throw new InvalidOperationException(
                    "PostgreSQL connection is required. Set ConnectionStrings:PostgreSQL or Supabase:Db* " +
                    "in appsettings.Development.json (Bcp:UsePostgres=true). " +
                    "Local SQLite is disabled unless Bcp:AllowSqlite=true.");
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
