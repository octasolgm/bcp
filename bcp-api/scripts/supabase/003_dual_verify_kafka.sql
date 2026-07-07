-- BCP: Kafka dual verify job tracking (session + per-point status)

CREATE TABLE IF NOT EXISTS dual_verify_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  status TEXT NOT NULL DEFAULT 'queued',
  granularity TEXT NOT NULL DEFAULT 'section',
  gov_doc_id TEXT NOT NULL,
  internal_doc_id TEXT NOT NULL,
  gov_file_hash TEXT NOT NULL,
  internal_file_hash TEXT NOT NULL,
  gov_file_name TEXT,
  internal_file_name TEXT,
  total_points INTEGER NOT NULL DEFAULT 0,
  completed_points INTEGER NOT NULL DEFAULT 0,
  failed_points INTEGER NOT NULL DEFAULT 0,
  running_points INTEGER NOT NULL DEFAULT 0,
  phase2_model TEXT,
  pipeline TEXT NOT NULL DEFAULT 'kafka-dual-verify',
  compliance_session_key TEXT,
  summary_json JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  completed_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS dual_verify_point_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES dual_verify_sessions(id) ON DELETE CASCADE,
  point_id TEXT NOT NULL,
  point_title TEXT,
  gov_text TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'queued',
  attempt INTEGER NOT NULL DEFAULT 1,
  max_attempts INTEGER NOT NULL DEFAULT 3,
  landing_message TEXT,
  llm_message TEXT,
  agreement_json JSONB,
  error_message TEXT,
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (session_id, point_id)
);

CREATE INDEX IF NOT EXISTS idx_dual_verify_sessions_created
  ON dual_verify_sessions (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_dual_verify_point_jobs_session
  ON dual_verify_point_jobs (session_id);

CREATE INDEX IF NOT EXISTS idx_dual_verify_point_jobs_status
  ON dual_verify_point_jobs (status);

DROP TRIGGER IF EXISTS trg_dual_verify_sessions_updated ON dual_verify_sessions;
CREATE TRIGGER trg_dual_verify_sessions_updated
  BEFORE UPDATE ON dual_verify_sessions
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS trg_dual_verify_point_jobs_updated ON dual_verify_point_jobs;
CREATE TRIGGER trg_dual_verify_point_jobs_updated
  BEFORE UPDATE ON dual_verify_point_jobs
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

COMMENT ON TABLE dual_verify_sessions IS
  'Kafka dual verify batch sessions — progress tracked per session';
COMMENT ON TABLE dual_verify_point_jobs IS
  'Per gov point job status for Kafka dual verify pipeline';
