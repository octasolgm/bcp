Weekly Summary - 31 Aug 2026 to 6 Sep 2026

31 Aug 2026

Tasks
- Recall now follows role rather than who originally submitted the report, so any maker or checker can pull a report back from the next stage
- Gap and action updates now refresh clause verdicts without a full page reload
- Gap and action badges now keep counting resolved items even after a clause is auto-marked compliant
- The MIS panel now lists items by clause number instead of grouping resolved items first
- Built and deployed the API and the web app to the Azure dev environment (version 2026.08.31.070844)

Bug fixes
- None today

2 Sep 2026

Tasks
- Built a local, offline document parsing pipeline for Internal and Regulation documents - no external AI service, no per-document cost, nothing leaves the server
- Split "Parse" (convert document to text with page references) and "Extract" (split text into clauses/points) into two independent, separately tracked steps instead of one combined action
- Added a "View parsed text" panel so a document's full parsed text is visible immediately after parsing, before extraction has run
- Built full clones of the Internal Documents and Regulation Documents pages on new routes, wired to the local pipeline, without touching the existing pages
- Built a third, simpler "Text Documents" library with a nested point/sub-point view
- Parallelized the local OCR step across multiple pages at once - cut a 23-page scanned document's parse time from 5+ minutes to under 90 seconds
- Wrote the on-prem/private deployment roadmap and the hybrid analysis pipeline implementation plan (planning only, not built)

Bug fixes
- Fixed a stuck status display where a parsed-but-not-yet-extracted document could incorrectly show as fully extracted with zero points
- Fixed a stale row staying stuck in "processing" forever after a server restart mid-run - now auto-recovers after a timeout
- Fixed a client request timeout discarding already-completed OCR work instead of saving it
- Fixed the "used in analyses" count matching documents by file content instead of by document ID, causing unrelated re-uploads to inherit another document's count
- Cleaned up a recurring OCR misread on table-of-contents pages (dense dot-leader lines read as garbled text)

3 Sep 2026

Tasks
- Made the OCR engine swappable instead of hard-coded, so the same document can be parsed by more than one engine and compared
- Added RapidOCR as a second local parsing engine alongside the existing one, with its own pages and navigation group for side-by-side comparison
- Reorganized the left navigation into per-engine groups for the new local document pages

Bug fixes
- Fixed a bug in the new RapidOCR engine where parsing silently returned no text for every page (a file-path resolution issue)
- Fixed a bug where the new RapidOCR engine could appear stuck for several minutes under normal use (a CPU thread-contention issue, not a hang)
- Fixed a race condition where clicking Parse immediately after uploading a new regulation document could fail with "Document not found"

4 Sep 2026

Tasks
- Added Docling as a third and fourth local parsing option (a "light" mode and a more accurate mode using a vision-language model), each with its own pages and navigation group
- Real side-by-side testing across all engines on the same real documents to compare accuracy and speed

Bug fixes
- Fixed a database migration step that could crash the API on startup once a document could have more than one parse result (one per engine)
- Fixed a status-recovery safety check that was incorrectly marking a still-in-progress parse as failed for slower parsing engines

Investigated, follow up pending
- Comparing Tesseract, RapidOCR, and Docling (in both its light and vision-language-model modes) for real-world OCR accuracy and speed, to decide which to standardize on. No final decision yet - the vision-language-model mode is noticeably more accurate in testing but currently too slow for full documents without dedicated GPU hardware, which is a separate infrastructure decision still to be made.
