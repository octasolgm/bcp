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
        """
        ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
        ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
          CHECK (status IN (
            'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
            'completed', 'failed', 'cancelled', 'submitted_for_review', 'pulled_back',
            'checker_approved', 'reviewer_approved', 'deleted'
          ));
        """,
        """
        CREATE TABLE IF NOT EXISTS demo_analysis_templates (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          code TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          description TEXT NULL,
          regulation_name_hint TEXT NOT NULL DEFAULT '',
          internal_name_hint TEXT NOT NULL DEFAULT '',
          is_active BOOLEAN NOT NULL DEFAULT true,
          sort_order INT NOT NULL DEFAULT 0,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS demo_analysis_template_points (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          template_id UUID NOT NULL REFERENCES demo_analysis_templates(id) ON DELETE CASCADE,
          clause_no TEXT NOT NULL DEFAULT '',
          clause_title TEXT NULL,
          design_status TEXT NOT NULL DEFAULT 'partial',
          operating_status TEXT NOT NULL DEFAULT 'partial',
          overall_status TEXT NOT NULL DEFAULT 'partial',
          confidence DOUBLE PRECISION NOT NULL DEFAULT 0,
          interpretation TEXT NOT NULL DEFAULT '',
          policy_extract_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          document_reference TEXT NOT NULL DEFAULT '',
          gap_description TEXT NOT NULL DEFAULT '',
          suggested_action TEXT NOT NULL DEFAULT '',
          gap_direction TEXT NOT NULL DEFAULT '',
          sort_order INT NOT NULL DEFAULT 0,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_demo_analysis_template_points_template
          ON demo_analysis_template_points (template_id, sort_order);
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
