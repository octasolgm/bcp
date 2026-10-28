-- AUDIT: find every orphan profile UUID across all public tables.
-- Orphan = value IS NOT NULL and NOT in public.profiles.
-- Run in Supabase SQL Editor BEFORE 028_fix_orphan_profile_ids_dynamic.sql

-- ── 1) Summary per table.column (NOTICE output in Messages tab) ─────────────
DO $$
DECLARE
  r RECORD;
  q TEXT;
  rec RECORD;
BEGIN
  RAISE NOTICE '=== Orphan profile UUID audit ===';
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
      SELECT COUNT(DISTINCT t.%1$I) AS distinct_orphans, COUNT(*) AS orphan_rows
      FROM public.%2$I t
      WHERE t.%1$I IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = t.%1$I)
      $q$,
      r.column_name, r.table_name
    );
    EXECUTE q INTO rec;
    IF rec.orphan_rows > 0 THEN
      RAISE NOTICE '%.% → % row(s), % distinct UUID(s)',
        r.table_name, r.column_name, rec.orphan_rows, rec.distinct_orphans;
    END IF;
  END LOOP;
END $$;

-- ── 2) Detail: table, column, orphan UUID, row count (result grid) ─────────
DO $$
DECLARE
  r RECORD;
  q TEXT;
BEGIN
  CREATE TEMP TABLE IF NOT EXISTS _orphan_audit (
    table_name text,
    column_name text,
    orphan_id text,
    row_count bigint
  ) ON COMMIT DROP;
  TRUNCATE _orphan_audit;

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
      INSERT INTO _orphan_audit (table_name, column_name, orphan_id, row_count)
      SELECT %L, %L, t.%I::text, COUNT(*)::bigint
      FROM public.%I t
      WHERE t.%I IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM public.profiles p WHERE p.id = t.%I)
      GROUP BY t.%I
      $q$,
      r.table_name, r.column_name,
      r.column_name, r.table_name,
      r.column_name, r.column_name, r.column_name
    );
    EXECUTE q;
  END LOOP;
END $$;

SELECT table_name, column_name, orphan_id, row_count
FROM _orphan_audit
ORDER BY table_name, column_name, row_count DESC;

-- ── 3) Generate UPDATE statements (for review; prefer 028 to run them) ─────
SELECT format(
  'UPDATE public.%I t SET %I = m.new_id FROM profile_id_map m WHERE t.%I = m.old_id;',
  table_name, column_name, column_name
) AS generated_update_sql
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
ORDER BY table_name, column_name;
