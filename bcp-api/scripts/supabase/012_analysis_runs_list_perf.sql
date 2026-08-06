-- Run manually in Supabase SQL Editor (do NOT run from API startup — CREATE INDEX can lock tables).
-- (list by created_at DESC, status filters, maker mineOnly).

-- Active runs list: WHERE status != 'deleted' ORDER BY created_at DESC LIMIT N
CREATE INDEX IF NOT EXISTS idx_analysis_runs_active_created
  ON analysis_runs (created_at DESC)
  WHERE status IS DISTINCT FROM 'deleted';

-- Maker dashboard: WHERE created_by = ? AND status != 'deleted' ORDER BY created_at DESC
CREATE INDEX IF NOT EXISTS idx_analysis_runs_created_by_active_created
  ON analysis_runs (created_by, created_at DESC)
  WHERE status IS DISTINCT FROM 'deleted';

-- Nav counts / queues: COUNT(*) FILTER (WHERE status = ...)
CREATE INDEX IF NOT EXISTS idx_analysis_runs_status_created
  ON analysis_runs (status, created_at DESC);

-- In-progress sidebar badge: status + point progress columns
CREATE INDEX IF NOT EXISTS idx_analysis_runs_in_progress
  ON analysis_runs (status, total_points_count, processed_points_count)
  WHERE status IN ('running', 'processing')
     OR (total_points_count > 0 AND processed_points_count < total_points_count);

-- Point status aggregation for summaryOnly list (MapSummariesLightAsync)
CREATE INDEX IF NOT EXISTS idx_analysis_points_run_status
  ON analysis_points (analysis_run_id, final_status, landing_ai_status);
