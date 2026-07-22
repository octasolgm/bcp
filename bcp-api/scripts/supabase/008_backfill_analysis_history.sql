-- ═══════════════════════════════════════════════════════════════════════════
-- 008_backfill_analysis_history.sql
-- Run in Supabase SQL Editor (service role / postgres).
-- Ensures history tables/columns exist and backfills timeline data from
-- analysis_runs timestamps when review rows were never written.
-- ═══════════════════════════════════════════════════════════════════════════

BEGIN;

-- ── 1. Ensure tables exist ────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS analysis_reviews (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  reviewer_id UUID REFERENCES profiles(id) ON DELETE SET NULL,
  reviewer_role TEXT NOT NULL,
  action TEXT NOT NULL,
  overall_comment TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS analysis_status_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  from_status TEXT,
  to_status TEXT NOT NULL,
  changed_by UUID REFERENCES profiles(id) ON DELETE SET NULL,
  comment TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_reviews_run
  ON analysis_reviews (analysis_run_id);

CREATE INDEX IF NOT EXISTS idx_analysis_status_history_run
  ON analysis_status_history (analysis_run_id);

-- Extra columns used by the API (safe if already present)
ALTER TABLE analysis_reviews ADD COLUMN IF NOT EXISTS review_status TEXT;
ALTER TABLE analysis_reviews ADD COLUMN IF NOT EXISTS priority INTEGER;
ALTER TABLE analysis_reviews ADD COLUMN IF NOT EXISTS responsibility TEXT;
ALTER TABLE analysis_reviews ADD COLUMN IF NOT EXISTS due_date TIMESTAMPTZ;

-- API writes reviewer_role = 'maker' on submit; allow it in the check constraint
ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_reviewer_role_check;
ALTER TABLE analysis_reviews ADD CONSTRAINT analysis_reviews_reviewer_role_check
  CHECK (reviewer_role IN ('maker', 'checker', 'reviewer'));

ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_action_check;
ALTER TABLE analysis_reviews ADD CONSTRAINT analysis_reviews_action_check
  CHECK (action IN ('submitted', 'approved', 'pulled_back', 'finalized'));

-- ── 2. Backfill "Submitted for review" from submitted_to_checker_at ─────────

INSERT INTO analysis_reviews (
  id, analysis_run_id, reviewer_id, reviewer_role, action, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  r.created_by,
  'maker',
  'submitted',
  r.submitted_to_checker_at
FROM analysis_runs r
WHERE r.status <> 'deleted'
  AND r.submitted_to_checker_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_reviews ar
    WHERE ar.analysis_run_id = r.id
      AND ar.action = 'submitted'
      AND ar.created_at BETWEEN r.submitted_to_checker_at - INTERVAL '2 minutes'
                            AND r.submitted_to_checker_at + INTERVAL '2 minutes'
  );

-- Status row: * → submitted_for_review
INSERT INTO analysis_status_history (
  id, analysis_run_id, from_status, to_status, changed_by, comment, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  CASE
    WHEN r.status = 'submitted_for_review' THEN 'completed'
    WHEN r.status = 'pulled_back' THEN 'pulled_back'
    WHEN r.status IN ('checker_approved', 'reviewer_approved') THEN 'completed'
    ELSE NULL
  END,
  'submitted_for_review',
  r.created_by,
  NULL,
  r.submitted_to_checker_at
FROM analysis_runs r
WHERE r.status <> 'deleted'
  AND r.submitted_to_checker_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_status_history h
    WHERE h.analysis_run_id = r.id
      AND h.to_status = 'submitted_for_review'
      AND h.created_at BETWEEN r.submitted_to_checker_at - INTERVAL '2 minutes'
                           AND r.submitted_to_checker_at + INTERVAL '2 minutes'
  );

-- ── 3. Backfill checker approve / pull-back from checker_reviewed_at ──────

-- Checker approved (run reached checker_approved or reviewer_approved)
INSERT INTO analysis_reviews (
  id, analysis_run_id, reviewer_id, reviewer_role, action, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  NULL,
  'checker',
  'approved',
  COALESCE(r.submitted_to_reviewer_at, r.checker_reviewed_at)
FROM analysis_runs r
WHERE r.status <> 'deleted'
  AND r.status IN ('checker_approved', 'reviewer_approved')
  AND r.checker_reviewed_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_reviews ar
    WHERE ar.analysis_run_id = r.id
      AND ar.reviewer_role = 'checker'
      AND ar.action = 'approved'
  );

INSERT INTO analysis_status_history (
  id, analysis_run_id, from_status, to_status, changed_by, comment, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  'submitted_for_review',
  'checker_approved',
  NULL,
  NULL,
  COALESCE(r.submitted_to_reviewer_at, r.checker_reviewed_at)
FROM analysis_runs r
WHERE r.status <> 'deleted'
  AND r.status IN ('checker_approved', 'reviewer_approved')
  AND r.checker_reviewed_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_status_history h
    WHERE h.analysis_run_id = r.id
      AND h.to_status = 'checker_approved'
  );

-- Checker pulled back to maker
INSERT INTO analysis_reviews (
  id, analysis_run_id, reviewer_id, reviewer_role, action, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  NULL,
  'checker',
  'pulled_back',
  r.checker_reviewed_at
FROM analysis_runs r
WHERE r.status = 'pulled_back'
  AND r.checker_reviewed_at IS NOT NULL
  AND r.submitted_to_reviewer_at IS NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_reviews ar
    WHERE ar.analysis_run_id = r.id
      AND ar.reviewer_role = 'checker'
      AND ar.action = 'pulled_back'
  );

INSERT INTO analysis_status_history (
  id, analysis_run_id, from_status, to_status, changed_by, comment, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  'submitted_for_review',
  'pulled_back',
  NULL,
  NULL,
  r.checker_reviewed_at
FROM analysis_runs r
WHERE r.status = 'pulled_back'
  AND r.checker_reviewed_at IS NOT NULL
  AND r.submitted_to_reviewer_at IS NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_status_history h
    WHERE h.analysis_run_id = r.id
      AND h.to_status = 'pulled_back'
  );

-- ── 4. Backfill reviewer finalize ─────────────────────────────────────────

INSERT INTO analysis_reviews (
  id, analysis_run_id, reviewer_id, reviewer_role, action, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  NULL,
  'reviewer',
  'finalized',
  r.reviewer_finalized_at
FROM analysis_runs r
WHERE r.status = 'reviewer_approved'
  AND r.reviewer_finalized_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_reviews ar
    WHERE ar.analysis_run_id = r.id
      AND ar.reviewer_role = 'reviewer'
      AND ar.action = 'finalized'
  );

INSERT INTO analysis_status_history (
  id, analysis_run_id, from_status, to_status, changed_by, comment, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  'checker_approved',
  'reviewer_approved',
  NULL,
  NULL,
  r.reviewer_finalized_at
FROM analysis_runs r
WHERE r.status = 'reviewer_approved'
  AND r.reviewer_finalized_at IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM analysis_status_history h
    WHERE h.analysis_run_id = r.id
      AND h.to_status = 'reviewer_approved'
  );

-- ── 5. Completed analysis (before review workflow) ────────────────────────

INSERT INTO analysis_status_history (
  id, analysis_run_id, from_status, to_status, changed_by, comment, created_at
)
SELECT
  gen_random_uuid(),
  r.id,
  'running',
  'completed',
  r.created_by,
  'Backfilled from run timestamps',
  r.updated_at - INTERVAL '1 second'
FROM analysis_runs r
WHERE r.status IN (
  'completed', 'submitted_for_review', 'checker_approved',
  'reviewer_approved', 'pulled_back'
)
AND NOT EXISTS (
  SELECT 1
  FROM analysis_status_history h
  WHERE h.analysis_run_id = r.id
    AND h.to_status = 'completed'
);

COMMIT;

-- ── 6. Verify (optional) ──────────────────────────────────────────────────
-- Replace the UUID with your run id.

-- SELECT id, name, status, submitted_to_checker_at, checker_reviewed_at
-- FROM analysis_runs
-- WHERE id = '37cadfa3-5e62-40c9-834f-a657ca9f433c';

-- SELECT reviewer_role, action, created_at
-- FROM analysis_reviews
-- WHERE analysis_run_id = '37cadfa3-5e62-40c9-834f-a657ca9f433c'
-- ORDER BY created_at;

-- SELECT from_status, to_status, created_at
-- FROM analysis_status_history
-- WHERE analysis_run_id = '37cadfa3-5e62-40c9-834f-a657ca9f433c'
-- ORDER BY created_at;
