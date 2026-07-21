-- Extra columns used by bcp-api EF entities (safe to re-run)
ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS queued_points INTEGER NOT NULL DEFAULT 0;

ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS transport TEXT NOT NULL DEFAULT 'local';
