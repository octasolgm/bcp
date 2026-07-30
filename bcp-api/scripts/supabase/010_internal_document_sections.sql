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
