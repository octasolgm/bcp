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
