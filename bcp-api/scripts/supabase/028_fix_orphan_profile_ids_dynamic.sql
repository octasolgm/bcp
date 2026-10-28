-- FULL dynamic remap: all profile-like UUID columns in public schema.
-- Replaces hand-maintained per-table UPDATEs in 022.
-- Run ONCE after all CSV imports complete.
--
-- 022 covers ~15 tables manually. This script auto-discovers every column
-- named *_by, user_id, reviewer_id, responsibility_user_id, etc.
-- Examples 022 misses: stored_documents.parsed_by/hidden_by,
-- nd_internal_document_sections.section_extracted_by,
-- analysis_action_plans.deleted_by/resolved_by, dual_verify_* tables, etc.

BEGIN;

CREATE TEMP TABLE profile_id_map (old_id UUID PRIMARY KEY, new_id UUID NOT NULL) ON COMMIT DROP;

INSERT INTO profile_id_map (old_id, new_id) VALUES
  ('45228e70-ff64-4a4a-87b6-7b42d1477768', '9ceb6574-1702-44fe-ae7b-f07d96c1697c'), -- Demo Admin
  ('b7b2090c-2627-476f-9b7c-411bdffd2346', 'e25f4c1b-1610-465d-a512-92c85f195d04'), -- Super Admin
  ('215113d1-784b-4018-bcf1-e69f10928d42', 'a39dfb14-bcaf-4705-899a-9f36c1d2e6ec'), -- Demo Checker
  ('818b1828-1034-450e-9395-cf394b66ccbd', '434a2ac4-b5a6-48fc-98e9-2787651ecaae'), -- Demo Reviewer
  ('8ad164e9-f55e-461d-91ee-0cddca41e36c', 'f19f5220-2d02-42c6-a441-c3f64b830e70'), -- Demo Maker
  ('5643754a-f238-4e61-81f2-891776eb9ef4', '5cb9363d-d154-4214-a821-794b9b82548e'), -- maker
  ('f27bf61a-d1f2-4bb2-9a1b-42755137dd2f', '9c05ad91-75ca-430a-b76c-c20f67ca42e0'), -- checker
  ('7a55f74c-c55f-4c5f-b758-bc93e048dcbe', '96f42898-580e-42a5-92f7-89d2c5db721b'); -- gm rehman

DO $$
DECLARE
  r RECORD;
  upd TEXT;
  n BIGINT;
BEGIN
  RAISE NOTICE '=== Remapping known old profile UUIDs ===';
  FOR r IN
    SELECT table_name, column_name
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND udt_name = 'uuid'
      AND table_name <> 'profiles'
      AND (
        column_name IN (
          'created_by', 'updated_by', 'uploaded_by', 'parsed_by', 'hidden_by',
          'extracted_by', 'changed_by', 'reviewer_id', 'commented_by',
          'reviewed_by', 'deleted_by', 'resolved_by', 'responsibility_user_id',
          'user_id', 'section_extracted_by'
        )
        OR column_name LIKE '%\_by' ESCAPE '\'
      )
      AND column_name NOT IN (
        'id', 'analysis_run_id', 'analysis_point_id', 'library_id',
        'department_id', 'regulation_document_id', 'regulation_point_id',
        'stored_document_id', 'template_id', 'compliance_session_id',
        'dual_verify_session_id', 'internal_document_id', 'legacy_id'
      )
    ORDER BY table_name, column_name
  LOOP
    upd := format(
      'UPDATE public.%I t SET %I = m.new_id FROM profile_id_map m WHERE t.%I = m.old_id',
      r.table_name, r.column_name, r.column_name
    );
    EXECUTE upd;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n > 0 THEN
      RAISE NOTICE 'Updated %.% → % row(s)', r.table_name, r.column_name, n;
    END IF;
  END LOOP;
END $$;

COMMIT;

-- Verify: list any remaining orphans (should be empty or only unknown old UUIDs)
DO $$
DECLARE
  r RECORD;
  q TEXT;
  rec RECORD;
BEGIN
  RAISE NOTICE '=== Remaining orphans after fix ===';
  FOR r IN
    SELECT table_name, column_name
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND udt_name = 'uuid'
      AND table_name <> 'profiles'
      AND (
        column_name IN (
          'created_by', 'updated_by', 'uploaded_by', 'parsed_by', 'hidden_by',
          'extracted_by', 'changed_by', 'reviewer_id', 'commented_by',
          'reviewed_by', 'deleted_by', 'resolved_by', 'responsibility_user_id',
          'user_id', 'section_extracted_by'
        )
        OR column_name LIKE '%\_by' ESCAPE '\'
      )
      AND column_name NOT IN (
        'id', 'analysis_run_id', 'analysis_point_id', 'library_id',
        'department_id', 'regulation_document_id', 'regulation_point_id',
        'stored_document_id', 'template_id', 'compliance_session_id',
        'dual_verify_session_id', 'internal_document_id', 'legacy_id'
      )
    ORDER BY table_name, column_name
  LOOP
    q := format(
      $q$
      SELECT COUNT(*) AS orphan_rows
      FROM public.%2$I t
      WHERE t.%1$I IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = t.%1$I)
      $q$,
      r.column_name, r.table_name
    );
    EXECUTE q INTO rec;
    IF rec.orphan_rows > 0 THEN
      RAISE NOTICE 'STILL ORPHAN: %.% → % row(s)', r.table_name, r.column_name, rec.orphan_rows;
    END IF;
  END LOOP;
END $$;
