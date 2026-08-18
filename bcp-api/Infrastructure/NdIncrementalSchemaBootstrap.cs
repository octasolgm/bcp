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
        """
        CREATE TABLE IF NOT EXISTS analysis_action_plans (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
          action_plan TEXT NOT NULL DEFAULT '',
          status TEXT NOT NULL DEFAULT 'pending',
          priority TEXT NOT NULL DEFAULT 'medium',
          target_date TIMESTAMPTZ NULL,
          responsibility_type TEXT NOT NULL DEFAULT 'department',
          responsibility_department_id UUID NULL,
          responsibility_user_id UUID NULL,
          responsibility_label TEXT NULL,
          comment TEXT NULL,
          sort_order INT NOT NULL DEFAULT 0,
          resolved_at TIMESTAMPTZ NULL,
          resolved_by UUID NULL,
          created_by UUID NULL,
          updated_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        ALTER TABLE analysis_action_plans
          ADD COLUMN IF NOT EXISTS gap_index INT NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE analysis_action_plans
          ADD COLUMN IF NOT EXISTS priority_score INT NOT NULL DEFAULT 50;
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_point
          ON analysis_action_plans (analysis_point_id, gap_index, sort_order);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_run
          ON analysis_action_plans (analysis_run_id, priority, status);
        """,
        """
        CREATE TABLE IF NOT EXISTS analysis_action_plan_assignees (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
          assignee_type TEXT NOT NULL DEFAULT 'department',
          department_id UUID NULL,
          user_id UUID NULL,
          label TEXT NOT NULL DEFAULT '',
          sort_order INT NOT NULL DEFAULT 0,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_plan
          ON analysis_action_plan_assignees (action_plan_id, sort_order);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_user
          ON analysis_action_plan_assignees (user_id);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_department
          ON analysis_action_plan_assignees (department_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS analysis_action_plan_reviews (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
          analysis_point_id UUID NOT NULL,
          analysis_run_id UUID NOT NULL,
          comment TEXT NOT NULL DEFAULT '',
          reviewer_id UUID NULL,
          reviewer_role TEXT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_reviews_plan
          ON analysis_action_plan_reviews (action_plan_id, created_at);
        """,
        """
        CREATE TABLE IF NOT EXISTS analysis_action_plan_date_history (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
          previous_target_date TIMESTAMPTZ NULL,
          new_target_date TIMESTAMPTZ NULL,
          reason TEXT NULL,
          changed_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_date_history_plan
          ON analysis_action_plan_date_history (action_plan_id, created_at DESC);
        """,
        """
        DO $$
        DECLARE r record;
        BEGIN
          FOR r IN
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'regulation_documents'::regclass
              AND contype = 'c'
              AND pg_get_constraintdef(oid) ILIKE '%extraction_status%'
          LOOP
            EXECUTE format('ALTER TABLE regulation_documents DROP CONSTRAINT %I', r.conname);
          END LOOP;
        END $$;
        ALTER TABLE regulation_documents ADD CONSTRAINT regulation_documents_extraction_status_check
          CHECK (extraction_status IN (
            'pending', 'processing', 'parsed', 'paused', 'completed', 'failed'
          ));
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
