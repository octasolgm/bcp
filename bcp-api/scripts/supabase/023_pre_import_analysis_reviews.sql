-- Run BEFORE importing analysis_reviews CSV from old project.
-- Fixes: reviewer_role = 'maker' (submit for review) rejected by old constraint.

ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_reviewer_id_fkey;
ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_analysis_run_id_fkey;

ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_reviewer_role_check;
ALTER TABLE analysis_reviews ADD CONSTRAINT analysis_reviews_reviewer_role_check
  CHECK (reviewer_role IN ('maker', 'checker', 'reviewer', 'super_admin'));

ALTER TABLE analysis_reviews DROP CONSTRAINT IF EXISTS analysis_reviews_action_check;
ALTER TABLE analysis_reviews ADD CONSTRAINT analysis_reviews_action_check
  CHECK (action IN ('submitted', 'approved', 'pulled_back', 'finalized', 'rejected', 'need_modify'));

-- After CSV import, run 022_fix_orphan_profile_ids.sql to remap reviewer_id UUIDs.
