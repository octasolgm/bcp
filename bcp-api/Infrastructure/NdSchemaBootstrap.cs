using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure;

/// <summary>Creates enterprise dashboard tables (005_enterprise_platform.sql) if missing.</summary>
public static class NdSchemaBootstrap
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await SupabaseSchemaBootstrap.NdSchemaAlreadyPresentAsync(db, ct))
            return;

        await EnsureInlineAsync(db, ct);
    }

  private static async Task EnsureInlineAsync(AppDbContext db, CancellationToken ct)
    {
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

        // Minimal fallback — tables only (no RLS). API uses service-role Postgres connection.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS departments (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              name TEXT NOT NULL,
              description TEXT,
              is_active BOOLEAN NOT NULL DEFAULT true,
              created_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS profiles (
              id UUID PRIMARY KEY,
              full_name TEXT NOT NULL DEFAULT '',
              role TEXT NOT NULL DEFAULT 'maker',
              department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
              is_active BOOLEAN NOT NULL DEFAULT true,
              created_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS regulation_documents (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              stored_document_id UUID,
              name TEXT NOT NULL,
              file_path TEXT NOT NULL DEFAULT '',
              file_url TEXT,
              department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
              extraction_status TEXT NOT NULL DEFAULT 'pending',
              extraction_result JSONB,
              extraction_markdown TEXT,
              extracted_at TIMESTAMPTZ,
              extracted_by UUID,
              created_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS regulation_points (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              regulation_document_id UUID NOT NULL REFERENCES regulation_documents(id) ON DELETE CASCADE,
              point_number TEXT NOT NULL,
              point_title TEXT,
              point_content TEXT NOT NULL,
              page_reference TEXT,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS libraries (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              name TEXT NOT NULL,
              description TEXT,
              department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
              created_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS library_points (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              library_id UUID NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
              regulation_point_id UUID NOT NULL REFERENCES regulation_points(id) ON DELETE CASCADE,
              regulation_document_id UUID NOT NULL REFERENCES regulation_documents(id) ON DELETE CASCADE,
              display_order INTEGER NOT NULL DEFAULT 0,
              point_snapshot JSONB,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              UNIQUE (library_id, regulation_point_id)
            );
            CREATE TABLE IF NOT EXISTS analysis_runs (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              name TEXT NOT NULL,
              description TEXT,
              library_id UUID REFERENCES libraries(id) ON DELETE SET NULL,
              selected_points_snapshot JSONB NOT NULL DEFAULT '[]'::jsonb,
              selected_internal_doc_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
              selected_regulation_doc_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
              status TEXT NOT NULL DEFAULT 'draft',
              total_points_count INTEGER NOT NULL DEFAULT 0,
              processed_points_count INTEGER NOT NULL DEFAULT 0,
              landing_ai_completed_count INTEGER NOT NULL DEFAULT 0,
              dual_verify_completed_count INTEGER NOT NULL DEFAULT 0,
              dual_verify_failed_count INTEGER NOT NULL DEFAULT 0,
              department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
              created_by UUID,
              dual_verify_session_id UUID,
              compliance_session_id UUID,
              submitted_to_checker_at TIMESTAMPTZ,
              checker_reviewed_at TIMESTAMPTZ,
              submitted_to_reviewer_at TIMESTAMPTZ,
              reviewer_finalized_at TIMESTAMPTZ,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS analysis_points (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
              regulation_point_id UUID,
              point_snapshot JSONB NOT NULL DEFAULT jsonb_build_object(),
              landing_ai_status TEXT NOT NULL DEFAULT 'pending',
              landing_ai_result JSONB,
              landing_ai_action_plan TEXT,
              landing_ai_run_at TIMESTAMPTZ,
              landing_ai_error TEXT,
              google_ai_status TEXT NOT NULL DEFAULT 'pending',
              google_ai_result JSONB,
              google_ai_run_at TIMESTAMPTZ,
              google_ai_error TEXT,
              dual_verify_status TEXT NOT NULL DEFAULT 'pending',
              dual_verify_run_at TIMESTAMPTZ,
              final_status TEXT,
              final_action_plan TEXT,
              original_ai_action_plan TEXT,
              landing_ai_rerun_count INTEGER NOT NULL DEFAULT 0,
              dual_verify_rerun_count INTEGER NOT NULL DEFAULT 0,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS action_plan_history (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
              action_plan_content TEXT NOT NULL,
              version_number INTEGER NOT NULL,
              change_type TEXT NOT NULL,
              reverted_to_version INTEGER,
              changed_by UUID,
              is_current BOOLEAN NOT NULL DEFAULT false,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS analysis_reviews (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
              reviewer_id UUID,
              reviewer_role TEXT NOT NULL,
              action TEXT NOT NULL,
              overall_comment TEXT,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS analysis_point_comments (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
              analysis_review_id UUID,
              comment TEXT NOT NULL,
              commented_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS action_plan_item_reviews (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
              analysis_review_id UUID REFERENCES analysis_reviews(id) ON DELETE SET NULL,
              action_index INTEGER NOT NULL,
              status TEXT NOT NULL,
              comment TEXT,
              reviewed_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS analysis_status_history (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
              from_status TEXT,
              to_status TEXT NOT NULL,
              changed_by UUID,
              comment TEXT,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS analysis_point_attachments (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
              stored_document_id UUID NOT NULL REFERENCES stored_documents(id) ON DELETE CASCADE,
              file_name TEXT NOT NULL DEFAULT '',
              uploaded_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_analysis_point_attachments_point
              ON analysis_point_attachments (analysis_point_id);

            ALTER TABLE analysis_point_attachments
              ADD COLUMN IF NOT EXISTS action_index INTEGER;

            ALTER TABLE action_plan_item_reviews
              ADD COLUMN IF NOT EXISTS responsibility TEXT;

            ALTER TABLE action_plan_item_reviews
              ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

            DO $$
            BEGIN
              IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name = 'action_plan_item_reviews'
                  AND column_name = 'due_date'
                  AND data_type = 'date'
              ) THEN
                ALTER TABLE action_plan_item_reviews
                  ALTER COLUMN due_date TYPE TIMESTAMPTZ USING due_date::timestamptz;
              END IF;
            END $$;

            ALTER TABLE action_plan_item_reviews
              ADD COLUMN IF NOT EXISTS priority TEXT;

            ALTER TABLE action_plan_item_reviews
              ADD COLUMN IF NOT EXISTS sort_order INTEGER NOT NULL DEFAULT 0;

            DO $$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM action_plan_item_reviews WHERE sort_order > 0 LIMIT 1) THEN
                WITH ranked AS (
                  SELECT id,
                         ROW_NUMBER() OVER (
                           PARTITION BY analysis_point_id, action_index
                           ORDER BY created_at ASC
                         ) - 1 AS rn
                  FROM action_plan_item_reviews
                )
                UPDATE action_plan_item_reviews r
                SET sort_order = ranked.rn
                FROM ranked
                WHERE r.id = ranked.id;
              END IF;
            END $$;

            ALTER TABLE analysis_reviews
              ADD COLUMN IF NOT EXISTS review_status TEXT;

            ALTER TABLE analysis_reviews
              ADD COLUMN IF NOT EXISTS priority INTEGER;

            ALTER TABLE analysis_reviews
              ADD COLUMN IF NOT EXISTS responsibility TEXT;

            ALTER TABLE analysis_reviews
              ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

            ALTER TABLE regulation_points
              ADD COLUMN IF NOT EXISTS is_introduction_point BOOLEAN NOT NULL DEFAULT false;

            ALTER TABLE regulation_points
              ADD COLUMN IF NOT EXISTS is_annex_point BOOLEAN NOT NULL DEFAULT false;

            ALTER TABLE regulation_points
              ADD COLUMN IF NOT EXISTS status INTEGER NOT NULL DEFAULT 1;

            CREATE INDEX IF NOT EXISTS idx_regulation_points_doc_status
              ON regulation_points (regulation_document_id, status);

            ALTER TABLE regulation_documents
              ADD COLUMN IF NOT EXISTS is_manual BOOLEAN NOT NULL DEFAULT false;

            ALTER TABLE regulation_documents
              ADD COLUMN IF NOT EXISTS status INTEGER NOT NULL DEFAULT 1;

            ALTER TABLE regulation_documents
              ADD COLUMN IF NOT EXISTS extraction_progress_label TEXT NULL;

            ALTER TABLE regulation_documents
              ADD COLUMN IF NOT EXISTS extraction_progress_pct INTEGER NULL;
            ALTER TABLE regulation_documents
              ADD COLUMN IF NOT EXISTS extraction_parse_chunk_completed INTEGER NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS idx_regulation_documents_manual_singleton
              ON regulation_documents (is_manual) WHERE is_manual = true;
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
            ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
              CHECK (status IN (
                'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
                'completed', 'failed', 'submitted_for_review', 'pulled_back',
                'checker_approved', 'reviewer_approved', 'deleted'
              ));
            ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS status_before_delete TEXT;
            ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

            CREATE TABLE IF NOT EXISTS hidden_legacy_runs (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              source TEXT NOT NULL,
              legacy_id UUID NOT NULL,
              deleted_by UUID,
              deleted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              UNIQUE (source, legacy_id)
            );

            CREATE TABLE IF NOT EXISTS nd_analysis_prompt_suggestions (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              prompt_key TEXT NOT NULL,
              comment TEXT NOT NULL,
              created_by UUID,
              updated_by UUID,
              sort_order INTEGER NOT NULL DEFAULT 0,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_nd_prompt_suggestions_key
              ON nd_analysis_prompt_suggestions (prompt_key);
            ALTER TABLE nd_analysis_prompt_suggestions
              ADD COLUMN IF NOT EXISTS applied_in_version_id UUID;

            CREATE TABLE IF NOT EXISTS nd_analysis_prompt_versions (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              prompt_key TEXT NOT NULL,
              version_number INTEGER NOT NULL,
              label TEXT NOT NULL DEFAULT 'Base',
              prompt_text TEXT NOT NULL,
              is_current BOOLEAN NOT NULL DEFAULT false,
              created_by UUID,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              UNIQUE (prompt_key, version_number)
            );
            CREATE INDEX IF NOT EXISTS idx_nd_prompt_versions_key
              ON nd_analysis_prompt_versions (prompt_key);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_nd_prompt_versions_current
              ON nd_analysis_prompt_versions (prompt_key) WHERE is_current = true;
            """,
            ct);
    }
}
