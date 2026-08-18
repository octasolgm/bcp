-- Run on NEW project before direct table copy (preserves all JSON/text data).
-- CSV import is NOT needed when using scripts/copy-supabase-table.ps1

ALTER TABLE regulation_documents
  ADD COLUMN IF NOT EXISTS page_count INTEGER NULL;

ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_stored_document_id_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_department_id_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_created_by_fkey;
ALTER TABLE regulation_documents DROP CONSTRAINT IF EXISTS regulation_documents_extracted_by_fkey;

-- Re-add after all data is copied and UUIDs are fixed:
-- ALTER TABLE regulation_documents ADD CONSTRAINT regulation_documents_stored_document_id_fkey ...
