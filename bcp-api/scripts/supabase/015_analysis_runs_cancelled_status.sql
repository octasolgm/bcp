-- Allow user-stopped runs (Stop endpoint sets status = 'cancelled').

ALTER TABLE analysis_runs DROP CONSTRAINT IF EXISTS analysis_runs_status_check;
ALTER TABLE analysis_runs ADD CONSTRAINT analysis_runs_status_check
  CHECK (status IN (
    'draft', 'running', 'landing_ai_complete', 'dual_verify_failed',
    'completed', 'failed', 'cancelled', 'submitted_for_review', 'pulled_back',
    'checker_approved', 'reviewer_approved', 'deleted'
  ));
