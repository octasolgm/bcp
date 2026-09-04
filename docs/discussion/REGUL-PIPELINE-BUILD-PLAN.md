# Build plan — local OCR + hybrid pipeline, task by task

**Goal:** replace Landing AI (cloud document-AI, cost + confidential docs leaving the building) with a local, non-AI OCR/parse layer, then build the hybrid retrieval pipeline on top of it, in small shippable tasks.

**Companion docs:** [Problem + guide](REGUL-PIPELINE-COST-PROBLEM-AND-GUIDE.md) · [V1 step guide](regul-hybrid-pipeline-detail.md) · [V2 precompute](regul-hybrid-pipeline-v2.md) · [Cost reduction levers](regul-hybrid-pipeline-cost-reduction-plan.md)

---

## 0. Two separate problems Landing AI currently solves

Landing AI isn't one thing today, it's two, and they need two different replacements:

| What it does | Output | What replaces it |
|---|---|---|
| **Parse** — get raw text out of a PDF/Word file | Markdown per page | **OCR / text extraction** — Phase 1 below |
| **Extract** — find where each numbered clause/section starts and ends | `clause_no`, `clause_text`, `source_page` | **Section/clause detection** — Phase 2 below |

Both are non-AI-solvable for the documents in this system. Regulation and internal policy PDFs are almost always **born-digital** (typed, not scanned) with **consistent numbering conventions** (`6.2`, `9.4.1`, `Article 12`) — that's exactly the case deterministic tools handle well. Save the AI-quality question for the LLM judgment step, which is a genuinely different kind of task (semantic reasoning), not text extraction.

---

## 1. OCR / text-extraction library choice

| Option | Type | Handles | Verdict |
|---|---|---|---|
| **PdfPig** | .NET, MIT license, local | Born-digital PDFs — embedded text layer, exact per-page geometry | **Already in this codebase** (`PdfNativePageDocument.cs`, used for page-reference grounding today). No new dependency. Use as the primary extractor. |
| **Tesseract** | Open-source OCR engine, local (via `Tesseract` NuGet wrapper) | Scanned/image-only pages (no embedded text layer) | Add as fallback for the pages PdfPig can't read. Already the OCR mentioned as the intended fallback in `docs/PAGE-REFERENCES.md`. Zero cost, nothing leaves the server. |
| **PaddleOCR** (upgrade path only) | Open-source, Apache 2.0, local (Python sidecar) | Scanned docs with tables/complex layout | Only worth adding if Tesseract's accuracy on real scanned files proves too low. Adds a Python sidecar service — more infra, hold off until there's evidence Tesseract isn't enough. |
| **DocumentFormat.OpenXml** | .NET, MIT license, local | `.docx` text extraction directly (no PDF conversion needed) | Replaces the current "convert Word to PDF first" step for Word uploads — one less conversion, one less place text can get mangled. |
| ~~Cloud OCR APIs (Azure Document Intelligence, Google Document AI, AWS Textract)~~ | Cloud, paid, non-confidential | — | **Ruled out** — same confidentiality problem as Landing AI, just a different vendor. Not evaluated further. |

**Recommendation: PdfPig first, Tesseract fallback for scanned pages, OpenXml for native `.docx`.** Everything runs inside the existing .NET process, nothing is sent anywhere, and two of the three pieces already exist in the codebase.

---

## 2. Phased task list

### Phase 1 — Local text extraction (replaces Landing AI "parse")

**Task 1.1 — Detect pages with no embedded text layer**
Extend `PdfNativePageDocument` (or a sibling class) to flag pages where PdfPig extracts zero/near-zero characters — these are the scanned-image pages that need OCR instead of direct extraction.
*Done when:* given a mixed PDF (some typed pages, some scanned), the loader correctly labels each page as `native-text` or `needs-ocr`.

**Task 1.2 — Add Tesseract OCR fallback**
New service (e.g. `TesseractOcrPageExtractor`) that rasterizes a flagged page to an image and runs local Tesseract against it, producing plain text for that page only.
*Done when:* a scanned test PDF produces usable text for its `needs-ocr` pages, merged into the same per-page text structure PdfPig produces for the rest.

**Task 1.3 — Native `.docx` extraction**
Add a `DocumentFormat.OpenXml`-based extractor for Word uploads, replacing the current PDF-conversion step.
*Done when:* a `.docx` internal policy upload produces the same per-page/per-section text shape as a PDF upload, without going through a PDF conversion first.

**Task 1.4 — Unified local parse output**
Combine 1.1-1.3 behind one interface that returns the same shape Landing AI's "parse" step returns today (markdown-ish text + reliable page numbers), so nothing downstream needs to change yet.
*Done when:* this new local parser can be swapped in wherever `PdfNativePageDocument`/Landing parse output is consumed today, with no changes needed further down the pipeline.

### Phase 2 — Local section/clause detection (replaces Landing AI "extract")

**Task 2.1 — Numbering-pattern detector**
Regex/heuristic pass over the extracted text: detect lines that look like section headers (`\d+(\.\d+)*\s`, `Article \d+`, `Rule \d+\.\d+`, `Section \d+`, etc.), using indentation/line-start position as a secondary signal. Reuse existing helpers where possible — `PointNumberSort` and the phrase/keyword scoring in `NdRegulPolicyContextService.cs` already handle clause-number-shaped strings.
*Done when:* run against a handful of real regulation/internal PDFs, header detection finds the same clause numbers Landing AI's extract currently finds, on a sample you spot-check by eye.

**Task 2.2 — Split into clause_no / clause_text / source_page**
Between two detected headers, everything is that clause's text; carry the page number(s) it spans from Phase 1's per-page text.
*Done when:* output matches the exact shape `LandingAiPolicyClauseExtractService` produces today — `clause_no`, `clause_text`, `source_page` — so `NdRegulPolicyContextService.FromInternalSections()` and friends need zero changes.

**Task 2.3 — Edge cases**
Handle: multi-level numbering resets (`9.4` then `9.4.1` then back to `9.5`), un-numbered intro/preamble text, annexes/appendices with their own numbering scheme, and pages where OCR text is noisy (drop obvious garbage rather than emitting a bogus clause).
*Done when:* a known "hard" document (one that previously needed a manual fix per `REGUL-FORWARD-MATCHING-FIX-PLAN.md` or similar) extracts cleanly.

**Task 2.4 — Validate against a golden set**
Before this replaces Landing AI for real documents: pick 3-5 real regulation/internal docs already processed by Landing AI, run the new local pipeline on the same files, and diff the clause lists (count, `clause_no` values, rough text overlap). This is the guardrail — don't cut over on faith.
*Done when:* local extraction matches Landing AI's clause boundaries closely enough (define a tolerance, e.g. >95% of clause numbers match) on the golden set, with mismatches reviewed by a person, not just eyeballed as "looks fine."

**Task 2.5 — Cut over behind a flag**
Add local parse+extract as a second `WorkflowEngine`-style option (same pattern already used for `regul_pipeline` vs `regul_pipeline_full`), so it can run side-by-side with Landing AI per-document before becoming the default. Keep Landing AI as a manual fallback for documents the local extractor struggles with (unusual formatting, heavily scanned, non-standard numbering).
*Done when:* a document can be processed with either extractor by config/flag, and the rest of the system (library, retrieval, analysis) doesn't know or care which one produced its sections.

**This is the point where the Landing AI cost and the "our confidential docs go to a third-party AI service" problem are both actually solved** — everything above is deterministic, local, and free. Phases 3+ below are the retrieval/cost pipeline from the other docs, built on top of this foundation.

### Phase 3 — Hybrid retrieval (from V1/V2 docs)

**Task 3.1 — BM25 index over local-extracted sections**
Build the keyword index from Phase 2's output. Free, local, no new infra (a .NET BM25 implementation or a lightweight library).
*Done when:* BM25 search against a real internal doc's sections returns sane top-N results for a sample clause query.

**Task 3.2 — Query expansion + sub-obligation split**
Regulation-side, free, no internal doc read — per [V1](regul-hybrid-pipeline-detail.md).
*Done when:* matches the behavior already documented (synonym/acronym table, compound-clause splitting).

**Task 3.3 — Embedding retrieval — decide local vs cloud first**
This is the one piece of the hybrid pipeline that is inherently AI-based. Before building it, make the same confidentiality call explicit: a cloud embeddings API (OpenAI/Gemini/etc.) means internal document *text* leaves the building for this step, even though OCR/parsing no longer does. Two paths:
  - **Local embedding model** (e.g. a small ONNX-exported sentence-embedding model run in-process or as a sidecar) — keeps the whole retrieval side fully on-prem, consistent with the OCR decision above. More setup, but closes the confidentiality gap completely for retrieval.
  - **Cloud embeddings API** — much less setup, but reintroduces "documents leave the building" for this one step (small excerpts per call, not whole documents, but still a decision worth making deliberately rather than by default).
*Done when:* a decision is made and recorded here or in the cost-reduction doc — this blocks Task 3.3's implementation, not the tasks before it.

**Task 3.4 — Hybrid fusion + adaptive select**
Per [V1](regul-hybrid-pipeline-detail.md): union BM25 + embedding top-100 lists, score `0.4*BM25 + 0.6*embedding`, pick 15-56 by threshold/budget.
*Done when:* matches V1's documented behavior on a sample run.

**Task 3.5 — Golden-set validation**
Same discipline as Task 2.4: before hybrid retrieval feeds real LLM judgments, validate its section selection against known-good judgments on a fixed clause set.

### Phase 4 — Precompute (V2) and LLM cost levers

Once Phase 3 is stable, layer in the two things from the other docs that don't change retrieval behavior, only its cost/speed:

**Task 4.1 — Ready Pipeline precompute** — per [V2](regul-hybrid-pipeline-v2.md): move expansion/sub-obligation/clause-embedding and BM25-index/section-embedding building out of the per-run loop into a one-time "Ready Pipeline" step, stored in DB.

**Task 4.2 — Extend prompt caching to the hybrid path** — currently only `analyse-regul-full` gets `cache_control` on its context block (see [problem doc](REGUL-PIPELINE-COST-PROBLEM-AND-GUIDE.md)); reuse the same `cacheContextBlock` plumbing for hybrid once retrieved section sets are stable per clause.

**Task 4.3 — Re-evaluate judgment model** — Sonnet vs Opus (or whichever models are in the provider catalog) against the golden set from Task 3.5, since this was the single biggest cost lever found in the real-scale analysis (~60% of the bill).

**Task 4.4 — Concise output schema / batch API** — the smaller levers from the [cost reduction plan](regul-hybrid-pipeline-cost-reduction-plan.md): tighter judgment output format, batch processing if async turnaround is acceptable.

---

## 3. Suggested order to actually work through this

1. **Phase 1** (local text extraction) — self-contained, no dependency on anything else, immediately removes Landing AI's parse cost.
2. **Phase 2** (local section detection) — depends on Phase 1's output; this is the harder/riskier piece (Task 2.4's golden-set validation matters most here), removes the rest of Landing AI's cost and the confidentiality exposure.
3. **Task 4.3** (model choice) can actually be tested *in parallel* with Phases 1-2 — it doesn't depend on any of the OCR work, just needs the golden set, and it's the single biggest cost number found so far. Worth doing early even though it's listed in Phase 4.
4. **Phase 3** (hybrid retrieval) — once local extraction is trustworthy, this is the piece that caps cost as the internal library grows.
5. **Phase 4 remainder** (precompute, caching, output trimming, batch) — cost/speed polish once the pipeline is functionally correct.

---

## 4. Decisions needed before starting

| Decision | Blocks | Recommendation |
|---|---|---|
| Tesseract good enough, or need PaddleOCR? | Task 1.2 | Start with Tesseract — it's simpler and already the documented fallback plan; only add PaddleOCR if real scanned docs show it's not accurate enough |
| Local vs cloud embeddings | Task 3.3 | If confidentiality is the driver for going local on OCR, be consistent and go local here too — otherwise the "docs don't leave the building" story has a gap |
| Golden set — does one exist, or does it need building? | Tasks 2.4, 3.5, 4.3 | Needed before *any* of these three tasks can be trusted — build it once, reuse everywhere. A few real, already-reviewed documents with known-correct clause lists and judgments |
| Tolerance for local-extraction mismatches vs Landing AI | Task 2.4 | Pick a number (e.g. 95% clause-number match) before running the comparison, not after, so the result isn't rationalized after the fact |

---

*Built on top of [REGUL-PIPELINE-COST-PROBLEM-AND-GUIDE.md](REGUL-PIPELINE-COST-PROBLEM-AND-GUIDE.md) — that doc has the "why," this one has the "how, task by task."*
