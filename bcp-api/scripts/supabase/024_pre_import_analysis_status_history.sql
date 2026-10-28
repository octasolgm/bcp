-- Run BEFORE importing analysis_status_history CSV from old project.
-- FK on changed_by blocks old profile UUIDs (e.g. b7b2090c → superadmin).

ALTER TABLE analysis_status_history DROP CONSTRAINT IF EXISTS analysis_status_history_analysis_run_id_fkey;
ALTER TABLE analysis_status_history DROP CONSTRAINT IF EXISTS analysis_status_history_changed_by_fkey;

-- After CSV import, run 022_fix_orphan_profile_ids.sql (updates changed_by).
