using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Lightweight idempotent DDL for tables added after initial Supabase deploy.
/// Runs on every startup when live schema is present (CREATE TABLE IF NOT EXISTS).
/// </summary>
public static class NdIncrementalSchemaBootstrap
{
    private static readonly string[] PatchSql =
    [
        """
        CREATE TABLE IF NOT EXISTS temp_point_review_comments (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
          comment TEXT NOT NULL,
          commented_by UUID,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_temp_point_review_comments_point
          ON temp_point_review_comments (analysis_point_id);
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
