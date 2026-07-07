-- BCP: Persist full Landing AI compliance compare sessions (replay for $0)

CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TABLE IF NOT EXISTS landing_ai_compliance_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_key TEXT NOT NULL UNIQUE,
  gov_file_hash TEXT NOT NULL,
  internal_file_hash TEXT NOT NULL,
  gov_file_name TEXT,
  internal_file_name TEXT,
  total_gov_points INTEGER NOT NULL DEFAULT 0,
  compared_points INTEGER NOT NULL DEFAULT 0,
  skipped_points INTEGER NOT NULL DEFAULT 0,
  skipped_json JSONB,
  results_json JSONB NOT NULL,
  summary_json JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_landing_ai_sessions_created
  ON landing_ai_compliance_sessions (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_landing_ai_sessions_gov_hash
  ON landing_ai_compliance_sessions (gov_file_hash);

DROP TRIGGER IF EXISTS trg_landing_ai_sessions_updated ON landing_ai_compliance_sessions;
CREATE TRIGGER trg_landing_ai_sessions_updated
  BEFORE UPDATE ON landing_ai_compliance_sessions
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE landing_ai_compliance_sessions IS
  'Full compare session results — reload reports without Landing AI credits';
