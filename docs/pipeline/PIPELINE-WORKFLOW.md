# Document analysis pipeline - full workflow

This is the living reference for the whole pipeline, from uploading a document through to
an AI compliance judgment. It covers every step end to end - the ones already built and
the ones still planned - so there is one place that always answers "what actually happens,
in what order, and what does each step cost."

**Keep this file up to date.** Whenever a planned step below actually gets built, flip its
status from `Planned` to `Built` and correct anything about it that changed during real
implementation. Don't let this drift out of sync with the code.

The order is fixed and never skips forward - a step can only start once the one before it
has finished. See the diagram shown in chat alongside this document for the same steps as
boxes.

## Phase 1 - document preparation (per document, once)

These four steps happen once per uploaded document (per Parse engine choice). Everything
here is already built and running.

### 1. Upload - `Built`

The document (PDF, DOCX) is saved to storage. Nothing is read yet.

**Cost: $0.**

### 2. Parse - `Built`

Converts the document into plain text with page markers, so every later step knows which
page a piece of text came from. Engine is a per-document choice:

- Tesseract / RapidOCR / Docling (Light or GLM-OCR) - run locally on this server.
- Azure Document Intelligence - calls Microsoft's cloud (`prebuilt-layout` model).

**Cost:**
- Tesseract / RapidOCR / Docling: $0, always.
- Azure: ~$10 per 1,000 pages analyzed (Layout model, Standard/S0 tier). Free (F0) tier is
  $0 but capped at 4MB files and the first 2 pages of any document.

### 3. Extract ("chunking") - splits the parsed text into clauses

Takes Parse's plain text and splits it into individual clauses/sections. One step, two
possible methods - only one runs per document, chosen automatically:

- **Structural extraction** - `Built`. Regex looks for numbered headings ("6.2 Independent
  Audit", "Article 12") and cuts there. Works on any document that numbers its clauses
  consistently.
- **Semantic extraction** - `Planned`, a fallback for when structural extraction finds few
  or no recognizable headings. Would embed the text sentence by sentence and cut wherever
  the meaning shifts between consecutive sentences, instead of relying on numbering.

**Neither method ever rewrites, paraphrases, or summarizes.** Both only decide *where* to
cut - the text inside every chunk is the original wording, copied exactly.

**Cost:** $0 either way - structural is pure pattern matching, semantic would use the
already-integrated local embedding model (no cloud call needed).

### 4. Index - `Built` (for internal documents only)

Runs automatically right after Extract finishes, for internal documents only - never for
regulation documents (see "why only internal documents," below). For every chunk Extract
produced, the local embedding model (`bge-micro-v2`, via ONNX Runtime) converts its text
into a 384-number vector and saves it permanently, so it can be searched later without
re-computing anything.

**Cost: $0**, using the local model. Only a `Planned`, optional path if this were ever
switched to a cloud embedding provider instead.

## Why only internal documents get indexed

Regulation documents are the *query* side of the analysis pipeline - one gov clause is
read at a time and used to search. Internal documents are the *haystack* - every one of
their sections needs to already be searchable, ahead of time. You don't pre-build a search
index out of the thing doing the searching, only out of what's being searched.

## Phase 2 - hybrid analysis run (per gov clause, `Planned`)

None of this phase is built yet - it's the plan this repo has already written up in
`HYBRID-ANALYSIS-PIPELINE-PLAN.md`, listed here in the same order so this doc stays the
one complete map of the whole pipeline. Every step below runs **once per gov clause**, in
a loop, for every clause in the selected regulation document.

### 5. Query expansion - `Planned`

Expands the gov clause's wording with synonyms/acronyms (e.g. "TFS" <-> "Targeted
Financial Sanctions") before searching, so wording differences alone don't defeat keyword
search. Never reads the internal side. **Cost: $0** - a dictionary lookup, no AI call.

### 6. Sub-obligation split - `Planned`

Some gov clauses bundle multiple obligations in one clause ("must do X and must also do
Y"). This would split such a clause into its sub-obligations before retrieval, so each can
be matched independently. Rule-based (conjunctions, numbered sub-items), same style as
today's structural extraction. **Cost: $0.**

### 7. BM25 retrieve - `Planned`

Classic keyword/lexical search: the expanded gov query against the internal section index
(the pgvector table from step 4), returning the top ~100 keyword-matched sections.
Recommended via Lucene.NET. **Cost: $0** - runs locally.

### 8. Embedding retrieve - `Planned`

Semantic search: embeds the gov clause's meaning and compares it against the already-
computed embeddings from step 4, returning the top ~100 semantically-similar sections.
Runs in parallel with BM25 (step 7), not after it - same input clause, two independent
retrieval strategies. **Cost: $0** with the local embedding model.

### 9. Hybrid fusion - `Planned`

Combines BM25's and embedding retrieve's ranked lists into one, weighted 0.4 x BM25 + 0.6
x embedding score. Pure application code. **Cost: $0.**

### 10. Adaptive select - `Planned`

Picks how many of the fused, ranked sections to actually hand to the judgment step -
somewhere between 15 and 56, based on a score-drop-off threshold rather than a fixed
number. **Cost: $0.**

### 11. Build context - `Planned`

Assembles the selected internal excerpts plus the gov clause into the prompt the judgment
step will send. String/prompt assembly. **Cost: $0.**

### 12. LLM judgment - `Planned` (the only paid step in the whole pipeline)

The one step that needs real reasoning: given the gov clause and its matched internal
excerpts, judge whether internal policy satisfies, partially covers, contradicts, or gaps
that clause, and explain why. Needs a real LLM (candidates: Claude Haiku 4.5/Sonnet 5, or
a cheaper embedding-adjacent model is not sufficient here - this step genuinely needs
reasoning). **Cost: ~$0.01-$0.05 per clause**, depending on model and how much context
step 10 selects - see `HYBRID-ANALYSIS-PIPELINE-PLAN.md` for the full cost table.

### 13. Save, loop next clause - `Planned`

Persists the judgment and moves to the next gov clause, reusing step 4's index (never
rebuilding it). **Cost: $0**, a database write.

## How the database keeps track of which document has which rows

Two tables today (phase 1), one row-per-document and one row-per-chunk:

### `nd_local_document_extractions` - one row per document (per engine) - `Built`

Created when Parse runs. Key columns:

| Column | What it holds |
|---|---|
| `id` | This row's own ID - other tables link back to this |
| `stored_document_id` | Which uploaded document this belongs to |
| `engine` | Which Parse engine was used (tesseract / rapidocr / docling-light / docling-glm / azure-di) |
| `status` / `extract_status` / `index_status` | Where this document is right now: pending -> processing -> parsed/extracted/indexed (or failed) |
| `sections_json` | All chunks Extract produced, as one block - where chunk text first lands |

One document can have more than one row here - one per engine, if parsed more than once
for comparison.

### `nd_local_document_extraction_sections` - one row per chunk - `Built`

What Indexing writes to. Key columns:

| Column | What it holds |
|---|---|
| `id` | This chunk's own ID |
| `extraction_id` | Which `nd_local_document_extractions` row this chunk came from |
| `section_index` | This chunk's position in the document (0, 1, 2, ...) |
| `clause_no` | The clause number, if the document had one (e.g. "6.2") |
| `clause_text` | The chunk's actual text - the original wording |
| `source_page` | Which PDF page this chunk came from |
| `embedding` | The 384-number vector - what makes this chunk searchable by meaning |

**To find every chunk belonging to one document**: look up its extraction row (by
`stored_document_id` + `engine`), then find every section row whose `extraction_id`
matches that row's `id`.

**Tracking which extraction method produced a chunk** - not yet a column, since only
structural extraction exists today. Once semantic extraction ships, the natural place to
record it is a new `extract_method` field on `nd_local_document_extractions` ("structural"
or "semantic") - one per document, not per chunk, since the method is chosen once for the
whole document.

## Cost summary, one line each

| Step | Status | Cost |
|---|---|---|
| Upload | Built | $0 |
| Parse (Tesseract / RapidOCR / Docling) | Built | $0 |
| Parse (Azure) | Built | ~$10 per 1,000 pages |
| Extract - structural | Built | $0 |
| Extract - semantic | Planned | $0 (local model) |
| Index | Built | $0 |
| Query expansion | Planned | $0 |
| Sub-obligation split | Planned | $0 |
| BM25 retrieve | Planned | $0 |
| Embedding retrieve | Planned | $0 |
| Hybrid fusion | Planned | $0 |
| Adaptive select | Planned | $0 |
| Build context | Planned | $0 |
| LLM judgment | Planned | ~$0.01-$0.05 per clause |
| Save | Planned | $0 |

Only two things in the entire pipeline can ever cost money: choosing Azure for Parse, and
the LLM judgment step once it's built. Everything else is free regardless of document
size, count, or which extraction method runs.
