-- Phase 2: remove tables left by a failed API bootstrap on an empty Supabase project.
-- Safe on a brand-new project (no real data yet). Run BEFORE phase2-full-schema.sql.

DROP TABLE IF EXISTS nd_system_settings CASCADE;
DROP TABLE IF EXISTS stored_documents CASCADE;
DROP TABLE IF EXISTS landing_ai_parse_cache CASCADE;
DROP TABLE IF EXISTS landing_ai_extract_cache CASCADE;
DROP TABLE IF EXISTS nd_internal_document_sections CASCADE;
