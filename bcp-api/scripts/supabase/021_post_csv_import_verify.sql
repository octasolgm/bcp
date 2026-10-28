-- Run after import-all-csv.ps1 — check row counts
SELECT 'departments' AS t, COUNT(*) FROM departments
UNION ALL SELECT 'stored_documents', COUNT(*) FROM stored_documents
UNION ALL SELECT 'regulation_documents', COUNT(*) FROM regulation_documents
UNION ALL SELECT 'regulation_points', COUNT(*) FROM regulation_points
UNION ALL SELECT 'libraries', COUNT(*) FROM libraries
UNION ALL SELECT 'library_points', COUNT(*) FROM library_points
UNION ALL SELECT 'analysis_runs', COUNT(*) FROM analysis_runs
UNION ALL SELECT 'analysis_points', COUNT(*) FROM analysis_points
UNION ALL SELECT 'action_plan_history', COUNT(*) FROM action_plan_history
UNION ALL SELECT 'temp_point_review_comments', COUNT(*) FROM temp_point_review_comments
ORDER BY 1;
