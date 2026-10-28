-- Run on NEW Supabase project before CSV imports from OLD project.
-- Adds columns that exist in old exports but may be missing on fresh schema.

-- regulation_documents
ALTER TABLE regulation_documents
  ADD COLUMN IF NOT EXISTS page_count INTEGER NULL;

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

-- Verify (should list page_count):
-- SELECT column_name FROM information_schema.columns
-- WHERE table_schema = 'public' AND table_name = 'regulation_documents'
-- ORDER BY ordinal_position;
