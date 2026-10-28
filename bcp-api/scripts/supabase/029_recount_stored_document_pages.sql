-- stored_documents.pages was often wrong after CSV import (size-based estimate: 37, 63, etc.).
-- Accurate counts require reading the PDF from Storage (PdfPig) — run from the app, not SQL.
--
-- After deploying the API fix, for each regulation row in the UI:
--   Open document → "Refresh PDF pages"
-- Or POST /nd/regulation-documents/{id}/refresh-page-references (updates stored_documents.pages + point refs).
--
-- Optional: inspect current values for duplicate CBUAE uploads:
SELECT id, title, pages, size_bytes, file_hash, parse_status, point_count, updated_at
FROM stored_documents
WHERE title ILIKE '%CBUAE_EN_3945%'
ORDER BY created_at;
