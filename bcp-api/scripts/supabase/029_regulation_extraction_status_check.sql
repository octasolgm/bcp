-- regulation_documents.extraction_status CHECK from 005 only allowed
-- pending|processing|completed|failed — but the API uses parsed + paused after Landing AI parse.
-- Without this, demo/production parse fails with:
--   "An error occurred while saving the entity changes."

DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT conname FROM pg_constraint
    WHERE conrelid = 'regulation_documents'::regclass
      AND contype = 'c'
      AND pg_get_constraintdef(oid) ILIKE '%extraction_status%'
  LOOP
    EXECUTE format('ALTER TABLE regulation_documents DROP CONSTRAINT %I', r.conname);
  END LOOP;
END $$;

ALTER TABLE regulation_documents ADD CONSTRAINT regulation_documents_extraction_status_check
  CHECK (extraction_status IN (
    'pending', 'processing', 'parsed', 'paused', 'completed', 'failed'
  ));
