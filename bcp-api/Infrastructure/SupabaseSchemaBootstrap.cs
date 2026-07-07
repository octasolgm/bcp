using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure;

/// <summary>Align Supabase tables with EF entities (extra columns + safe create).</summary>
public static class SupabaseSchemaBootstrap
{
    private static readonly string[] PatchSql =
    [
        """
        ALTER TABLE dual_verify_sessions
          ADD COLUMN IF NOT EXISTS queued_points INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE dual_verify_sessions
          ADD COLUMN IF NOT EXISTS transport TEXT NOT NULL DEFAULT 'local';
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, DatabaseConfig dbConfig, CancellationToken ct = default)
    {
        if (!dbConfig.UsePostgres)
        {
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION set_updated_at()
            RETURNS TRIGGER AS $$
            BEGIN
              NEW.updated_at = now();
              RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """,
            ct);

        await db.Database.EnsureCreatedAsync(ct);

        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
