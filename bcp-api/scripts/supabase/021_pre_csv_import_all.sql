-- Run ONCE on NEW project before import-all-csv.ps1
-- Drops FK checks so old UUIDs and parent order don't block COPY import.

ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_stored_document_id_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_department_id_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_created_by_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_extracted_by_fkey;

ALTER TABLE regulation_points DROP CONSTRAINT IF EXISTS regulation_points_regulation_document_id_fkey;

ALTER TABLE libraries DROP CONSTRAINT IF EXISTS libraries_created_by_fkey;
ALTER TABLE libraries DROP CONSTRAINT IF EXISTS libraries_department_id_fkey;

ALTER TABLE library_points DROP CONSTRAINT IF EXISTS library_points_library_id_fkey;
ALTER TABLE library_points DROP CONSTRAINT IF EXISTS library_points_regulation_point_id_fkey;
ALTER TABLE library_points DROP CONSTRAINT IF EXISTS library_points_regulation_document_id_fkey;

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_created_by_fkey;
ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_department_id_fkey;
ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_library_id_fkey;
ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_dual_verify_session_id_fkey;

ALTER TABLE analysis_points DROP CONSTRAINT IF EXISTS analysis_points_analysis_run_id_fkey;
ALTER TABLE analysis_points DROP CONSTRAINT IF EXISTS analysis_points_regulation_point_id_fkey;

ALTER TABLE action_plan_history DROP CONSTRAINT IF EXISTS action_plan_history_analysis_point_id_fkey;
ALTER TABLE action_plan_history DROP CONSTRAINT IF EXISTS action_plan_history_changed_by_fkey;

ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_analysis_run_id_fkey;
ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_reviewer_id_fkey;

ALTER TABLE analysis_point_comments DROP CONSTRAINT IF EXISTS analysis_point_comments_analysis_point_id_fkey;
ALTER TABLE analysis_point_comments DROP CONSTRAINT IF EXISTS analysis_point_comments_commented_by_fkey;

ALTER TABLE temp_point_review_comments DROP CONSTRAINT IF EXISTS temp_point_review_comments_analysis_point_id_fkey;

-- Extra columns old exports may include
ALTER TABLE regulation_documents ADD COLUMN IF NOT EXISTS page_count INTEGER NULL;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS status_before_delete TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS compare_prompt_version TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS workflow_engine TEXT NOT NULL DEFAULT 'bcp_landing';
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS enable_qualitative BOOLEAN NOT NULL DEFAULT false;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_llm_provider TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_llm_model TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_pipeline_phase TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_pipeline_error TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS regul_clauses_confirmed_at TIMESTAMPTZ;

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
  CHECK (status IN (
    'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
    'completed', 'failed', 'cancelled', 'submitted_for_review', 'pulled_back',
    'checker_approved', 'reviewer_approved', 'deleted'
  ));

ALTER TABLE analysis_points DROP CONSTRAINT IF EXISTS analysis_points_dual_verify_status_check;
ALTER TABLE analysis_points ADD CONSTRAINT analysis_points_dual_verify_status_check
  CHECK (dual_verify_status IN (
    'pending', 'running', 'passed', 'failed', 'skipped', 'cancelled', 'completed'
  ));
