-- Run BEFORE importing dual_verify_point_jobs CSV.
-- Parent table dual_verify_sessions must exist first (import its CSV before this one).

ALTER TABLE dual_verify_point_jobs DROP CONSTRAINT IF EXISTS dual_verify_point_jobs_session_id_fkey;

-- Optional: if sessions CSV not imported yet, drop parent FK prep too:
-- ALTER TABLE dual_verify_sessions DROP CONSTRAINT IF EXISTS ...;
