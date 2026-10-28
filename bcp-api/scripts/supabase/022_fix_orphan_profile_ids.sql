-- Remap OLD Supabase profile UUIDs → NEW profile UUIDs after CSV import.
-- Run ONCE on NEW project (prxmkrmwqxlltwjnazay) SQL Editor.
--
-- WHY: Users were recreated in new Auth with new UUIDs. Imported rows still
--      reference old IDs (45228e70, b7b2090c, etc.) → wrong maker names,
--      demo filter bugs, orphan FKs.

-- ═══════════════════════════════════════════════════════════════════════════
-- OLD (ss2)                    →  NEW (ss1)
-- ═══════════════════════════════════════════════════════════════════════════
-- 45228e70… Demo Admin         →  9ceb6574… Demo Admin
-- b7b2090c… superadmin         →  e25f4c1b… Super Admin
-- 215113d1… Demo Checker       →  a39dfb14… Demo Checker
-- 818b1828… Demo Reviewer      →  434a2ac4… Demo Reviewer
-- 8ad164e9… Demo Maker         →  f19f5220… Demo Maker
-- 5643754a… maker              →  5cb9363d… maker
-- f27bf61a… checker            →  9c05ad91… checker
-- 7a55f74c… octasol.gm         →  96f42898… gm rehman (super_admin)

BEGIN;

CREATE TEMP TABLE profile_id_map (old_id UUID PRIMARY KEY, new_id UUID NOT NULL) ON COMMIT DROP;

INSERT INTO profile_id_map (old_id, new_id) VALUES
  ('45228e70-ff64-4a4a-87b6-7b42d1477768', '9ceb6574-1702-44fe-ae7b-f07d96c1697c'),
  ('b7b2090c-2627-476f-9b7c-411bdffd2346', 'e25f4c1b-1610-465d-a512-92c85f195d04'),
  ('215113d1-784b-4018-bcf1-e69f10928d42', 'a39dfb14-bcaf-4705-899a-9f36c1d2e6ec'),
  ('818b1828-1034-450e-9395-cf394b66ccbd', '434a2ac4-b5a6-48fc-98e9-2787651ecaae'),
  ('8ad164e9-f55e-461d-91ee-0cddca41e36c', 'f19f5220-2d02-42c6-a441-c3f64b830e70'),
  ('5643754a-f238-4e61-81f2-891776eb9ef4', '5cb9363d-d154-4214-a821-794b9b82548e'),
  ('f27bf61a-d1f2-4bb2-9a1b-42755137dd2f', '9c05ad91-75ca-430a-b76c-c20f67ca42e0'),
  ('7a55f74c-c55f-4c5f-b758-bc93e048dcbe', '96f42898-580e-42a5-92f7-89d2c5db721b');

-- Preview orphans before fix
SELECT 'BEFORE' AS phase, tbl, col, orphan_id, cnt FROM (
  SELECT 'analysis_runs' AS tbl, 'created_by' AS col, r.created_by AS orphan_id, COUNT(*) AS cnt
  FROM analysis_runs r WHERE r.created_by IN (SELECT old_id FROM profile_id_map) GROUP BY r.created_by
  UNION ALL
  SELECT 'regulation_documents', 'created_by', r.created_by, COUNT(*)
  FROM regulation_documents r WHERE r.created_by IN (SELECT old_id FROM profile_id_map) GROUP BY r.created_by
  UNION ALL
  SELECT 'stored_documents', 'uploaded_by', r.uploaded_by, COUNT(*)
  FROM stored_documents r WHERE r.uploaded_by IN (SELECT old_id FROM profile_id_map) GROUP BY r.uploaded_by
) x ORDER BY tbl, col;

-- ── Tables with profile FK columns ─────────────────────────────────────────
UPDATE analysis_runs t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE regulation_documents t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE regulation_documents t SET extracted_by = m.new_id
FROM profile_id_map m WHERE t.extracted_by = m.old_id;

UPDATE libraries t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE departments t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE stored_documents t SET uploaded_by = m.new_id
FROM profile_id_map m WHERE t.uploaded_by = m.old_id;

UPDATE action_plan_history t SET changed_by = m.new_id
FROM profile_id_map m WHERE t.changed_by = m.old_id;

UPDATE analysis_reviews t SET reviewer_id = m.new_id
FROM profile_id_map m WHERE t.reviewer_id = m.old_id;

UPDATE analysis_point_comments t SET commented_by = m.new_id
FROM profile_id_map m WHERE t.commented_by = m.old_id;

UPDATE analysis_status_history t SET changed_by = m.new_id
FROM profile_id_map m WHERE t.changed_by = m.old_id;

UPDATE analysis_action_plans t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE analysis_action_plans t SET updated_by = m.new_id
FROM profile_id_map m WHERE t.updated_by = m.old_id;

UPDATE analysis_action_plans t SET responsibility_user_id = m.new_id
FROM profile_id_map m WHERE t.responsibility_user_id = m.old_id;

UPDATE analysis_action_plan_assignees t SET user_id = m.new_id
FROM profile_id_map m WHERE t.user_id = m.old_id;

UPDATE action_plan_item_reviews t SET reviewed_by = m.new_id
FROM profile_id_map m WHERE t.reviewed_by = m.old_id;

UPDATE temp_point_review_comments t SET commented_by = m.new_id
FROM profile_id_map m WHERE t.commented_by = m.old_id;

UPDATE profiles t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE nd_analysis_prompt_suggestions t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

UPDATE nd_analysis_prompt_versions t SET created_by = m.new_id
FROM profile_id_map m WHERE t.created_by = m.old_id;

-- Any remaining orphan UUIDs not in profiles (safety net)
UPDATE analysis_runs SET created_by = 'e25f4c1b-1610-465d-a512-92c85f195d04'
WHERE created_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = analysis_runs.created_by);

UPDATE regulation_documents SET created_by = 'e25f4c1b-1610-465d-a512-92c85f195d04'
WHERE created_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = regulation_documents.created_by);

UPDATE stored_documents SET uploaded_by = 'e25f4c1b-1610-465d-a512-92c85f195d04'
WHERE uploaded_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = stored_documents.uploaded_by);

COMMIT;

-- Verify: should return 0 rows
SELECT 'analysis_runs' AS tbl, created_by AS orphan_id, COUNT(*) AS cnt
FROM analysis_runs
WHERE created_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = analysis_runs.created_by)
GROUP BY created_by
UNION ALL
SELECT 'regulation_documents', created_by, COUNT(*)
FROM regulation_documents
WHERE created_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = regulation_documents.created_by)
GROUP BY created_by
UNION ALL
SELECT 'stored_documents', uploaded_by, COUNT(*)
FROM stored_documents
WHERE uploaded_by IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM profiles p WHERE p.id = stored_documents.uploaded_by)
GROUP BY uploaded_by;
