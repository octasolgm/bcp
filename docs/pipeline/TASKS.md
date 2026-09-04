# Local Document Pipeline - Task List

Short list of what was built. One task per line. See
[`LIBRARY-REFERENCE.md`](LIBRARY-REFERENCE.md) for the detail behind any
item marked with a link - which library, why that one, how the algorithm
works.

## Build

- Built a new, fully local (non-AI) document pipeline for parsing and
  extracting regulation and internal documents - no Landing AI, no
  per-document cost, nothing leaves the server. Code lives in
  `bcp-api/Services/LocalDocs/`.
- Added **Parse**: converts a PDF or `.docx` into plain markdown text with
  page references (`<!-- BCP_PDF_PAGE:N -->` markers). See
  [Libraries used for Parse](LIBRARY-REFERENCE.md#libraries-used-for-parse).
- Added **Extract**: splits already-parsed markdown into
  clauses/points using regex over numbering conventions (`6.2`,
  `Article 12`, etc.) - no AI involved. See
  [Extraction rule](LIBRARY-REFERENCE.md#extraction-rule-regex-not-ai).
- Made Parse and Extract two genuinely independent steps, not one combined
  action - separate status fields (`Status` / `ExtractStatus`), separate
  buttons, separate timestamps (`ParsedAt` / `ExtractedAt`) in the
  `nd_local_document_extractions` table.
- Built full clones of the Internal Documents and Regulation Documents
  pages (same columns, filters, upload, sections panel, analysis history,
  delete as the originals) wired to the local pipeline instead of Landing
  AI - new routes `internal-documents-new` / `regulation-documents-new`.
  The original Landing-AI pages are untouched.
- Built a third, simpler "Text Documents" library with a nested
  Point / Sub-point tree view, sharing the same local parse/extract
  endpoints as the two above.
- Added a "View parsed text" panel - clicking a document row shows the
  full parsed markdown right away (auto-opens when nothing has been
  extracted yet, so there's no empty panel to click past).

## Fixes

- Fixed a stuck/contradictory status display - parse status and extract
  status are now tracked and shown independently instead of collapsing
  into one derived state (previously a parsed-but-not-extracted document
  could show "extracted" with 0 points).
- Added stale-row auto-recovery - a row stuck in "processing" (e.g. from a
  server restart mid-run) automatically flips to "failed" after 5 minutes
  instead of staying stuck forever.
- Fixed a bug where a client-side request timeout during OCR discarded
  already-completed work - the final database save no longer depends on
  the client's connection staying open. See
  [Why a client timeout was losing finished OCR work](LIBRARY-REFERENCE.md#why-a-client-timeout-was-losing-finished-ocr-work).
- Fixed the "used in analyses" count - it was matching documents by
  file-content hash, so two separate document uploads with identical
  content (e.g. a re-upload of the same PDF) shared the same count. Now
  matches by document ID only, so every upload is counted independently.

## Performance

- Parallelized OCR across a small pool of Tesseract engines instead of
  running one page at a time - cut a 23-page scanned document's parse time
  from 5+ minutes (previously timing out) to under 90 seconds. See
  [OCR performance - parallel engine pool](LIBRARY-REFERENCE.md#ocr-performance---parallel-engine-pool).
- Added OCR artifact cleanup - table-of-contents dot-leader lines
  (`.......`) that Tesseract misreads as garbled text are now cleaned up.
  See
  [OCR artifact cleanup - dot-leader misreads](LIBRARY-REFERENCE.md#ocr-artifact-cleanup---dot-leader-misreads).

## Multi-engine comparison (RapidOCR added alongside Tesseract)

- Made the OCR engine pluggable (`IOcrEngine`) instead of hard-coded to
  Tesseract - Parse/Extract now take an `{engine}` route segment
  (`tesseract` | `rapidocr`), and `nd_local_document_extractions` has an
  `Engine` column so the same document can be parsed independently by more
  than one engine and compared. See
  [Why a second engine, and how the pieces fit together](LIBRARY-REFERENCE.md#why-a-second-engine-and-how-the-pieces-fit-together).
- Added **RapidOCR** as a second engine (PaddleOCR models via ONNX,
  cross-platform including Linux with no extra setup, unlike Tesseract's
  current NuGet package) - own nav group, own pages
  (`internal-documents-rapidocr` / `regulation-documents-rapidocr`), same
  UI as the Tesseract pages. See
  [RapidOCR - the engine itself](LIBRARY-REFERENCE.md#rapidocr---the-engine-itself).
- Fixed two real RapidOCR bugs found through live testing, not left as
  theoretical risks: a model-file path bug that silently produced empty
  text for every page, and a CPU thread-oversubscription bug that made a
  23-page document look stuck for 6+ minutes. See
  [RapidOCR bugs found and fixed](LIBRARY-REFERENCE.md#rapidocr-bugs-found-and-fixed).
- Compared Tesseract vs RapidOCR on a real page (a dense table-of-contents
  page with dot leaders) - **both got it wrong, differently.** Neither is
  confirmed more accurate yet; the real test (a normal body/content page,
  not a decorative TOC) hasn't been run. See
  [Accuracy comparison - what we actually know so far](LIBRARY-REFERENCE.md#accuracy-comparison---what-we-actually-know-so-far).

## Docling added (two more modes: Light and GLM-OCR)

- Added **Docling** as a third parsing option - fundamentally different
  from Tesseract/RapidOCR: it converts the *whole* PDF itself (its own
  layout analysis, its own page handling) rather than reading one page
  image at a time, and it's Python-only, so it runs as a **separate local
  service** (`docling-service/`), not inside `bcp-api`. See
  [Where each engine actually runs](LIBRARY-REFERENCE.md#where-each-engine-actually-runs).
- Two selectable modes, both reachable from their own nav group and pages
  (`internal/regulation-documents-docling-light`,
  `internal/regulation-documents-docling-glm`):
  - **Docling Light** - Docling's default pipeline (layout model +
    RapidOCR under the hood). Comparable speed to the RapidOCR page.
  - **Docling GLM-OCR** - Docling's VLM pipeline using GLM-OCR (the model
    Zafar flagged on the team as needing hosted inference - turned out to
    be a selectable option *inside* Docling, no separate integration
    needed). See
    [GLM-OCR via Docling - real test result](LIBRARY-REFERENCE.md#glm-ocr-via-docling---real-test-result).
- **Real accuracy result: GLM-OCR read the same table-of-contents page
  perfectly** - every heading, every number, every dot-leader line
  correct, where both Tesseract and RapidOCR made real errors on the
  identical page. **Real speed result: ~1249 seconds (~21 minutes) for
  that one page**, CPU-only - confirms Zafar's original caution that
  GLM-OCR needs GPU-backed hosting to be practical at real volume.
- Fixed a real schema-migration bug found through this work: an old,
  now-superseded database index (unique on document ID alone) was still
  being recreated on every server restart, and started failing once
  multiple engines began writing independent rows for the same document
  (which is expected now, not a bug) - see
  [The schema migration bug](LIBRARY-REFERENCE.md#the-schema-migration-bug).
- Fixed a real, reproduced bug found by actually testing Docling Light on
  a full document: the 5-minute stale-processing auto-recovery (built
  earlier for Tesseract/RapidOCR) was falsely marking a still-working
  ~9-minute Docling parse as "failed" partway through, even though the
  server was never crashed. Now engine-aware - 5 minutes for
  Tesseract/RapidOCR, 8 hours for either Docling mode. See
  [The stale-processing auto-recovery bug](LIBRARY-REFERENCE.md#the-stale-processing-auto-recovery-bug-found-via-live-docling-testing).

## Known limitations (worth knowing, not yet fixed)

- **Docling GLM-OCR is not usable at real document volume yet.**
  ~21 minutes per page, CPU-only, means a full multi-page document could
  take hours. Only test it on single/short documents until this runs on
  a GPU - see the on-prem roadmap for what that would actually require.
- Docling's page-reference tracking is a known simplification right now -
  the whole document is currently treated as one page for citation
  purposes (Docling's own output doesn't carry our `BCP_PDF_PAGE:N`
  marker format), unlike Tesseract/RapidOCR which track real per-page
  references. Fine for accuracy testing, not yet accurate for citations.
- The `Tesseract` NuGet package only ships Windows native binaries - OCR
  will silently return empty text if this runs on a Linux server without
  those binaries installed separately. RapidOCR does not have this
  problem (see above) - a real reason to prefer it if Linux hosting is
  the target, once accuracy is actually confirmed on real content.
  See `docs/roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md`.
- Regex-based extraction assumes a document numbers its clauses/sections
  consistently. An unusually formatted document may split worse than
  Landing AI's old approach did - see
  [Extraction rule - known limitation](LIBRARY-REFERENCE.md#extraction-rule-regex-not-ai).
- Neither Tesseract nor RapidOCR has been confirmed accurate on real
  content pages yet - only a decorative table-of-contents page has been
  compared, and both did poorly on it (differently). Do not treat either
  engine as "the accurate one" until a real body-page comparison has been
  run. See
  [Accuracy comparison - what we actually know so far](LIBRARY-REFERENCE.md#accuracy-comparison---what-we-actually-know-so-far).
