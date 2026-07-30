-- Soft-delete for regulation points (status 1 = active, -1 = removed)
ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS status INTEGER NOT NULL DEFAULT 1;

CREATE INDEX IF NOT EXISTS idx_regulation_points_doc_status
  ON regulation_points (regulation_document_id, status);
