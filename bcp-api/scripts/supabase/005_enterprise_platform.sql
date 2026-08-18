-- BCP: Enterprise compliance platform (new dashboard) — new tables only

-- ── Departments ──
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

-- ── Profiles (extends auth.users) ──
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

-- ── Helper: current user role from profiles (after profiles table exists) ──
CREATE OR REPLACE FUNCTION get_my_role()
RETURNS TEXT AS $$
  SELECT role FROM profiles WHERE id = auth.uid();
$$ LANGUAGE sql STABLE SECURITY DEFINER;

-- ── Regulation documents (dashboard tracking) ──
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

-- ── Regulation points ──
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

-- ── Libraries ──
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

-- ── Library points (junction) ──
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

-- ── Analysis runs (enterprise dashboard) ──
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

-- ── Analysis points (per-point results) ──
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

-- ── Action plan history ──
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

-- ── Analysis reviews ──
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

-- ── Analysis point comments ──
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

-- ── Analysis status history ──
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

-- ═══════════════════════════════════════════════════════
-- ROW LEVEL SECURITY
-- ═══════════════════════════════════════════════════════

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
