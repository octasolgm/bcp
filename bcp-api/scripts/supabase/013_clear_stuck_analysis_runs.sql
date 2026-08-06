-- Emergency: free Supabase pool when runs are stuck in 'running' with no active worker.
-- Run in Supabase SQL Editor if dashboard requests time out at 90s.

-- Optional: terminate other sessions from this role (use carefully on shared DBs).
-- SELECT pg_terminate_backend(pid)
-- FROM pg_stat_activity
-- WHERE pid <> pg_backend_pid()
--   AND usename = current_user
--   AND state IN ('active', 'idle in transaction');

UPDATE analysis_runs
SET status = 'failed', updated_at = now()
WHERE status IN ('running', 'processing');

UPDATE analysis_points
SET landing_ai_status = CASE WHEN landing_ai_status IN ('pending', 'running') THEN 'cancelled' ELSE landing_ai_status END,
    dual_verify_status = CASE WHEN dual_verify_status IN ('pending', 'running') THEN 'cancelled' ELSE dual_verify_status END,
    google_ai_status = CASE WHEN google_ai_status IN ('pending', 'running') THEN 'cancelled' ELSE google_ai_status END,
    updated_at = now()
WHERE analysis_run_id IN (
  SELECT id FROM analysis_runs WHERE status = 'failed' AND updated_at > now() - interval '1 minute'
);
