-- Per-chunk policy section cache stores under schema_key = policy_clauses_v1 (embedded chunk_cache JSON).
-- Legacy CHECK constraint blocked keys like policy_clauses_v1:chunk:0 and caused failed saves after Landing AI calls.
ALTER TABLE landing_ai_extract_cache
  DROP CONSTRAINT IF EXISTS landing_ai_extract_cache_schema_key_check;
