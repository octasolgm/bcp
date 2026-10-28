-- Run BEFORE importing landing_ai_* CSVs via PowerShell (large JSONB — UI hangs).

-- Clear stuck/partial compliance session from hung Table Editor import:
DELETE FROM landing_ai_compliance_sessions WHERE id = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';

-- landing_ai_extract_cache: import-csv-table.ps1 uses a temp staging table and maps
-- id,file_hash,schema_key,points_json,extract_model,credit_usage,created_at → real columns.
-- No ALTER needed on the destination table.
