-- Soft-delete for analysis runs: status = 'deleted' hides from portal; data retained for restore.

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
  CHECK (status IN (
    'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
    'completed', 'failed', 'submitted_for_review', 'pulled_back',
    'checker_approved', 'reviewer_approved', 'deleted'
  ));

ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS status_before_delete TEXT;
ALTER TABLE analysis_runs ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

-- Legacy analyses (document_analysis_runs / dual_verify_sessions) are soft-deleted
-- via a marker row here; the legacy tables themselves are never modified.
CREATE TABLE IF NOT EXISTS hidden_legacy_runs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  source TEXT NOT NULL,
  legacy_id UUID NOT NULL,
  deleted_by UUID,
  deleted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (source, legacy_id)
);
