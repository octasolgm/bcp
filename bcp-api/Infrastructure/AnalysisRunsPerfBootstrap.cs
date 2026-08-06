using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure;

/// <summary>
/// Idempotent performance indexes for analysis_runs list + nav-counts.
/// Runs on every startup (CREATE INDEX IF NOT EXISTS — fast when indexes already exist).
/// </summary>
public static class AnalysisRunsPerfBootstrap
{
    private static readonly string[] IndexSql =
    [
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_runs_active_created
          ON analysis_runs (created_at DESC)
          WHERE status IS DISTINCT FROM 'deleted';
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_runs_created_by_active_created
          ON analysis_runs (created_by, created_at DESC)
          WHERE status IS DISTINCT FROM 'deleted';
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_runs_status_created
          ON analysis_runs (status, created_at DESC);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_runs_in_progress
          ON analysis_runs (status, total_points_count, processed_points_count)
          WHERE status IN ('running', 'processing')
             OR (total_points_count > 0 AND processed_points_count < total_points_count);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_points_run_status
          ON analysis_points (analysis_run_id, final_status, landing_ai_status);
        """,
    ];

    public static async Task EnsureIndexesAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var sql in IndexSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
