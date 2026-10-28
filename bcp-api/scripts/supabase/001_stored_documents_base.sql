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
