-- Point classification flags for introduction / annex (analysis vs display)
ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS is_introduction_point BOOLEAN NOT NULL DEFAULT false;

ALTER TABLE regulation_points
  ADD COLUMN IF NOT EXISTS is_annex_point BOOLEAN NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS idx_regulation_points_intro
  ON regulation_points (regulation_document_id, is_introduction_point);
