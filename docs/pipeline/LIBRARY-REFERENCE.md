# Local Document Pipeline - Library & Design Reference

Detail behind each task in [`TASKS.md`](TASKS.md). This is the "why", not
the "what" - which library was picked, why that one over the alternatives,
and how each algorithm actually works.

## Libraries used for Parse

All of the following are free and open-source - no license fees, no
per-document or per-page cost, no API key. Nothing here calls out to any
external service; everything runs inside the API process.

| Library | NuGet package | License | What it does here |
|---|---|---|---|
| PdfPig | `UglyToad.PdfPig` | Apache 2.0 | Reads text directly out of "born-digital" PDFs - ones that already have a real text layer, not scanned images. |
| PDFtoImage | `PDFtoImage` | MIT | Renders a PDF page to a bitmap image, for pages that need OCR. Built on PDFium (the same PDF rendering engine Chrome uses). |
| Tesseract | `Tesseract` | Apache 2.0 | The OCR engine that reads text out of a rendered scanned page. Originally built by HP, now maintained by Google. |
| RapidOCR | `RapidOcrNet` | Apache 2.0 | A second, alternative OCR engine - PaddleOCR's deep-learning models exported to ONNX. See below for why it was added alongside Tesseract, not instead of it. |
| SkiaSharp | `SkiaSharp` | MIT | Encodes the rendered page bitmap to PNG before handing it to Tesseract/RapidOCR. |
| DocumentFormat.OpenXml | `DocumentFormat.OpenXml` | MIT | Reads `.docx` files directly (Word's own XML format) - no conversion step needed. |

Code: `bcp-api/Services/LocalDocs/LocalPdfExtractionService.cs`,
`TesseractOcrEngine.cs`, `LocalDocxExtractionService.cs`.

### Why PdfPig first, and only fall back to OCR when needed

Most regulation/policy PDFs already have a real, embedded text layer -
reading that directly is instant, free, and perfectly accurate. Running
every page through OCR regardless would be slower and strictly less
accurate than just reading the text that's already there. So Parse checks,
per page: if PdfPig found at least 20 characters of native text, use it
as-is; only render + OCR the page if it found less than that (the
signature of an actual scanned/image page).

### Why render the full page instead of extracting embedded images

The first version of this pipeline tried pulling embedded images out of
the PDF directly via PdfPig (`page.GetImages()`) and OCR-ing those. That
silently found zero images on real scanned documents, because many scanned
PDFs encode their page images as JBIG2 or CCITT fax data, which PdfPig
doesn't reliably enumerate. Rendering the whole page to a bitmap via
PDFtoImage/PDFium instead works regardless of how the page's image data is
internally encoded - it's the reliable path, not a shortcut.

### Why 200 DPI for rendering

Tesseract's own accuracy guidance is 200-300 DPI. PDFium's unset default
renders considerably higher than that, which - combined with the "best"
Tesseract model - multiplies OCR runtime across every scanned page for no
real accuracy gain. Capping render resolution at 200 DPI was one of the
larger performance wins in this pipeline, alongside the parallel OCR pool
below.

### Why the "best" Tesseract trained-data variant

Tesseract ships three trained-data variants for each language: `fast`
(~4MB, quickest, lowest accuracy), `best` (~15MB, LSTM-only, highest
accuracy, slower), and a standard middle option. This was an explicit
choice made with the client during setup - accuracy over raw speed for
regulatory documents, since a misread clause is worse than a slower parse.
The engine mode is also set to `EngineMode.LstmOnly` explicitly (not
`Default`), since `Default` would additionally try Tesseract's older
legacy engine if legacy data happens to be present - the "best"/"fast"
data files are LSTM-only anyway, so this just avoids a wasted code path.

## OCR performance - parallel engine pool

Code: `bcp-api/Services/LocalDocs/TesseractOcrEngine.cs`,
`LocalPdfExtractionService.cs`.

**Original design:** a single `TesseractEngine` instance, reused across
calls (loading its trained-data model from disk is the expensive part, so
it's built once and kept alive), but pages were OCR'd one at a time behind
a lock, because one `TesseractEngine` instance cannot process two pages
concurrently.

**Problem found through live testing:** a 23-page scanned PDF took over 5
minutes and hit the client's request timeout before finishing, even after
the "load the model once" fix. OCR inference itself - not model loading,
not page rendering - was the actual bottleneck, and it was running
strictly serially.

**Fix:** instead of one engine, `TesseractOcrEngine` now holds a small pool
of engines (bounded by `min(CPU core count, 8)`, overridable via
`LocalOcr:MaxConcurrency` in config) and OCRs multiple pages at once via
`Parallel.ForEachAsync`, gated by a `SemaphoreSlim` sized to the pool. PDF
rendering itself stays sequential (PDFium/PdfPig are not safe to call
concurrently against the same document) - only the actual Tesseract
inference step, which is the real cost, runs in parallel. Result: the same
23-page document went from timing out past 5 minutes to finishing in
roughly 70-90 seconds on a 12-core dev machine.

## Why a second engine, and how the pieces fit together

The Linux-native-binary gap in the `Tesseract` NuGet package (documented
above, and in `docs/roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md`) is a real
blocker for the eventual cloud/on-prem deployment, not just a local dev
inconvenience. Rather than betting everything on one fix for that gap,
the OCR engine was made pluggable so a second engine could be run
side by side with Tesseract on the same real documents, and a decision
made once there's real evidence, not a guess.

**Code shape:** `IOcrEngine` (`IsAvailable`, `MaxConcurrency`,
`OcrImageAsync`) is the interface both `TesseractOcrEngine` and
`RapidOcrEngine` implement. `LocalPdfExtractionService` and
`LocalDocumentExtractionService` no longer own a fixed engine - they take
one as a parameter per call. `OcrEngineRegistry` resolves a request's
`{engine}` route segment (`tesseract` | `rapidocr`) to the right
implementation. `nd_local_document_extractions` gained an `Engine` column,
unique together with `StoredDocumentId` - so the exact same uploaded
document can have two independent rows, one per engine, each with its own
parse/extract status and results. Every route in
`LocalDocumentsController` takes `{engine}` as part of the path
(`/nd/local-documents/{engine}/{id}/parse`, etc.).

**Frontend shape:** rather than duplicating the ~1800-line page
components, the existing Internal/Regulation Documents (Local) components
read an `engine` value from route data and thread it through every API
call - so `/nd/internal-documents-new` (Tesseract) and
`/nd/internal-documents-rapidocr` (RapidOCR) are the *same* component,
pointed at different backend engines via routing, not two separate
maintained codebases. Each engine also has its own left-nav group
("Tesseract (Local)" / "RapidOCR (Local)") so both are reachable and
comparable at all times, not hidden behind a config flag.

## RapidOCR - the engine itself

RapidOCR (via the `RapidOcrNet` NuGet package) wraps PaddleOCR's
deep-learning OCR models, exported to ONNX and run through
`Microsoft.ML.OnnxRuntime`. Unlike Tesseract, a single `Detect()` call
runs three separate models in sequence, not one:

1. **Detector** - finds the text regions (boxes) anywhere on the page
   image, from scratch. There is no equivalent step in Tesseract's flow
   here, since Tesseract is only ever handed an already-isolated page
   image and reads it directly - RapidOCR has to first figure out *where*
   the text is.
2. **Classifier** - checks whether each detected region is upside-down
   and rotates it if so.
3. **Recognizer** - reads the characters in each (now upright) region.

**Why this one, specifically, over other alternatives:** `RapidOcrNet`
depends on `Microsoft.ML.OnnxRuntime` and `SkiaSharp.NativeAssets.Linux`,
both of which ship real Linux native binaries in the NuGet package itself
- the same pattern already proven working for `PDFtoImage`/PDFium in this
codebase. That means RapidOCR is expected to work on Azure App Service
Linux with zero extra setup (no `apt-get`, no Docker, no custom
container) - a real, structural advantage over Tesseract's current
Windows-only NuGet package, confirmed from the package's own dependency
list before adding it, not assumed.

## RapidOCR bugs found and fixed

Both of these were found by actually running RapidOCR against a real
document, not caught by reasoning about the code alone - live testing is
what surfaced them.

**Bug 1 - silently empty output on every page.** The parameterless
`InitModels()` resolves its default model-file path (`models/v5/...`)
against the process's *current working directory*. Under `dotnet run`,
that's the project folder, not the folder the actual compiled binary
lives in (`bin/Debug/net8.0/`) - so even though the NuGet package copies
the model files to the right place in the build output, `InitModels()`
looked for them in the wrong place, found nothing, and OCR silently
returned empty text for every page (no crash, no visible error - a
document just came back with no readable text at all). Fixed by building
the model paths explicitly from `AppContext.BaseDirectory` (the real
binary location) and passing them straight to `InitModels()`.

**Bug 2 - a 23-page document looked stuck for 6+ minutes.** RapidOCR's
own README documents a `GetDefaultSessionOptions(int numThread = 0)`
helper specifically for tuning this: each ONNX inference session, left at
its default, tries to use *multiple* CPU threads internally for its own
work. This codebase already runs up to 8 of these engines at once (one
per page, the same pool pattern as Tesseract) - so with each of those 8
engines *also* trying to multithread internally, the real behavior was 8
engines times several threads each, all fighting over the same CPU cores.
That reads as "stuck," not "slow" - forward progress essentially stalls
under that much contention, rather than cleanly running in parallel.
Fixed by pinning each pooled engine to exactly 1 internal thread
(`RapidOcr.GetDefaultSessionOptions(numThread: 1)`), so the *only* level
of parallelism is the outer pool (up to 8 pages at once) - matching the
pattern the library's own documentation recommends for exactly this
situation.

A minor, related correctness fix landed alongside these: the
concurrency-limiting semaphore was being lazily created with
`_gate ??= new SemaphoreSlim(...)`, which is not safe if multiple threads
hit it at the same moment (a real, if narrow, race). Changed to build it
once upfront instead of lazily.

## Speed depends on core count, not on having a GPU

Worth being explicit about, since it came up directly: none of this uses
a graphics card. `numThread: 1` per engine plus an 8-engine pool means
speed scales with **how many CPU cores are actually available**, up to
the pool's cap (`min(available cores, 8)`, same reasoning as
`TesseractOcrEngine`). On a machine with fewer cores - notably an Azure
App Service B1 plan, which has exactly 1 vCPU - the pool automatically
shrinks to running one page at a time, no code change needed, but also no
parallelism benefit: a multi-page document would take roughly as many
times longer as the ratio of cores lost (an 8-core-to-1-core drop is
roughly an 8x slowdown for a multi-page document, not because any single
page gets slower, but because far fewer pages can run at the same time).
GPU acceleration is possible in principle (the library supports a CUDA
execution provider) but was deliberately not used here - it would need an
NVIDIA GPU specifically, and standard Azure App Service tiers (including
B1) don't provide GPU access at all, so it wouldn't help the actual
deployment target regardless of what's available on a local dev machine.

## Accuracy comparison - what we actually know so far

A real side-by-side test was run: the same page (a dense table of
contents with dot-leader lines) through both Tesseract and RapidOCR.
**Both got it wrong, in different ways** - this is a live finding, not a
theoretical caveat:

- Tesseract invented long garbage strings on the dot-leader sections (the
  pattern described above), and also mangled some real words - e.g.
  "Applicability" read as "APPHCADIIIEY", "Legal Basis" as "L@GAI BASIS".
- RapidOCR didn't invent garbage runs, but it dropped or altered
  characters inside real words - "Purpose" read as "Pupo",
  "Applicability" as "Applicabilit" (missing the final letter),
  "Definitions" as "Deinition" - and lost the page-number column
  entirely.

**Neither engine should be treated as "the accurate one" based on this.**
A table of contents with dense small print and dot leaders is closer to a
worst case for any OCR engine than a typical body/content page - and body
pages (the actual regulation clause paragraphs that get extracted into
points) are what accuracy actually needs to be judged on, not decorative
front matter. That real comparison - a normal content page, both engines,
side by side - has not been run yet. Two other untried levers before
drawing a conclusion: rendering at a higher DPI than the current 200 (this
specific page may need more resolution to read cleanly, for either
engine), and RapidOCR's alternate `PythonCompat` preset (the current code
uses `RapidOcrOptions.Default`, tuned differently).

## Where each engine actually runs

Worth being explicit about, since it's the biggest structural difference
between the four options:

| Engine | Runs where | GPU or CPU |
|---|---|---|
| Tesseract | Inside `bcp-api` (.NET) directly - no separate process | CPU only - Tesseract has no GPU support at all, by design |
| RapidOCR | Inside `bcp-api` (.NET) directly - no separate process | CPU only (deliberately configured that way - see the thread-tuning section above); the library could support GPU in principle, not set up here |
| Docling Light | **Separate Python service** (`docling-service/server.py`, port 5055) - `bcp-api` calls it over HTTP via `DoclingClient`, does not run it in-process | CPU only. Internally uses RapidOCR too (the Python package, not our .NET one) |
| Docling GLM-OCR | Same separate Python service, different mode (`?mode=glm`) | CPU only - the direct cause of the ~21 min/page cost below |

Tesseract and RapidOCR are single-process: one thing running, `bcp-api`
itself. Docling (either mode) is two things running at once - our .NET
backend, plus a completely separate Python program it talks to over a
local network call. That's also why Docling needed its own setup (a
Python virtual environment, `pip install`, a service to start) that the
other two didn't - see `docling-service/` in the repo root.

## Docling Light - real test result

Tested on the real, full 23-page `TFS Guidelines.pdf` (the same document
used throughout this evaluation) via the actual web page
(`/nd/internal-documents-docling-light`, calling
`POST /nd/local-documents/docling-light/{id}/parse`).

**Result: 9.2 minutes for the full 23-page document**, confirmed by the
user directly from the running app - noticeably slower than the
equivalent RapidOCR page (which handles the same document in well under 2
minutes). Root cause, visible directly in `docling-service/server_log.txt`
for this exact run: Docling Light does real table/layout structure
analysis on top of OCR (`MatchingPostProcessor` entries doing cell-to-row
matching in the log), uses a different model version and a different,
generally slower-on-CPU runtime than our own RapidOCR page (`PP-OCRv6`
via PyTorch, vs `PP-OCRv5` via ONNX Runtime), and does not appear to
parallelize across pages the way our own RapidOCR pool was tuned to. Not
a bug - a real, structural difference in how much work each pipeline
actually does per page.

## GLM-OCR via Docling - real test result

A real test was run: the same table-of-contents page used in the
Tesseract/RapidOCR comparison above, converted via Docling's VLM pipeline
with GLM-OCR selected as the model (`vlm_model_specs.GLMOCR_TRANSFORMERS`
- confirmed to exist as a built-in Docling option, no separate GLM-OCR
integration needed). Full transcript saved at
`docling-service/glmocr_test_output.txt`.

**Accuracy: every line came back correct** - every heading, every clause
number, every dot-leader line, including the exact words that tripped up
both Tesseract ("APPHCADIIIEY" for "Applicability") and RapidOCR ("Pupo"
for "Purpose") on this identical page. This is a real, measured result on
a real page from the real document, not a claim from GLM-OCR's own
marketing.

**Speed: 1249.2 seconds - just under 21 minutes - for that one page**,
CPU-only (this machine has no GPU acceleration configured for any of the
four engines - see the table above). For a 23-page document at this rate,
that's roughly 8 hours. This is exactly the tradeoff Zafar flagged on the
team when GLM-OCR came up: real accuracy gain, but it needs GPU-backed
hosted inference to be practical at volume - now backed by a measured
number instead of a general caution.

One minor artifact also worth noting: the raw output had a stray leaked
token (`&lt;|user`) at the very end - a small cleanup issue, not a
content-accuracy problem.

**Practical implication:** Docling GLM-OCR mode is wired up and testable
today, but only sensibly on single-page or very short documents given the
~21 min/page cost - the frontend's timeout for this mode was set to 6
hours specifically to allow that kind of short test, not as a real
per-request SLA. See the on-prem roadmap for what GPU-backed hosting for
this would actually require.

## The schema migration bug

Adding the `Engine` column (so one document can have independent rows per
engine) exposed a real, previously-invisible bug in
`NdIncrementalSchemaBootstrap.cs`: an old migration step recreated a
unique index on `stored_document_id` alone, every single server restart
(`CREATE UNIQUE INDEX IF NOT EXISTS ...`). That was harmless before -
there was only ever one row per document - but became a real conflict the
moment multiple engines started writing independent rows for the same
document (now expected, not a bug). The old step ran *before* the newer
step that drops it and replaces it with the correct compound index
(`stored_document_id` + `engine`), so on every restart it tried to
recreate a constraint that duplicate data (by design) now violated,
crashing the whole schema bootstrap with a Postgres `23505` unique-
violation error before the app could even start.

Fix: the old `CREATE UNIQUE INDEX` step itself was changed to a
`DROP INDEX IF EXISTS` - a safe no-op cleanup step instead of a doomed
recreation - since the real, correct index is created by the later step
in the same migration list. `PatchSql` runs top-to-bottom on every
startup, so step ordering like this matters and is worth checking
whenever an older step's assumptions (like "one row per document") change
underneath it.

## The stale-processing auto-recovery bug (found via live Docling testing)

A safety net built earlier (`RecoverIfStaleAsync`) flips any row still
stuck in "processing" back to "failed" after 5 minutes, on the assumption
that no legitimate parse takes that long - the server must have crashed
or restarted mid-run. That assumption was true for Tesseract/RapidOCR
(both reliably finish a full document in under 2 minutes) but is **false
for Docling** - confirmed via live testing at ~9 minutes for Docling
Light on a full document, and Docling GLM-OCR can legitimately run for
hours.

**Real, reproduced symptom:** parsing a document with Docling Light,
watching the network tab show the `/parse` request complete with `200`
after the real ~9 minutes, and the row *still* showing "Not Parsed /
Not Extracted" / "Parsing..." even after a full page reload. Root cause:
any status poll that happened to land between the 5-minute mark and the
job's actual completion saw a row correctly still "processing" and
*incorrectly* flipped it to "failed" - a false positive from a threshold
tuned for the wrong engines, not a display bug and not a backend
crash.

Fix: the threshold is now engine-aware
(`StaleProcessingAfterFor(row.Engine)` in `LocalDocumentsController.cs`)
- 5 minutes still for Tesseract/RapidOCR, 8 hours for either Docling mode
(`OcrEngineNames.IsDocling(engine)`). A single shared constant across
engines with wildly different realistic run times was the actual mistake
here, not the concept of stale-recovery itself.

## Why a client timeout was losing finished OCR work

Every ASP.NET request carries a `CancellationToken` that fires if the
client disconnects (e.g. a request timeout in the browser). The original
code threaded that same token all the way through to the final database
save after Parse finished. If the client's timeout fired *after* OCR had
actually completed but *before* the save ran, `SaveChangesAsync(ct)` would
throw immediately instead of saving - discarding several minutes of
completed OCR work, and leaving the document stuck showing "not parsed."

Fix: the final save (in both Parse and Extract, in
`bcp-api/Controllers/NewDashboard/LocalDocumentsController.cs`) now
explicitly uses `CancellationToken.None`, deliberately, so a client
disconnect can never throw away work that already finished. The client-side
timeout itself was also raised from 5 minutes to 20 minutes
(`bcp-web/src/app/services/nd/nd-api.service.ts`) as a separate, additional
safety margin for genuinely large scanned documents.

## Extraction rule (regex, not AI)

Code: `bcp-api/Services/LocalDocs/LocalSectionSplitter.cs`.

There is no AI/LLM call anywhere in Extract - it is plain pattern-matching
over the plain text that Parse already produced, using two regular
expressions:

1. **Numbered headings** - `6.2. Independent Audit`, `9.4.1 Record
   Keeping` - up to 4 numbering levels deep
   (`\d{1,3}(\.\d{1,3}){0,4}`).
2. **Labelled headings** - `Article 12`, `Section 6.2`, `Rule 9.4.1`,
   `Clause 3`, `Chapter 5`, `Annex 2`.

**The algorithm** walks the parsed markdown page by page, line by line:

- A line matching either pattern **starts a new point** - whatever text
  had been accumulating under the previous point is saved as a finished
  section (with its source page number), and a fresh buffer starts.
- A line that doesn't match is **appended** to whatever point is currently
  accumulating - this is how a clause's body text (everything until the
  next numbered heading) ends up grouped under it.
- Any text before the very first detected heading is bucketed under an
  "Introduction" point rather than dropped, so nothing at the start of a
  document silently disappears just because numbering hadn't started yet.
- A length guard (3-160 characters) stops an accidental heading-shaped
  line inside a paragraph from being mistaken for a real clause.

**Same code for every document type.** `LocalSectionSplitter.Split()` and
its caller `LocalDocumentExtractionService.ExtractFromMarkdown()` have no
concept of "this is a regulation doc" vs "this is an internal doc" - the
single `/nd/local-documents/{id}/extract` endpoint runs identically
regardless of the underlying document's `DocKind`. Only the *content* fed
into it differs between documents, never the splitting rule.

### Extraction rule - known limitation

This is the one part of the local pipeline that is a genuine judgment
call rather than a safe drop-in replacement for Landing AI's extraction.
Regex over numbering conventions works well for documents that number
consistently, which most regulation/policy documents do - but an
unusually formatted document could split worse than before. It is also
sensitive to OCR quality: if a heading line itself comes out garbled from
OCR, it silently fails to match and gets folded into the previous point
instead of becoming its own. Worth spot-checking extracted results against
a few known-good documents before relying on it for a live analysis run.

## OCR artifact cleanup - dot-leader misreads

Code: `bcp-api/Services/LocalDocs/OcrArtifactCleaner.cs`.

**The problem:** a table of contents commonly uses a "dot leader" - a row
of periods connecting an entry to its page number
(`1. INTRODUCTION .......................... 3`). To an OCR model, a long
dense row of tiny dots looks like a dense string of tiny character-like
marks, and Tesseract will confidently "read" it as garbled text, e.g.
`ccocveeresmssesssnssessssssssesssssssnssnssssssnnns`. This is a known,
common OCR artifact on decorative dot-leader lines specifically - it does
not happen in normal paragraph text.

**The fix:** a targeted, bounded heuristic - not a general OCR-quality
filter. A single whitespace-separated token is treated as a dot-leader
artifact, and replaced with `...`, only if **both**:

- it is at least 12 characters long, and
- fewer than 15% of its letters are vowels.

Real English words, even long ones, never fall under a 15% vowel ratio -
so this only strips the specific implausible-word pattern OCR produces
from dot leaders, and never touches genuine text. This runs on OCR output
only (never on PdfPig's native text, which doesn't have this failure
mode, since it reads the document's real text rather than guessing at
pixels).
