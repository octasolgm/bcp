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
