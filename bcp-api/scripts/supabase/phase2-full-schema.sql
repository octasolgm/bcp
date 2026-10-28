-- BCP Phase 2 FULL schema (comply-solution-project / prxmkrmwqxlltwjnazay)
-- SAFE: empty schema on NEW project. Old users/data stay on old project until Phase B import.
-- Generated 2026-08-17 19:46

-- ===============================================================
-- 000_phase2_cleanup_partial_bootstrap.sql
-- ===============================================================
-- Phase 2: remove tables left by a failed API bootstrap on an empty Supabase project.
-- Safe on a brand-new project (no real data yet). Run BEFORE phase2-full-schema.sql.

DROP TABLE IF EXISTS nd_system_settings CASCADE;
DROP TABLE IF EXISTS stored_documents CASCADE;
DROP TABLE IF EXISTS landing_ai_parse_cache CASCADE;
DROP TABLE IF EXISTS landing_ai_extract_cache CASCADE;
DROP TABLE IF EXISTS nd_internal_document_sections CASCADE;


-- ===============================================================
-- 002_compliance_sessions.sql
-- ===============================================================
-- BCP: Persist full Landing AI compliance compare sessions (replay for $0)

CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TABLE IF NOT EXISTS landing_ai_compliance_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_key TEXT NOT NULL UNIQUE,
  gov_file_hash TEXT NOT NULL,
  internal_file_hash TEXT NOT NULL,
  gov_file_name TEXT,
  internal_file_name TEXT,
  total_gov_points INTEGER NOT NULL DEFAULT 0,
  compared_points INTEGER NOT NULL DEFAULT 0,
  skipped_points INTEGER NOT NULL DEFAULT 0,
  skipped_json JSONB,
  results_json JSONB NOT NULL,
  summary_json JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_landing_ai_sessions_created
  ON landing_ai_compliance_sessions (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_landing_ai_sessions_gov_hash
  ON landing_ai_compliance_sessions (gov_file_hash);

DROP TRIGGER IF EXISTS trg_landing_ai_sessions_updated ON landing_ai_compliance_sessions;
CREATE TRIGGER trg_landing_ai_sessions_updated
  BEFORE UPDATE ON landing_ai_compliance_sessions
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE landing_ai_compliance_sessions IS
  'Full compare session results â€” reload reports without Landing AI credits';


-- ===============================================================
-- 003_dual_verify_kafka.sql
-- ===============================================================
-- BCP: Kafka dual verify job tracking (session + per-point status)

CREATE TABLE IF NOT EXISTS dual_verify_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  status TEXT NOT NULL DEFAULT 'queued',
  granularity TEXT NOT NULL DEFAULT 'section',
  gov_doc_id TEXT NOT NULL,
  internal_doc_id TEXT NOT NULL,
  gov_file_hash TEXT NOT NULL,
  internal_file_hash TEXT NOT NULL,
  gov_file_name TEXT,
  internal_file_name TEXT,
  total_points INTEGER NOT NULL DEFAULT 0,
  completed_points INTEGER NOT NULL DEFAULT 0,
  failed_points INTEGER NOT NULL DEFAULT 0,
  running_points INTEGER NOT NULL DEFAULT 0,
  phase2_model TEXT,
  pipeline TEXT NOT NULL DEFAULT 'kafka-dual-verify',
  compliance_session_key TEXT,
  summary_json JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS dual_verify_point_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES dual_verify_sessions(id) ON DELETE CASCADE,
  point_id TEXT NOT NULL,
  point_title TEXT,
  gov_text TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'queued',
  attempt INTEGER NOT NULL DEFAULT 1,
  max_attempts INTEGER NOT NULL DEFAULT 3,
  landing_message TEXT,
  llm_message TEXT,
  agreement_json JSONB,
  error_message TEXT,
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (session_id, point_id)
);

CREATE INDEX IF NOT EXISTS idx_dual_verify_sessions_created
  ON dual_verify_sessions (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_dual_verify_point_jobs_session
  ON dual_verify_point_jobs (session_id);

CREATE INDEX IF NOT EXISTS idx_dual_verify_point_jobs_status
  ON dual_verify_point_jobs (status);

DROP TRIGGER IF EXISTS trg_dual_verify_sessions_updated ON dual_verify_sessions;
CREATE TRIGGER trg_dual_verify_sessions_updated
  BEFORE UPDATE ON dual_verify_sessions
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS trg_dual_verify_point_jobs_updated ON dual_verify_point_jobs;
CREATE TRIGGER trg_dual_verify_point_jobs_updated
  BEFORE UPDATE ON dual_verify_point_jobs
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE dual_verify_sessions IS
  'Kafka dual verify batch sessions â€” progress tracked per session';
COMMENT ON TABLE dual_verify_point_jobs IS
  'Per gov point job status for Kafka dual verify pipeline';


-- ===============================================================
-- 004_bcp_api_extra_columns.sql
-- ===============================================================
-- Extra columns used by bcp-api EF entities (safe to re-run)
ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS queued_points INTEGER NOT NULL DEFAULT 0;

ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS transport TEXT NOT NULL DEFAULT 'local';


-- ===============================================================
-- 001_stored_documents_base.sql
-- ===============================================================
-- Base document storage table (required before 005_enterprise_platform.sql)

CREATE TABLE IF NOT EXISTS stored_documents (
  id UUID PRIMARY KEY,
  title TEXT NOT NULL DEFAULT '',
  original_file_name TEXT NOT NULL DEFAULT '',
  file_type TEXT NOT NULL DEFAULT 'PDF',
  category TEXT NOT NULL DEFAULT 'AML/CFT',
  filter_key TEXT NOT NULL DEFAULT 'aml',
  doc_kind TEXT NOT NULL DEFAULT 'document',
  version TEXT NOT NULL DEFAULT 'v1',
  version_number INTEGER NOT NULL DEFAULT 1,
  status TEXT NOT NULL DEFAULT 'review-due',
  gap_count INTEGER NULL,
  pages INTEGER NOT NULL DEFAULT 0,
  size_bytes BIGINT NOT NULL DEFAULT 0,
  content_type TEXT NOT NULL DEFAULT 'application/octet-stream',
  storage_bucket TEXT NOT NULL DEFAULT 'doc',
  storage_path TEXT NOT NULL DEFAULT '',
  file_hash TEXT NULL,
  point_count INTEGER NULL,
  workspace_id TEXT NOT NULL DEFAULT 'snb-uae-difc',
  history_json JSONB NOT NULL DEFAULT '[]'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_stored_documents_workspace_kind_title
  ON stored_documents (workspace_id, doc_kind, title);

CREATE INDEX IF NOT EXISTS ix_stored_documents_file_hash
  ON stored_documents (file_hash);


-- ===============================================================
-- 004b_bcp_api_support_tables.sql
-- ===============================================================
-- Tables/columns normally created by API SupabaseSchemaBootstrap (must exist before 011+).

CREATE TABLE IF NOT EXISTS nd_system_settings (
  key TEXT PRIMARY KEY,
  value_json JSONB NOT NULL DEFAULT jsonb_build_object(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_by UUID NULL
);

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS parse_status TEXT NOT NULL DEFAULT 'pending';

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS parsed_at TIMESTAMPTZ NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS parse_error TEXT NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS uploaded_by UUID NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS parsed_by UUID NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS is_hidden BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS hidden_at TIMESTAMPTZ NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS hidden_by UUID NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS source_storage_path TEXT NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS extraction_cache_key TEXT NULL;

CREATE TABLE IF NOT EXISTS landing_ai_parse_cache (
  file_hash TEXT PRIMARY KEY,
  file_name TEXT NULL,
  markdown TEXT NOT NULL,
  parse_model TEXT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS landing_ai_extract_cache (
  file_hash TEXT NOT NULL,
  schema_key TEXT NOT NULL,
  points_json JSONB NOT NULL DEFAULT jsonb_build_object(),
  extract_model TEXT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (file_hash, schema_key)
);

CREATE TABLE IF NOT EXISTS document_analysis_runs (
  id UUID PRIMARY KEY,
  workspace_id TEXT NOT NULL DEFAULT 'snb-uae-difc',
  internal_document_id UUID NULL,
  regulation_document_id UUID NULL,
  dual_verify_session_id UUID NULL,
  compliance_session_id UUID NULL,
  label TEXT NOT NULL DEFAULT '',
  regulation_file_name TEXT NULL,
  internal_file_name TEXT NULL,
  internal_file_hash TEXT NULL,
  gov_file_hash TEXT NULL,
  status TEXT NOT NULL DEFAULT 'queued',
  point_count INTEGER NOT NULL DEFAULT 0,
  completed_points INTEGER NOT NULL DEFAULT 0,
  granularity TEXT NOT NULL DEFAULT 'leaf',
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE document_analysis_runs
  ALTER COLUMN dual_verify_session_id DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_document_analysis_runs_internal_doc
  ON document_analysis_runs (internal_document_id);

CREATE INDEX IF NOT EXISTS ix_document_analysis_runs_session
  ON document_analysis_runs (dual_verify_session_id);


-- ===============================================================
-- 005_enterprise_platform.sql
-- ===============================================================
-- BCP: Enterprise compliance platform (new dashboard) â€” new tables only

-- â”€â”€ Departments â”€â”€
CREATE TABLE IF NOT EXISTS departments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  description TEXT,
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_departments_name ON departments (name);

DROP TRIGGER IF EXISTS trg_departments_updated ON departments;
CREATE TRIGGER trg_departments_updated
  BEFORE UPDATE ON departments
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Profiles (extends auth.users) â”€â”€
CREATE TABLE IF NOT EXISTS profiles (
  id UUID PRIMARY KEY REFERENCES auth.users(id) ON DELETE CASCADE,
  full_name TEXT NOT NULL DEFAULT '',
  role TEXT NOT NULL DEFAULT 'maker'
    CHECK (role IN ('super_admin', 'maker', 'checker', 'reviewer')),
  department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_profiles_role ON profiles (role);
CREATE INDEX IF NOT EXISTS idx_profiles_department ON profiles (department_id);

DROP TRIGGER IF EXISTS trg_profiles_updated ON profiles;
CREATE TRIGGER trg_profiles_updated
  BEFORE UPDATE ON profiles
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Helper: current user role from profiles (after profiles table exists) â”€â”€
CREATE OR REPLACE FUNCTION get_my_role()
RETURNS TEXT AS $$
  SELECT role FROM profiles WHERE id = auth.uid();
$$ LANGUAGE sql STABLE SECURITY DEFINER;

-- â”€â”€ Regulation documents (dashboard tracking) â”€â”€
CREATE TABLE IF NOT EXISTS regulation_documents (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  stored_document_id UUID REFERENCES stored_documents(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  file_path TEXT NOT NULL DEFAULT '',
  file_url TEXT,
  department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
  extraction_status TEXT NOT NULL DEFAULT 'pending'
    CHECK (extraction_status IN ('pending', 'processing', 'completed', 'failed')),
  extraction_result JSONB,
  extraction_markdown TEXT,
  extracted_at TIMESTAMPTZ,
  extracted_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_regulation_documents_department
  ON regulation_documents (department_id);
CREATE INDEX IF NOT EXISTS idx_regulation_documents_status
  ON regulation_documents (extraction_status);
CREATE INDEX IF NOT EXISTS idx_regulation_documents_stored_doc
  ON regulation_documents (stored_document_id);

DROP TRIGGER IF EXISTS trg_regulation_documents_updated ON regulation_documents;
CREATE TRIGGER trg_regulation_documents_updated
  BEFORE UPDATE ON regulation_documents
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Regulation points â”€â”€
CREATE TABLE IF NOT EXISTS regulation_points (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  regulation_document_id UUID NOT NULL REFERENCES regulation_documents(id) ON DELETE CASCADE,
  point_number TEXT NOT NULL,
  point_title TEXT,
  point_content TEXT NOT NULL,
  page_reference TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_regulation_points_document
  ON regulation_points (regulation_document_id);

-- â”€â”€ Libraries â”€â”€
CREATE TABLE IF NOT EXISTS libraries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  description TEXT,
  department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
  created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_libraries_department ON libraries (department_id);

DROP TRIGGER IF EXISTS trg_libraries_updated ON libraries;
CREATE TRIGGER trg_libraries_updated
  BEFORE UPDATE ON libraries
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Library points (junction) â”€â”€
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

CREATE INDEX IF NOT EXISTS idx_library_points_library ON library_points (library_id);

-- â”€â”€ Analysis runs (enterprise dashboard) â”€â”€
CREATE TABLE IF NOT EXISTS analysis_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  description TEXT,
  library_id UUID REFERENCES libraries(id) ON DELETE SET NULL,
  selected_points_snapshot JSONB NOT NULL DEFAULT '[]'::jsonb,
  selected_internal_doc_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
  selected_regulation_doc_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
  status TEXT NOT NULL DEFAULT 'draft'
    CHECK (status IN (
      'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
      'completed', 'failed', 'submitted_for_review', 'pulled_back',
      'checker_approved', 'reviewer_approved'
    )),
  total_points_count INTEGER NOT NULL DEFAULT 0,
  processed_points_count INTEGER NOT NULL DEFAULT 0,
  landing_ai_completed_count INTEGER NOT NULL DEFAULT 0,
  dual_verify_completed_count INTEGER NOT NULL DEFAULT 0,
  dual_verify_failed_count INTEGER NOT NULL DEFAULT 0,
  department_id UUID REFERENCES departments(id) ON DELETE SET NULL,
  created_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  dual_verify_session_id UUID REFERENCES dual_verify_sessions(id) ON DELETE SET NULL,
  compliance_session_id UUID,
  submitted_to_checker_at TIMESTAMPTZ,
  checker_reviewed_at TIMESTAMPTZ,
  submitted_to_reviewer_at TIMESTAMPTZ,
  reviewer_finalized_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_runs_status ON analysis_runs (status);
CREATE INDEX IF NOT EXISTS idx_analysis_runs_created_by ON analysis_runs (created_by);
CREATE INDEX IF NOT EXISTS idx_analysis_runs_department ON analysis_runs (department_id);

DROP TRIGGER IF EXISTS trg_analysis_runs_updated ON analysis_runs;
CREATE TRIGGER trg_analysis_runs_updated
  BEFORE UPDATE ON analysis_runs
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Analysis points (per-point results) â”€â”€
CREATE TABLE IF NOT EXISTS analysis_points (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  regulation_point_id UUID REFERENCES regulation_points(id) ON DELETE SET NULL,
  point_snapshot JSONB NOT NULL DEFAULT '{}'::jsonb,
  landing_ai_status TEXT NOT NULL DEFAULT 'pending'
    CHECK (landing_ai_status IN (
      'pending', 'running', 'compliant', 'partial_compliant', 'non_compliant', 'failed'
    )),
  landing_ai_result JSONB,
  landing_ai_action_plan TEXT,
  landing_ai_run_at TIMESTAMPTZ,
  landing_ai_error TEXT,
  google_ai_status TEXT NOT NULL DEFAULT 'pending'
    CHECK (google_ai_status IN (
      'pending', 'running', 'compliant', 'partial_compliant', 'non_compliant', 'failed'
    )),
  google_ai_result JSONB,
  google_ai_run_at TIMESTAMPTZ,
  google_ai_error TEXT,
  dual_verify_status TEXT NOT NULL DEFAULT 'pending'
    CHECK (dual_verify_status IN ('pending', 'passed', 'failed', 'skipped')),
  dual_verify_run_at TIMESTAMPTZ,
  final_status TEXT
    CHECK (final_status IS NULL OR final_status IN ('compliant', 'partial_compliant', 'non_compliant')),
  final_action_plan TEXT,
  original_ai_action_plan TEXT,
  landing_ai_rerun_count INTEGER NOT NULL DEFAULT 0,
  dual_verify_rerun_count INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_points_run ON analysis_points (analysis_run_id);

DROP TRIGGER IF EXISTS trg_analysis_points_updated ON analysis_points;
CREATE TRIGGER trg_analysis_points_updated
  BEFORE UPDATE ON analysis_points
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- â”€â”€ Action plan history â”€â”€
CREATE TABLE IF NOT EXISTS action_plan_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  action_plan_content TEXT NOT NULL,
  version_number INTEGER NOT NULL,
  change_type TEXT NOT NULL
    CHECK (change_type IN ('ai_original', 'maker_edit', 'maker_reverted_to_version')),
  reverted_to_version INTEGER,
  changed_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  is_current BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_action_plan_history_point
  ON action_plan_history (analysis_point_id);

-- â”€â”€ Analysis reviews â”€â”€
CREATE TABLE IF NOT EXISTS analysis_reviews (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  reviewer_id UUID REFERENCES profiles(id) ON DELETE SET NULL,
  reviewer_role TEXT NOT NULL CHECK (reviewer_role IN ('checker', 'reviewer')),
  action TEXT NOT NULL CHECK (action IN ('submitted', 'approved', 'pulled_back', 'finalized')),
  overall_comment TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_reviews_run ON analysis_reviews (analysis_run_id);

-- â”€â”€ Analysis point comments â”€â”€
CREATE TABLE IF NOT EXISTS analysis_point_comments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  analysis_review_id UUID REFERENCES analysis_reviews(id) ON DELETE SET NULL,
  comment TEXT NOT NULL,
  commented_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_point_comments_point
  ON analysis_point_comments (analysis_point_id);

-- â”€â”€ Analysis status history â”€â”€
CREATE TABLE IF NOT EXISTS analysis_status_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  from_status TEXT,
  to_status TEXT NOT NULL,
  changed_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  comment TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_status_history_run
  ON analysis_status_history (analysis_run_id);

-- â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
-- ROW LEVEL SECURITY
-- â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

ALTER TABLE departments ENABLE ROW LEVEL SECURITY;
ALTER TABLE profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE regulation_documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE regulation_points ENABLE ROW LEVEL SECURITY;
ALTER TABLE libraries ENABLE ROW LEVEL SECURITY;
ALTER TABLE library_points ENABLE ROW LEVEL SECURITY;
ALTER TABLE analysis_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE analysis_points ENABLE ROW LEVEL SECURITY;
ALTER TABLE action_plan_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE analysis_reviews ENABLE ROW LEVEL SECURITY;
ALTER TABLE analysis_point_comments ENABLE ROW LEVEL SECURITY;
ALTER TABLE analysis_status_history ENABLE ROW LEVEL SECURITY;

-- Departments
CREATE POLICY departments_super_admin_all ON departments
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY departments_read_all ON departments
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));

-- Profiles
CREATE POLICY profiles_super_admin_all ON profiles
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY profiles_read_own ON profiles
  FOR SELECT USING (id = auth.uid() OR get_my_role() IN ('super_admin', 'checker', 'reviewer'));
CREATE POLICY profiles_update_own ON profiles
  FOR UPDATE USING (id = auth.uid());

-- Regulation documents
CREATE POLICY reg_docs_super_admin_all ON regulation_documents
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY reg_docs_read ON regulation_documents
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));
CREATE POLICY reg_docs_maker_insert ON regulation_documents
  FOR INSERT WITH CHECK (get_my_role() IN ('super_admin', 'maker'));
CREATE POLICY reg_docs_maker_update ON regulation_documents
  FOR UPDATE USING (get_my_role() IN ('super_admin', 'maker'));

-- Regulation points
CREATE POLICY reg_points_super_admin_all ON regulation_points
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY reg_points_read ON regulation_points
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));

-- Libraries
CREATE POLICY libraries_super_admin_all ON libraries
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY libraries_read ON libraries
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));
CREATE POLICY libraries_maker_write ON libraries
  FOR INSERT WITH CHECK (get_my_role() IN ('super_admin', 'maker'));
CREATE POLICY libraries_maker_update ON libraries
  FOR UPDATE USING (get_my_role() IN ('super_admin', 'maker') AND created_by = auth.uid());
CREATE POLICY libraries_maker_delete ON libraries
  FOR DELETE USING (get_my_role() IN ('super_admin', 'maker') AND created_by = auth.uid());

-- Library points
CREATE POLICY library_points_super_admin_all ON library_points
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY library_points_read ON library_points
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));
CREATE POLICY library_points_maker_write ON library_points
  FOR ALL USING (get_my_role() IN ('super_admin', 'maker'));

-- Analysis runs
CREATE POLICY analysis_runs_super_admin_all ON analysis_runs
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY analysis_runs_maker_own ON analysis_runs
  FOR ALL USING (get_my_role() = 'maker' AND created_by = auth.uid());
CREATE POLICY analysis_runs_checker_read ON analysis_runs
  FOR SELECT USING (
    get_my_role() = 'checker'
    AND status IN ('submitted_for_review', 'pulled_back', 'checker_approved', 'reviewer_approved')
  );
CREATE POLICY analysis_runs_reviewer_read ON analysis_runs
  FOR SELECT USING (
    get_my_role() = 'reviewer'
    AND status IN ('checker_approved', 'reviewer_approved', 'submitted_for_review')
  );

-- Analysis points
CREATE POLICY analysis_points_super_admin_all ON analysis_points
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY analysis_points_maker_own ON analysis_points
  FOR ALL USING (
    get_my_role() = 'maker'
    AND EXISTS (
      SELECT 1 FROM analysis_runs r
      WHERE r.id = analysis_points.analysis_run_id AND r.created_by = auth.uid()
    )
  );
CREATE POLICY analysis_points_checker_read ON analysis_points
  FOR SELECT USING (
    get_my_role() = 'checker'
    AND EXISTS (
      SELECT 1 FROM analysis_runs r
      WHERE r.id = analysis_points.analysis_run_id
        AND r.status IN ('submitted_for_review', 'pulled_back', 'checker_approved', 'reviewer_approved')
    )
  );
CREATE POLICY analysis_points_reviewer_read ON analysis_points
  FOR SELECT USING (
    get_my_role() = 'reviewer'
    AND EXISTS (
      SELECT 1 FROM analysis_runs r
      WHERE r.id = analysis_points.analysis_run_id
        AND r.status IN ('checker_approved', 'reviewer_approved')
    )
  );

-- Action plan history
CREATE POLICY action_plan_super_admin_all ON action_plan_history
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY action_plan_maker_own ON action_plan_history
  FOR ALL USING (
    get_my_role() = 'maker'
    AND EXISTS (
      SELECT 1 FROM analysis_points ap
      JOIN analysis_runs r ON r.id = ap.analysis_run_id
      WHERE ap.id = action_plan_history.analysis_point_id AND r.created_by = auth.uid()
    )
  );
CREATE POLICY action_plan_checker_read ON action_plan_history
  FOR SELECT USING (get_my_role() IN ('checker', 'reviewer'));

-- Analysis reviews
CREATE POLICY analysis_reviews_super_admin_all ON analysis_reviews
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY analysis_reviews_checker_insert ON analysis_reviews
  FOR INSERT WITH CHECK (get_my_role() = 'checker' AND reviewer_role = 'checker');
CREATE POLICY analysis_reviews_reviewer_insert ON analysis_reviews
  FOR INSERT WITH CHECK (get_my_role() = 'reviewer' AND reviewer_role = 'reviewer');
CREATE POLICY analysis_reviews_read ON analysis_reviews
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));

-- Analysis point comments
CREATE POLICY point_comments_super_admin_all ON analysis_point_comments
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY point_comments_checker_insert ON analysis_point_comments
  FOR INSERT WITH CHECK (get_my_role() IN ('checker', 'reviewer'));
CREATE POLICY point_comments_read ON analysis_point_comments
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));

-- Analysis status history
CREATE POLICY status_history_super_admin_all ON analysis_status_history
  FOR ALL USING (get_my_role() = 'super_admin');
CREATE POLICY status_history_read ON analysis_status_history
  FOR SELECT USING (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));
CREATE POLICY status_history_insert ON analysis_status_history
  FOR INSERT WITH CHECK (get_my_role() IN ('super_admin', 'maker', 'checker', 'reviewer'));

COMMENT ON TABLE profiles IS 'Enterprise dashboard user profiles linked to Supabase auth.users';
COMMENT ON TABLE analysis_runs IS 'Enterprise dashboard analysis runs (separate from document_analysis_runs)';


-- ===============================================================
-- 017_nd_supplemental_tables.sql
-- ===============================================================
-- Extra ND tables/columns from NdSchemaBootstrap (not in 005_enterprise_platform.sql).

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS compare_prompt_version TEXT NULL;

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

ALTER TABLE action_plan_item_reviews
  ADD COLUMN IF NOT EXISTS responsibility TEXT;

ALTER TABLE action_plan_item_reviews
  ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

ALTER TABLE action_plan_item_reviews
  ADD COLUMN IF NOT EXISTS priority TEXT;

ALTER TABLE action_plan_item_reviews
  ADD COLUMN IF NOT EXISTS sort_order INTEGER NOT NULL DEFAULT 0;

ALTER TABLE analysis_reviews
  ADD COLUMN IF NOT EXISTS review_status TEXT;

ALTER TABLE analysis_reviews
  ADD COLUMN IF NOT EXISTS priority INTEGER;

ALTER TABLE analysis_reviews
  ADD COLUMN IF NOT EXISTS responsibility TEXT;

ALTER TABLE analysis_reviews
  ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

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


-- ===============================================================
-- 006_point_classification.sql
-- ===============================================================
-- Point classification flags for introduction / annex (analysis vs display)
ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS is_introduction_point BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS is_annex_point BOOLEAN NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS idx_regulation_points_intro
  ON regulation_points (regulation_document_id, is_introduction_point);


-- ===============================================================
-- 007_analysis_run_soft_delete.sql
-- ===============================================================
-- Soft-delete for analysis runs: status = 'deleted' hides from portal; data retained for restore.

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
  CHECK (status IN (
    'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
    'completed', 'failed', 'cancelled', 'submitted_for_review', 'pulled_back',
    'checker_approved', 'reviewer_approved', 'deleted'
  ));

ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS status_before_delete TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

-- Legacy analyses (document_analysis_runs / dual_verify_sessions) are soft-deleted
-- via a marker row here; the legacy tables themselves are never modified.
CREATE TABLE IF NOT EXISTS hidden_legacy_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  source TEXT NOT NULL,
  legacy_id UUID NOT NULL,
  deleted_by UUID,
  deleted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (source, legacy_id)
);


-- ===============================================================
-- 008_regulation_point_status.sql
-- ===============================================================
-- Soft-delete for regulation points (status 1 = active, -1 = removed)
ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS status INTEGER NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS idx_regulation_points_doc_status
  ON regulation_points (regulation_document_id, status);


-- ===============================================================
-- 009_regul_workflow.sql
-- ===============================================================
-- Regul.ai workflow tables + analysis_runs workflow columns (V3 analyse-regul)

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS workflow_engine TEXT NOT NULL DEFAULT 'bcp_landing';

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS enable_qualitative BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS regul_llm_provider TEXT NULL;

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS regul_llm_model TEXT NULL;

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS regul_pipeline_phase TEXT NULL;

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS regul_pipeline_error TEXT NULL;

ALTER TABLE analysis_runs
  ADD COLUMN IF NOT EXISTS regul_clauses_confirmed_at TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS regul_forward_findings (
  id UUID PRIMARY KEY,
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  analysis_point_id UUID NULL,
  clause_no TEXT NOT NULL DEFAULT '',
  clause_text TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'pending',
  result_json JSONB NULL,
  error_message TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_regul_forward_findings_run
  ON regul_forward_findings (analysis_run_id);

CREATE TABLE IF NOT EXISTS regul_internal_sections (
  id UUID PRIMARY KEY,
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  section_ref TEXT NOT NULL DEFAULT '',
  section_text TEXT NOT NULL DEFAULT '',
  source_doc TEXT NULL,
  source_page INTEGER NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_regul_internal_sections_run
  ON regul_internal_sections (analysis_run_id);

CREATE TABLE IF NOT EXISTS regul_reverse_mappings (
  id UUID PRIMARY KEY,
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  internal_section_id UUID NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  mapping TEXT NULL,
  mapped_clause_nos JSONB NULL,
  result_json JSONB NULL,
  error_message TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_regul_reverse_mappings_run
  ON regul_reverse_mappings (analysis_run_id);

CREATE TABLE IF NOT EXISTS regul_qualitative_assessments (
  id UUID PRIMARY KEY,
  analysis_run_id UUID NOT NULL UNIQUE REFERENCES analysis_runs(id) ON DELETE CASCADE,
  status TEXT NOT NULL DEFAULT 'pending',
  result_json JSONB NULL,
  error_message TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);


-- ===============================================================
-- 010_internal_document_sections.sql
-- ===============================================================
-- Internal document library sections (Regul policy-clauses extract, mirror regulation_points)

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extract_status TEXT NOT NULL DEFAULT 'pending';

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extracted_at TIMESTAMPTZ NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extract_error TEXT NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_count INTEGER NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extracted_by UUID NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extract_progress_label TEXT NULL;

ALTER TABLE stored_documents
  ADD COLUMN IF NOT EXISTS section_extract_progress_pct INTEGER NULL;

CREATE TABLE IF NOT EXISTS nd_internal_document_sections (
  id UUID PRIMARY KEY,
  stored_document_id UUID NOT NULL REFERENCES stored_documents(id) ON DELETE CASCADE,
  section_ref TEXT NOT NULL DEFAULT '',
  section_text TEXT NOT NULL DEFAULT '',
  source_page INTEGER NULL,
  display_order INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_nd_internal_document_sections_doc
  ON nd_internal_document_sections (stored_document_id);


-- ===============================================================
-- 011_landing_ai_extract_cache_schema_key.sql
-- ===============================================================
-- Per-chunk policy section cache stores under schema_key = policy_clauses_v1 (embedded chunk_cache JSON).
-- Legacy CHECK constraint blocked keys like policy_clauses_v1:chunk:0 and caused failed saves after Landing AI calls.
ALTER TABLE landing_ai_extract_cache
  DROP CONSTRAINT IF EXISTS landing_ai_extract_cache_schema_key_check;


-- ===============================================================
-- 012_analysis_runs_list_perf.sql
-- ===============================================================
-- Run manually in Supabase SQL Editor (do NOT run from API startup â€” CREATE INDEX can lock tables).
-- (list by created_at DESC, status filters, maker mineOnly).

-- Active runs list: WHERE status != 'deleted' ORDER BY created_at DESC LIMIT N
CREATE INDEX IF NOT EXISTS idx_analysis_runs_active_created
  ON analysis_runs (created_at DESC)
  WHERE status IS DISTINCT FROM 'deleted';

-- Maker dashboard: WHERE created_by = ? AND status != 'deleted' ORDER BY created_at DESC
CREATE INDEX IF NOT EXISTS idx_analysis_runs_created_by_active_created
  ON analysis_runs (created_by, created_at DESC)
  WHERE status IS DISTINCT FROM 'deleted';

-- Nav counts / queues: COUNT(*) FILTER (WHERE status = ...)
CREATE INDEX IF NOT EXISTS idx_analysis_runs_status_created
  ON analysis_runs (status, created_at DESC);

-- In-progress sidebar badge: status + point progress columns
CREATE INDEX IF NOT EXISTS idx_analysis_runs_in_progress
  ON analysis_runs (status, total_points_count, processed_points_count)
  WHERE status IN ('running', 'processing')
     OR (total_points_count > 0 AND processed_points_count < total_points_count);

-- Point status aggregation for summaryOnly list (MapSummariesLightAsync)
CREATE INDEX IF NOT EXISTS idx_analysis_points_run_status
  ON analysis_points (analysis_run_id, final_status, landing_ai_status);


-- ===============================================================
-- 014_temp_point_review_comments.sql
-- ===============================================================
-- Temporary manual review notes per analysis point (remove table when workflow is finalized).

CREATE OR REPLACE FUNCTION is_nd_super_admin()
RETURNS BOOLEAN AS $$
  SELECT get_my_role() = 'super_admin';
$$ LANGUAGE sql STABLE SECURITY DEFINER;

CREATE OR REPLACE FUNCTION is_nd_authenticated()
RETURNS BOOLEAN AS $$
  SELECT auth.uid() IS NOT NULL;
$$ LANGUAGE sql STABLE SECURITY DEFINER;

CREATE TABLE IF NOT EXISTS temp_point_review_comments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  comment TEXT NOT NULL,
  commented_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_temp_point_review_comments_point
  ON temp_point_review_comments (analysis_point_id);

ALTER TABLE temp_point_review_comments ENABLE ROW LEVEL SECURITY;

CREATE POLICY temp_point_review_comments_super_admin_all ON temp_point_review_comments
  FOR ALL USING (public.is_nd_super_admin());

CREATE POLICY temp_point_review_comments_read ON temp_point_review_comments
  FOR SELECT USING (public.is_nd_authenticated());

CREATE POLICY temp_point_review_comments_insert ON temp_point_review_comments
  FOR INSERT WITH CHECK (public.is_nd_authenticated());

CREATE POLICY temp_point_review_comments_delete_own ON temp_point_review_comments
  FOR DELETE USING (
    public.is_nd_super_admin()
    OR commented_by = auth.uid()
  );


-- ===============================================================
-- 015_analysis_runs_cancelled_status.sql
-- ===============================================================
-- Allow user-stopped runs (Stop endpoint sets status = 'cancelled').

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
  CHECK (status IN (
    'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
    'completed', 'failed', 'cancelled', 'submitted_for_review', 'pulled_back',
    'checker_approved', 'reviewer_approved', 'deleted'
  ));


-- ===============================================================
-- 016_analysis_action_plans.sql
-- ===============================================================
-- 016: Corrective action plans per gap, their review comments, and target-date audit trail.
-- Replaces the planning half of action_plan_item_reviews (kept for backward compatibility).

CREATE TABLE IF NOT EXISTS analysis_action_plans (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  gap_index INT NOT NULL DEFAULT 0,
  action_plan TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'pending',
  priority TEXT NOT NULL DEFAULT 'medium',
  priority_score INT NOT NULL DEFAULT 50,
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

-- Additive columns for pre-existing installs.
ALTER TABLE analysis_action_plans ADD COLUMN IF NOT EXISTS gap_index INT NOT NULL DEFAULT 0;
ALTER TABLE analysis_action_plans ADD COLUMN IF NOT EXISTS priority_score INT NOT NULL DEFAULT 50;

CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_point
  ON analysis_action_plans (analysis_point_id, gap_index, sort_order);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_run
  ON analysis_action_plans (analysis_run_id, priority, status);

-- Owners of an action. Several departments and/or people can share one action; each
-- row here feeds the assignee's inbox.
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

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_plan
  ON analysis_action_plan_assignees (action_plan_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_user
  ON analysis_action_plan_assignees (user_id);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_department
  ON analysis_action_plan_assignees (department_id);

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

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_reviews_plan
  ON analysis_action_plan_reviews (action_plan_id, created_at);

CREATE TABLE IF NOT EXISTS analysis_action_plan_date_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
  previous_target_date TIMESTAMPTZ NULL,
  new_target_date TIMESTAMPTZ NULL,
  reason TEXT NULL,
  changed_by UUID NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_date_history_plan
  ON analysis_action_plan_date_history (action_plan_id, created_at DESC);


-- ===============================================================
-- 018_demo_analysis_templates.sql
-- ===============================================================
-- Demo workspace templates (seeded by API on first startup; empty tables required).

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

CREATE INDEX IF NOT EXISTS idx_demo_analysis_template_points_template
  ON demo_analysis_template_points (template_id, sort_order);



