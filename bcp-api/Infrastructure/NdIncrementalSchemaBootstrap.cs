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
        ALTER TABLE analysis_action_plan_reviews
          ADD COLUMN IF NOT EXISTS assignee_type TEXT NULL,
          ADD COLUMN IF NOT EXISTS assignee_department_id UUID NULL,
          ADD COLUMN IF NOT EXISTS assignee_user_id UUID NULL,
          ADD COLUMN IF NOT EXISTS assignee_label TEXT NULL;
        """,
        """
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'analysis_action_plan_reviews_assignee_type_check'
          ) THEN
            ALTER TABLE analysis_action_plan_reviews
              ADD CONSTRAINT analysis_action_plan_reviews_assignee_type_check
              CHECK (assignee_type IS NULL OR assignee_type IN ('department', 'user'));
          END IF;
        END $$;
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_action_plan_reviews_assignee_user
          ON analysis_action_plan_reviews (assignee_user_id)
          WHERE assignee_user_id IS NOT NULL;
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_action_plan_reviews_assignee_department
          ON analysis_action_plan_reviews (assignee_department_id)
          WHERE assignee_department_id IS NOT NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS analysis_action_plan_status_history (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
          previous_status TEXT NULL,
          new_status TEXT NOT NULL,
          changed_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_status_history_plan
          ON analysis_action_plan_status_history (action_plan_id, created_at DESC);
        """,
        """
        CREATE TABLE IF NOT EXISTS analysis_gaps (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          analysis_run_id UUID NOT NULL,
          analysis_point_id UUID NOT NULL,
          gap_index INT NOT NULL,
          risk TEXT NOT NULL DEFAULT 'medium',
          risk_score INT NOT NULL DEFAULT 50,
          status TEXT NOT NULL DEFAULT 'pending',
          resolved_at TIMESTAMPTZ NULL,
          resolved_by UUID NULL,
          updated_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS idx_analysis_gaps_point_index
          ON analysis_gaps (analysis_point_id, gap_index);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_analysis_gaps_run
          ON analysis_gaps (analysis_run_id, status);
        """,
        """
        ALTER TABLE analysis_points
          ADD COLUMN IF NOT EXISTS final_status_source TEXT NULL,
          ADD COLUMN IF NOT EXISTS ai_final_status TEXT NULL;
        """,
        """
        DO $$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conname = 'analysis_points_final_status_source_check'
          ) THEN
            ALTER TABLE analysis_points
              ADD CONSTRAINT analysis_points_final_status_source_check
              CHECK (final_status_source IS NULL OR final_status_source IN ('manual', 'auto'));
          END IF;
        END $$;
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
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parse_progress_label TEXT NULL,
          ADD COLUMN IF NOT EXISTS parse_progress_pct INTEGER NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_local_document_extractions (
          id UUID PRIMARY KEY,
          stored_document_id UUID NOT NULL REFERENCES stored_documents(id) ON DELETE CASCADE,
          status TEXT NOT NULL DEFAULT 'pending',
          total_pages INTEGER NULL,
          ocr_page_count INTEGER NULL,
          section_count INTEGER NULL,
          sections_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          warnings_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          error TEXT NULL,
          parsed_at TIMESTAMPTZ NULL,
          parsed_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        -- Superseded by ix_nd_local_document_extractions_doc_engine below (stored_document_id is no
        -- longer unique on its own now that a document can have one row per engine) — this step is now
        -- just a safe no-op cleanup for anyone whose DB still has the old single-column index, kept as a
        -- DROP rather than removed outright so `PatchSql` stays append-only/idempotent to rerun.
        DROP INDEX IF EXISTS ix_nd_local_document_extractions_doc;
        """,
        """
        ALTER TABLE nd_local_document_extractions
          ADD COLUMN IF NOT EXISTS markdown_text TEXT NULL,
          ADD COLUMN IF NOT EXISTS extract_status TEXT NOT NULL DEFAULT 'pending',
          ADD COLUMN IF NOT EXISTS extract_error TEXT NULL,
          ADD COLUMN IF NOT EXISTS extracted_at TIMESTAMPTZ NULL;
        """,
        """
        ALTER TABLE nd_local_document_extractions
          ADD COLUMN IF NOT EXISTS engine TEXT NOT NULL DEFAULT 'tesseract';
        """,
        """
        DROP INDEX IF EXISTS ix_nd_local_document_extractions_doc;
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_nd_local_document_extractions_doc_engine
          ON nd_local_document_extractions (stored_document_id, engine);
        """,
        """
        ALTER TABLE nd_local_document_extractions
          ADD COLUMN IF NOT EXISTS index_status TEXT NOT NULL DEFAULT 'pending',
          ADD COLUMN IF NOT EXISTS index_error TEXT NULL,
          ADD COLUMN IF NOT EXISTS indexed_at TIMESTAMPTZ NULL;
        """,
        """
        CREATE EXTENSION IF NOT EXISTS vector;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_local_document_extraction_sections (
          id UUID PRIMARY KEY,
          extraction_id UUID NOT NULL REFERENCES nd_local_document_extractions(id) ON DELETE CASCADE,
          section_index INTEGER NOT NULL,
          clause_no TEXT NULL,
          clause_text TEXT NOT NULL,
          source_page INTEGER NULL,
          embedding vector(384) NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_nd_local_document_extraction_sections_extraction
          ON nd_local_document_extraction_sections (extraction_id);
        """,
        """
        ALTER TABLE nd_local_document_extractions
          ADD COLUMN IF NOT EXISTS semantic_extract_status TEXT NOT NULL DEFAULT 'pending',
          ADD COLUMN IF NOT EXISTS semantic_section_count INTEGER NULL,
          ADD COLUMN IF NOT EXISTS semantic_sections_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          ADD COLUMN IF NOT EXISTS semantic_warnings_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          ADD COLUMN IF NOT EXISTS semantic_extract_error TEXT NULL,
          ADD COLUMN IF NOT EXISTS semantic_extracted_at TIMESTAMPTZ NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_dictionary_entries (
          id UUID PRIMARY KEY,
          acronym TEXT NOT NULL,
          definition TEXT NOT NULL,
          source TEXT NOT NULL DEFAULT 'auto',
          source_document_id UUID NULL,
          source_page INTEGER NULL,
          is_active BOOLEAN NOT NULL DEFAULT true,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nd_dictionary_entries_pair
          ON nd_dictionary_entries (lower(acronym), lower(definition));
        """,
        """
        ALTER TABLE regul_forward_findings
          ADD COLUMN IF NOT EXISTS retrieval_json JSONB NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_synonym_entries (
          id UUID PRIMARY KEY,
          term_a TEXT NOT NULL,
          term_b TEXT NOT NULL,
          is_active BOOLEAN NOT NULL DEFAULT true,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nd_synonym_entries_pair
          ON nd_synonym_entries (lower(term_a), lower(term_b));
        """,
        """
        ALTER TABLE nd_synonym_entries
          ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'manual',
          ADD COLUMN IF NOT EXISTS source_document_id UUID NULL,
          ADD COLUMN IF NOT EXISTS source_page INTEGER NULL;
        """,
        """
        ALTER TABLE nd_local_document_extractions
          ADD COLUMN IF NOT EXISTS structural_coverage_ratio DOUBLE PRECISION NULL,
          ADD COLUMN IF NOT EXISTS structural_coverage_orphan_snippet TEXT NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS gap_evidence_reruns (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          scope TEXT NOT NULL DEFAULT 'report',
          status TEXT NOT NULL DEFAULT 'queued',
          phase TEXT NOT NULL DEFAULT 'queued',
          phase_detail TEXT NULL,
          evidence_documents_json TEXT NOT NULL DEFAULT '[]',
          total_points INT NOT NULL DEFAULT 0,
          completed_points INT NOT NULL DEFAULT 0,
          failed_points INT NOT NULL DEFAULT 0,
          fulfilled_gaps INT NOT NULL DEFAULT 0,
          partial_gaps INT NOT NULL DEFAULT 0,
          open_gaps INT NOT NULL DEFAULT 0,
          resolved_actions INT NOT NULL DEFAULT 0,
          split_actions INT NOT NULL DEFAULT 0,
          error TEXT NULL,
          llm_provider TEXT NULL,
          llm_model TEXT NULL,
          created_by UUID NULL,
          started_at TIMESTAMPTZ NULL,
          finished_at TIMESTAMPTZ NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_gap_evidence_reruns_run
          ON gap_evidence_reruns (analysis_run_id, created_at DESC);
        """,
        """
        CREATE TABLE IF NOT EXISTS gap_evidence_reviews (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          rerun_id UUID NOT NULL REFERENCES gap_evidence_reruns(id) ON DELETE CASCADE,
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
          gap_index_filter INT NULL,
          status TEXT NOT NULL DEFAULT 'queued',
          clause_no TEXT NULL,
          clause_outcome TEXT NULL,
          prior_final_status TEXT NULL,
          new_final_status TEXT NULL,
          summary TEXT NULL,
          evidence_documents_json TEXT NOT NULL DEFAULT '[]',
          gaps_json TEXT NOT NULL DEFAULT '[]',
          actions_json TEXT NOT NULL DEFAULT '[]',
          context_json TEXT NOT NULL DEFAULT '[]',
          error TEXT NULL,
          started_at TIMESTAMPTZ NULL,
          completed_at TIMESTAMPTZ NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_gap_evidence_reviews_rerun
          ON gap_evidence_reviews (rerun_id);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_gap_evidence_reviews_point
          ON gap_evidence_reviews (analysis_point_id, created_at DESC);
        """,
        """
        ALTER TABLE gap_evidence_reviews ADD COLUMN IF NOT EXISTS reanalysis_json TEXT NULL;
        ALTER TABLE gap_evidence_reviews ADD COLUMN IF NOT EXISTS retrieval_json TEXT NULL;
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_gap_evidence_reviews_run
          ON gap_evidence_reviews (analysis_run_id, created_at DESC);
        """,
        """
        CREATE TABLE IF NOT EXISTS regul_clause_traces (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          finding_id UUID NULL,
          clause_no TEXT NOT NULL DEFAULT '',
          step TEXT NOT NULL,
          attempt INT NOT NULL DEFAULT 0,
          provider TEXT NULL,
          model TEXT NULL,
          system_prompt TEXT NULL,
          context_text TEXT NULL,
          chunks_json TEXT NULL,
          query_text TEXT NULL,
          response_text TEXT NULL,
          result_json TEXT NULL,
          notes TEXT NULL,
          error TEXT NULL,
          chars_sent INT NULL,
          duration_ms INT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_regul_clause_traces_run
          ON regul_clause_traces (analysis_run_id, clause_no, created_at);
        """,
        """
        ALTER TABLE regul_clause_traces ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'analysis';
        """,
        """
        ALTER TABLE regul_clause_traces ADD COLUMN IF NOT EXISTS clause_context TEXT NULL;
        ALTER TABLE regul_clause_traces ADD COLUMN IF NOT EXISTS clause_context_sent BOOLEAN NOT NULL DEFAULT false;
        """,
        """
        ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_pipeline_version INT NULL;
        """,
        """
        ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_prompt_versions TEXT NULL;
        """,
        // The first, run-level eval table (never used) was replaced by clause evals; dropped only if empty.
        """
        DO $$
        BEGIN
          IF to_regclass('nd_analysis_evals') IS NOT NULL
             AND NOT EXISTS (SELECT 1 FROM nd_analysis_evals) THEN
            DROP TABLE nd_analysis_evals;
          END IF;
        END $$;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_clause_evals (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          clause_no TEXT NOT NULL,
          clause_key TEXT NOT NULL,
          clause_title TEXT NOT NULL DEFAULT '',
          clause_text TEXT NOT NULL DEFAULT '',
          regulation_document_id UUID NULL,
          regulation_name TEXT NULL,
          version_number INT NOT NULL,
          is_current BOOLEAN NOT NULL DEFAULT false,
          notes TEXT NULL,
          source_run_id UUID NULL,
          source_run_name TEXT NOT NULL DEFAULT '',
          llm_provider TEXT NULL,
          llm_model TEXT NULL,
          pipeline_version INT NULL,
          overall_status TEXT NOT NULL DEFAULT '',
          gap_count INT NOT NULL DEFAULT 0,
          prompt_versions_json TEXT NOT NULL DEFAULT '[]',
          result_json TEXT NOT NULL DEFAULT '{{}}',
          created_by UUID NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS idx_nd_clause_evals_key ON nd_clause_evals (clause_key, version_number DESC);
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_local_document_passages (
          id UUID PRIMARY KEY,
          extraction_id UUID NOT NULL REFERENCES nd_local_document_extractions(id) ON DELETE CASCADE,
          section_id UUID NOT NULL,
          section_index INTEGER NOT NULL,
          passage_index INTEGER NOT NULL,
          clause_no TEXT NULL,
          heading_path TEXT NOT NULL DEFAULT '',
          passage_text TEXT NOT NULL,
          source_page INTEGER NULL,
          embedding vector NULL,
          embedding_model TEXT NOT NULL DEFAULT '',
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS ix_nd_local_document_passages_extraction
          ON nd_local_document_passages (extraction_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_retrieval_expectations (
          id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
          clause_key TEXT NOT NULL,
          clause_no TEXT NOT NULL,
          expected_json TEXT NOT NULL DEFAULT '[]',
          updated_by UUID NULL,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS idx_nd_retrieval_expectations_key ON nd_retrieval_expectations (clause_key);
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);

        await NdWorkspaceSchemaBootstrap.EnsureAsync(db, ct);
    }
}
