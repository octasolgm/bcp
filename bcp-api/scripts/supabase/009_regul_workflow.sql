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
