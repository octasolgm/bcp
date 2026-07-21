-- Run in Supabase → SQL Editor (one paste, click Run)
-- Skips 001 if you already have landing_ai_jobs / extract cache working.

-- === 002 compliance sessions ===
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

-- set_updated_at helper (from 001) — safe if already exists
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_landing_ai_sessions_updated ON landing_ai_compliance_sessions;
CREATE TRIGGER trg_landing_ai_sessions_updated
  BEFORE UPDATE ON landing_ai_compliance_sessions
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- === 003 kafka dual verify ===
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

-- === 004 bcp-api extra columns ===
ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS queued_points INTEGER NOT NULL DEFAULT 0;

ALTER TABLE dual_verify_sessions
  ADD COLUMN IF NOT EXISTS transport TEXT NOT NULL DEFAULT 'local';
