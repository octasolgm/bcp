# Regul.ai — Complete Workflow

**Project path:** `C:\Users\Pc\Documents\GitHub\Regul.ai\app`

**Config**
- App URL (local): `http://127.0.0.1:8756`
- Desktop: `npm start` in `app/electron`
- LLM provider: **Anthropic** (`backend/llm/provider.py`)
- Model: **`claude-sonnet-5`**
- Output mode: **structured tool calls** (JSON schemas in `backend/llm/schemas.py`)
- API key: Admin → Settings → AI Provider, or env `ANTHROPIC_API_KEY`
- Policy context for judgment: **full internal manual** if total policy pages ≤ **50**; else **keyword retrieval** per clause (`backend/llm/retrieval.py`)
- Judgment concurrency: **5** parallel calls; rate limit **20** requests/minute
- Ingest: PyMuPDF / python-docx / openpyxl / BeautifulSoup; **Tesseract OCR** when a PDF page yields <50 chars text
- Default dev login: `local@regulai.app` / `changeme`

---

# BCP New Dashboard — V3 Regul workflow (this repo)

BCP **analyse-regul** (`workflow_engine = regul_pipeline`) follows the same **analysis** steps as Regul.ai (forward judgment → reverse map → optional qualitative) but uses **Landing AI** for structural extraction where BCP already parses documents.

| Step | Regul.ai (original) | BCP V3 (`feat/nd-regul-workflow`) |
|------|---------------------|-----------------------------------|
| Parse regulation + internal | Local ingest/OCR | **Landing AI parse** → `landing_ai_parse_cache` |
| Extract regulation clauses/points | Claude `EXTRACTION_SYSTEM_PROMPT` + `record_clauses` | **Landing AI** `gov-requirement-points.schema.json` (library points) |
| Extract internal sections (reverse A) | Claude same prompt + `record_clauses` | **Landing AI** `policy-clauses.schema.json` — **same output shape** as Regul `EXTRACTION_TOOL_SCHEMA` (`clause_no`, `clause_text`, `source_page`) |
| Forward judgment | Claude `JUDGMENT_SYSTEM_PROMPT` | Admin **regul_workflow_llm** + `NdRegulPromptDefaults.JudgmentSystemPrompt` |
| Reverse map each section | Claude `REVERSE_MAPPING_SYSTEM_PROMPT` | Same prompt in `NdRegulPromptDefaults` + admin LLM |
| Reverse **INT** gap rows | `INT {section_ref}` when mapping ≠ `covered` | Same — `NdRegulReverseIntRows` → `regul_forward_findings` |
| Qualitative | Claude one-shot | Admin LLM + `NdRegulPromptDefaults.QualitativeAssessmentSystemPrompt` |

**BCP prompts (analysis only — not Landing extract):** `bcp-api/Services/NewDashboard/NdRegulPromptDefaults.cs` (ported from Regul.ai `app/backend/llm/prompts.py`).

**BCP extraction schemas:** `bcp-api/Schemas/gov-requirement-points.schema.json` (regulation), `bcp-api/Schemas/policy-clauses.schema.json` (internal sections — Regul `EXTRACTION_TOOL_SCHEMA` shape).

**BCP pipeline:** `bcp-api/Services/NewDashboard/NdRegulAnalysisProcessor.cs` · internal extract: `LandingAiPolicyClauseExtractService` (15-page chunks, cached per `file_hash`).

---

# BOX DIAGRAM

```
┌─────────────────────────────────────┐
│ 1. New assessment wizard            │
│    Details (title, entity, etc.)    │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 2. Select / upload Regulatory doc   │
│    Regulatory Library version       │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 3. INGEST regulatory file (local)   │
│    Parse + OCR → extracted_text     │
│    Model: none                      │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 4. Select / upload Internal policy  │
│    Policy Library version           │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 5. INGEST policy file (local)       │
│    Parse + OCR → extracted_text     │
│    Model: none                      │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 6. Create & extract                 │
│    POST /assessments                │
│    POST .../extract                 │
│    status → extraction_review       │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 7. EXTRACT regulatory clauses (AI)  │
│    Claude + EXTRACTION_SYSTEM_PROMPT│
│    tool: record_clauses             │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 8. Clause review (human)            │
│    Edit / split / merge / delete    │
│    Confirm clauses (hard gate)      │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 9. Findings workbench               │
│    Click RUN ANALYSIS               │
│    POST .../analyze (202)           │
│    SSE .../analyze/stream           │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 10. For EACH regulatory clause:     │
│   Load policy context               │
│   Forward judgment (Claude)         │
│   Post-process:bg verify_quotes       │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 11. Reverse coverage                │
│   A) Extract internal sections      │
│     (BCP: Landing AI policy schema) │
│   B) Map each section → clauses     │
│     (LLM — same as Regul.ai)        │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 12. Qualitative assessment (AI)     │
│     one call, full reg + policy     │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 13. Analysis complete               │
│     status → checking               │
│     Checker → finalize → Excel      │
└─────────────────────────────────────┘
```

**Important notes**
- **Upload/ingest** is local (no Claude). Text stored once per `sha256`.
- **Extract** (Step 7) only segments the **regulatory** document. Internal policy is **not** clause-extracted until reverse coverage (Step 11A).
- **Confirm clauses** is mandatory before **Run analysis**.
- There is **no dual-verify second model** — one Claude judgment per clause + server-side quote verification.
- **Run analysis** is one button; backend runs judgment → reverse coverage → qualitative in `run_pipeline()`.

---

# STEPS

---

## Step 1 — Assessment details

- **UI:** `/new` (Assessments → New assessment — **maker** role only)
- **API:** none yet
- **Model:** none
- **Prompt:** none

**Input (wizard state only — not sent until Step 6)**

```json
{
  "title": "TFS gap analysis Q1",
  "entity_name": "Example Bank",
  "regulator": "CBUAE",
  "period": "Q1 2026"
}
```

---

## Step 2 — Select or upload regulatory document

- **UI:** wizard step **Regulatory**; or **Library → Regulatory** (`/library/regulatory`)
- **API (list):** `GET /regulatory-documents`
- **API (new family):** `POST /regulatory-documents`
- **API (upload version):** `POST /regulatory-documents/{id}/versions`
- **Model:** none
- **Prompt:** none

**Upload:** `multipart/form-data` with `file` (PDF/DOCX/etc.)

**What happens**
1. Server runs local `ingest()` — PyMuPDF + OCR if page has <50 chars text
2. Saves `extracted_text`, `pages_json`, `sha256` on `regulatory_document_versions`
3. Duplicate `sha256` in same family reuses existing version row

---

## Step 3 — Select or upload internal policy document

- **UI:** wizard step **Internal policy**; or **Library → Policy** (`/library/policy`)
- **API (list):** `GET /policies`
- **API (new family):** `POST /policies`
- **API (upload version):** `POST /policies/{id}/versions`
- **Model:** none
- **Prompt:** none

Same local ingest + `sha256` dedup as Step 2. Policy text is **not** sent to Claude until **Run analysis**.

---

## Step 4 — Create assessment and run extraction

- **UI:** wizard **Create & extract**
- **API:** `POST /assessments` then `POST /assessments/{id}/extract`
- **Response:** synchronous (waits for extraction)
- **Status:** `draft` → **`extraction_review`**

### 4A — Create assessment

`POST /assessments` — `application/json` — **requires maker role**

```json
{
  "title": "TFS gap analysis Q1",
  "entity_name": "Example Bank",
  "regulator": "CBUAE",
  "period": "Q1 2026",
  "regulatory_document_version_id": 10,
  "policy_version_id": 20
}
```

### 4B — Extract clauses (kicks off Step 5)

`POST /assessments/{id}/extract` — **empty body**

Server loads pinned regulatory `extracted_text` from DB. Policy text **not used** in this call.

---

## Step 5 — Extract regulatory clauses (Claude)

- **Service:** `extract_clauses()` in `backend/llm/pipeline.py`
- **API:** invoked by `POST /assessments/{id}/extract`
- **Provider:** Anthropic Messages API via `LLMProvider.structured_call`
- **Model:** `claude-sonnet-5`
- **max_tokens:** `16000` (`EXTRACTION_MAX_TOKENS`)
- **Chunk size:** 100 pages per chunk (`EXTRACTION_PAGE_CHUNK_SIZE`); retry/split on failure
- **System prompt:** `EXTRACTION_SYSTEM_PROMPT`
- **User prompt:** `build_extraction_prompt(regulatory_text)` + optional numbering skeleton
- **Tool name:** `record_clauses`
- **Schema:** `EXTRACTION_TOOL_SCHEMA`

**Exact system prompt (EXTRACTION_SYSTEM_PROMPT):**

```
You are a compliance analyst. You segment regulatory circulars/guidelines, or internal policy manuals, into discrete, individually-assessable clauses or sections. Preserve the official numbering used in the source document (e.g. 3.2, 4.1.1). Each clause/section should be a single, self-contained obligation or topic that a compliance officer could judge or map on its own. Do not merge unrelated obligations into one clause, and do not split a single obligation across multiple clauses. Ignore preambles, definitions sections, and purely administrative text (e.g. document control, revision history) unless they impose an obligation.

Granularity is not a free choice -- it is dictated by the document's own numbering, and you must go to the DEEPEST level of enumeration the document uses:
- If a numbered section (e.g. "2.3") is itself just an introductory heading whose content is broken into lettered or numbered sub-points (e.g. "(a)", "(b)", "(i)", "(ii)", or a bulleted/ numbered list), the section heading is NOT a clause on its own. Each sub-point is its own clause (clause_no "2.3-a", "2.3-b", ...), containing only that sub-point's own text. Do NOT emit a single clause for "2.3" that concatenates or summarizes all of its sub-points -- that discards the granularity the document itself provides and makes each sub-obligation impossible to assess individually.
- Recurse this rule at every level: if a sub-point itself has further sub-sub-points, those become the clauses instead, not the sub-point.
- Only stop descending when you reach an item the document does not subdivide further -- that item (whatever numbering depth it's at) is the clause.
- Produce exactly one clause per requirement at that deepest level -- never merge two distinct numbered items into one clause, and never split a single numbered item into several.
- Only when the document truly gives no numbering at any level to a distinct obligation (e.g. an unnumbered sentence inside a numbered section) should you use judgment to decide whether it's a separate assessable point; in that case, invent the narrowest reasonable label rather than lumping it into a neighboring numbered clause.

Example: if section "4" reads "LFIs must notify the regulator when: a) ... b) ... c) ... h) ...", produce eight clauses "4-a" through "4-h", one per notification trigger -- never a single clause "4" that lists all eight inside one clause_text.

EXCEPTION -- annexes and reference lists: the deepest-level rule above applies only to *obligation* sections (sections that impose requirements). Annexes, appendices, and reference/illustrative lists -- red-flag indicators, typology examples, case studies, sample scenarios -- are different: treat each ENTIRE annex or typology group as ONE single clause, even if it internally numbers or letters dozens of individual examples. Do not create one clause per red-flag indicator or per typology item. The compliance question for an annex is "does the internal policy's monitoring program consider these typologies as a category," not item-by-item coverage of every example -- splitting them defeats that question and produces dozens of clauses nobody can meaningfully judge individually. Recognize these sections by heading/framing (e.g. "Annex", "Appendix", "Red Flag Indicators", "Typologies", "Examples of...", "Illustrative List") rather than by numbering alone -- a numbered list under an annex heading is still one clause for the whole annex, with clause_text summarizing what the group covers (you do not need to reproduce every individual example verbatim).

Every clause_no you return must be unique. If the source document numbers several distinct sub-obligations under the same heading without giving each its own number (e.g. a bulleted list under section "2"), invent a distinguishing suffix for each one (e.g. "2-a", "2-b") rather than repeating the same clause_no for different clauses.
```

**User prompt (`build_extraction_prompt`) — template:**

```
Segment the following document into discrete, individually-assessable clauses or sections. Preserve official numbering.
{optional numbering skeleton hint from detect_numbering_skeleton()}

--- DOCUMENT ---
{regulatory extracted_text chunk — pages rendered as [page N]\ntext}
--- END DOCUMENT ---
```

**Optional numbering skeleton hint (when detected):**

```
A numbering skeleton was detected automatically by scanning this text for labels at the start of lines, in the order they appear: {label1}, {label2}, ... Segment along these exact labels wherever they mark a real requirement heading ...
```

**Exact tool schema (EXTRACTION_TOOL_SCHEMA):**

```json
{
  "type": "object",
  "properties": {
    "clauses": {
      "type": "array",
      "description": "Array of clause/section OBJECTS -- never plain strings.",
      "items": {
        "type": "object",
        "properties": {
          "clause_no": { "type": "string" },
          "clause_text": { "type": "string" },
          "source_page": { "type": "integer" }
        },
        "required": ["clause_no", "clause_text", "source_page"],
        "additionalProperties": false
      }
    }
  },
  "required": ["clauses"],
  "additionalProperties": false
}
```

**Output example**

```json
{
  "clauses": [
    {
      "clause_no": "3.2-a",
      "clause_text": "The licensed institution shall...",
      "source_page": 12
    }
  ]
}
```

**After extract — code clean-up**
| Code | Why |
|------|-----|
| `dedupe_clause_numbers()` | Unique `clause_no` across chunks |
| Chunk retry / page split | Failed schema → retry with error feedback or split range |

**Save in DB:** `clauses` table (`source = regulatory`)

---

## Step 6 — Human clause review + confirm

- **UI:** `/review/{id}`
- **API:** `GET /assessments/{id}/clauses`, `PUT /clauses/{id}`, split/merge/delete
- **API (gate):** `POST /assessments/{id}/confirm-clauses` — **empty body**
- **Status:** `extraction_review` → **`analyzing`**
- **Model:** none
- **Prompt:** none

**What happens**
1. Maker reviews AI clause list; edit/split/merge as needed
2. **Confirm** locks clauses — required before analysis
3. Navigate to `/workbench/{id}`

---

## Step 7 — Click RUN ANALYSIS on workbench

- **UI:** `/workbench/{id}` — **Run analysis** (maker only, status `analyzing`)
- **API:** `POST /assessments/{id}/analyze` → **202 Accepted** (background)
- **Progress:** `GET /assessments/{id}/analyze/stream` (SSE: `judgment`, `reverse_coverage`, `qualitative_assessment`)
- **Entry:** `run_pipeline()` via `backend/api/analyze.py`
- **Documents:** loaded from pinned version IDs (no re-upload, no second OCR)

**Input**

| Call | Body | Server loads |
|------|------|--------------|
| `POST .../analyze` | **Empty** | Confirmed `clauses` + pinned reg + policy `extracted_text` |
| `GET .../analyze/stream` | `?token=` if needed | Progress events only |

**On success:** status → **`checking`**; persists `findings`, reverse `INT` rows, `qualitative_assessments`

---

## Step 8 — Phase A: Forward judgment (per regulatory clause)

- **Service:** `judge_clause()` → `_call_judgment()`
- **Model:** `claude-sonnet-5`
- **max_tokens:** `8192` (default)
- **System prompt:** `JUDGMENT_SYSTEM_PROMPT`
- **User content (two blocks):**
  1. `build_judgment_context_text(policy_context)` — **cacheable** (full manual or retrieved excerpts)
  2. `build_judgment_query_text(clause_no, clause_text)` — unique per clause
- **Tool name:** `record_judgment`
- **Schema:** `JUDGMENT_TOOL_SCHEMA`
- **Concurrency:** 5 parallel; 20 req/min rate limit

**Policy context rules**
| Policy size | Context sent |
|-------------|--------------|
| ≤ 50 pages total | Full policy text: `=== DOCUMENT: {filename} ===\n{full_text}` |
| > 50 pages | `build_retrieved_context(clause_text, chunks)` — keyword retrieval top chunks |

**Exact system prompt (JUDGMENT_SYSTEM_PROMPT):**

```
You are a compliance analyst comparing a single regulatory requirement clause against a bank's internal policy documents. You judge whether the internal policy documents cover the requirement (design) and default operating status to the same value as design status.

Document-perspective rule -- judge the internal manual as a bank IMPLEMENTING the regulator's requirements, never as a mirror expected to restate the regulatory document itself. Some regulatory clause content only makes sense coming from the regulator and has no implementing counterpart to look for: statements about which OTHER entity types the guidance applies to, "this document does not constitute legislation"/disclaimer-of-legal-force language, or instructions addressed to supervisors or the regulator's own staff rather than to the regulated entity. When a regulatory clause is this kind of regulator-only content, its correct and expected internal-policy counterpart is that the internal document says nothing about it -- this is NEVER a gap. Do not mark such a clause partial or non_compliant merely because the internal manual (correctly, as a bank-facing document) omits it; mark it compliant with an interpretation noting it is regulator-facing content with no implementing counterpart expected.

Vendor/list-provider due diligence (AML-CFT domain term) -- when a regulatory clause requires "due diligence" on an external vendor or list provider used for sanctions/watchlist screening, this means verifying the accuracy and completeness of the data or list that vendor supplies (i.e. confirming the vendor's list actually contains all the required designated names) -- it does NOT mean a general vendor-selection, procurement, or onboarding vetting process. If the internal policy states it ensures/verifies the vendor-supplied list's completeness against the required source lists, that satisfies this kind of requirement even without a separately documented vendor assessment or selection procedure. Do not require a vendor-vetting procedure the clause never actually asked for.

Element-level checking -- when a regulatory clause enumerates multiple discrete required elements (e.g. a list of essential program components, a set of notification triggers, an enumerated list of factors to consider), do not form one holistic impression of the clause as a whole. Instead, go element by element: decide whether each individual element is covered in the internal policy text, and list every element's coverage (covered / not covered, with the specific supporting or missing evidence) in gap_description. Derive overall_status from the aggregate of the element results: compliant only if every element is covered, partial if some but not all are covered, non_compliant if none are covered.

Rules:
- design_status: does the internal policy text address this requirement on paper? compliant = fully covered, partial = partially covered or covered with gaps, non_compliant = not addressed at all.
- operating_status: set equal to design_status (documents alone cannot prove operating effectiveness -- a human will adjust this later with evidence).
- overall_status: same as design_status in MVP.
- confidence: your calibrated confidence (0-1) in this judgment given the available text.
- policy_extract: copy the supporting text VERBATIM, character-for-character, from the internal policy documents provided below. Do not paraphrase, summarize, or fix typos. If you cannot find any directly relevant text, return an empty list and lower your design_status/confidence accordingly.
- document_reference: name the specific internal document and, if identifiable, section or page.
- gap_description: MANDATORY non-empty text whenever overall_status is partial or non_compliant -- never leave this blank for a finding that isn't fully compliant. State two things explicitly: (1) exactly what is missing, and (2) which document it was found in (if partially covered) or was not found in (if absent). For a clause with multiple discrete elements (see element-level checking above), list each element's covered/not-covered status with its evidence rather than one vague sentence. For example: "The IMPTFS Manual addresses screening frequency but does not specify a review cadence, which CBUAE Guidance requires annually" -- not a vague restatement of the regulatory clause. Leave as an empty string only when overall_status is compliant.
- suggested_action: required whenever status is partial or non_compliant; leave as an empty string when fully compliant.
- gap_direction: set to "missing_in_internal" whenever overall_status is partial or non_compliant -- the regulatory requirement is not (fully) covered in the internal policy text. Leave as an empty string when overall_status is compliant.
```

**User block 1 (`build_judgment_context_text`):**

```
--- INTERNAL POLICY DOCUMENT EXCERPTS (retrieved as the sections most likely relevant to a clause -- they may not be the full manual, and if nothing here addresses a given clause it may still be covered elsewhere) ---
{policy_context — full manual OR retrieved chunks}
--- END EXCERPTS ---
```

**User block 2 (`build_judgment_query_text`):**

```
REGULATORY CLAUSE {clause_no}:
{clause_text}

Judge this clause against the excerpts above. If the excerpts don't clearly address the clause, prefer a lower confidence and non_compliant/partial design_status rather than assuming coverage that isn't shown.
```

**On retry (empty `gap_description` on partial/non_compliant):** block 2 gets appended:

```
--- RETRY ---
Your overall_status was '{status}' but gap_description was empty. A partial or non_compliant finding MUST have a non-empty gap_description stating exactly what is missing and naming the document it was/was not found in. Provide that now.
```

**Exact tool schema (JUDGMENT_TOOL_SCHEMA) — required fields:**

`design_status`, `operating_status`, `overall_status`, `confidence`, `interpretation`, `policy_extract`, `document_reference`, `gap_description`, `suggested_action`, `gap_direction`

**Output example**

```json
{
  "design_status": "partial",
  "operating_status": "partial",
  "overall_status": "partial",
  "confidence": 0.82,
  "interpretation": "CBUAE expects annual review of...",
  "policy_extract": ["The bank reviews its program periodically..."],
  "document_reference": "IMPTFS Manual, section 4.2, p.21",
  "gap_description": "Manual does not state annual frequency; guidance requires at least annual review.",
  "suggested_action": "Update section 4.2 to specify annual review cadence.",
  "gap_direction": "missing_in_internal"
}
```

**After judgment — code post-process (not extra AI)**
| Code | Why |
|------|-----|
| `verify_quotes()` | Quotes must appear in policy source text (near-match for OCR/unicode) |
| `_downgrade_for_unverified_quote()` | Unverified quote → cap at partial, `basis_not_verifiable` |
| `_call_judgment` retry loop | Empty `gap_description` → corrective retry (up to 2) |
| `_finding_from_result()` | `needs_review` if confidence < 0.7, unverified quotes, or partial/non_compliant |

**Save in DB:** `findings` per clause

---

## Step 9 — Phase B: Reverse coverage

### 9A — Extract internal sections

**Regul.ai (original):**
- **Service:** `extract_internal_sections()` → reuses `extract_clauses()` on each policy doc
- **Model:** `claude-sonnet-5`
- **System prompt:** same **`EXTRACTION_SYSTEM_PROMPT`**
- **User prompt:** same **`build_extraction_prompt()`** on **internal** policy text
- **Chunk size:** **15** pages (`INTERNAL_SECTION_PAGE_CHUNK_SIZE`)
- **Tool:** `record_clauses` → mapped to `InternalSection` (`section_ref`, `section_text`, `source_page`, `source_doc`)

**BCP V3 (this repo):**
- **Service:** `LandingAiPolicyClauseExtractService.ExtractFromMarkdownAsync()` during reverse phase (`NdRegulAnalysisProcessor`)
- **Model:** Landing AI `extract-latest` (`LandingAi:ExtractModel`) — **not** the admin regul LLM
- **Schema:** `Schemas/policy-clauses.schema.json` — same fields as Regul **`EXTRACTION_TOOL_SCHEMA`**: `clauses[]` with `clause_no`, `clause_text`, `source_page`
- **Chunk size:** **15** pages (`LandingAiPolicyClauseExtractService.InternalSectionPagesPerChunk`)
- **Cache:** `landing_ai_extract_cache` keyed by internal `file_hash` + schema `policy_clauses_v1`
- **Save in DB:** `regul_internal_sections` (`section_ref` ← `clause_no`, `section_text`, `source_page`, `source_doc`)

### 9B — Map each internal section

- **Service:** `reverse_map_section()`
- **Model:** `claude-sonnet-5`
- **System prompt:** `REVERSE_MAPPING_SYSTEM_PROMPT`
- **User content:**
  - Cached: `build_reverse_mapping_context_text(all regulatory clauses)`
  - Per section: `build_reverse_mapping_query_text(section)`
- **Tool name:** `record_mapping`
- **Schema:** `REVERSE_MAPPING_TOOL_SCHEMA`

**Exact system prompt (REVERSE_MAPPING_SYSTEM_PROMPT):**

```
You are a compliance analyst performing reverse-coverage analysis: given a section of a bank's internal policy manual and the full list of extracted regulatory requirement clauses, determine which regulatory clause(s) (if any) this internal section implements.

Rules:
- mapping: "covered" if this section clearly implements one or more of the listed regulatory clauses (list them in mapped_clause_nos). "no_regulatory_basis" if this is legitimate operational content (e.g. definitions, contact lists, system configuration, internal escalation paths) that simply isn't required by any of the listed clauses -- this is NORMAL, not a defect; internal policies routinely contain more detail than the regulation demands. "basis_not_verifiable" if the section's own text claims or implies it addresses a specific regulatory requirement, but none of the listed clauses actually match that claim.
- mapped_clause_nos: for "covered", the clause_no values (exactly as given below) this section implements. For "no_regulatory_basis" or "basis_not_verifiable", this is normally empty -- BUT if you can identify a SPECIFIC regulatory clause this section relates to or conflicts with (e.g. the section states a numeric threshold, timeline, or rule that differs from one stated in a particular listed clause), include that clause_no here even though the section doesn't "implement" it. This is what lets a human reviewer see exactly which regulatory requirement is at odds with this internal content -- e.g. a section claiming a 25% ownership threshold when clause 3.4-1 states 50% should list "3.4-1" here even though mapping is "basis_not_verifiable" or contradicts_regulation is true.
- confidence: your calibrated confidence (0-1) in this mapping decision.
- contradicts_regulation: true ONLY if this section actively conflicts with a regulatory requirement -- e.g. it permits something the regulation prohibits, states a threshold or timeline that contradicts one in the listed clauses, or otherwise directs staff to do something the regulation forbids. This is a stronger claim than "not required by the regulation" -- ordinary no_regulatory_basis/basis_not_verifiable content is NOT a contradiction, just unrelated or unconfirmed. Always false when mapping is "covered".
- commentary: one or two sentences explaining the decision -- if contradicts_regulation is true, state exactly what conflicts with what.
```

**Cached user block (`build_reverse_mapping_context_text`):**

```
--- REGULATORY CLAUSES (map the internal section below against these) ---
{clause_no}: {clause_text}
{clause_no}: {clause_text}
...
--- END REGULATORY CLAUSES ---
```

**Per-section user block (`build_reverse_mapping_query_text`):**

```
INTERNAL POLICY SECTION ({section_ref}, {source_doc}):
{section_text}

Which regulatory clause(s) above, if any, does this section implement?
```

**Tool output example**

```json
{
  "mapped_clause_nos": ["3.4-1"],
  "mapping": "basis_not_verifiable",
  "commentary": "Section states 25% ownership threshold; clause 3.4-1 requires 50%.",
  "confidence": 0.88,
  "contradicts_regulation": true
}
```

**Sections that become gap rows:** `no_regulatory_basis` or `basis_not_verifiable` (and conflicts via `contradicts_regulation`) → synthetic rows with `clause_no` like **`INT 7.9-2`** + matching findings. In Regul.ai these land in `clauses` + `findings`; in BCP V3 they are stored as `regul_forward_findings` with `ClauseNo = "INT …"` via `NdRegulReverseIntRows` (reverse mapping LLM call not yet wired).

---

## Step 10 — Phase C: Qualitative assessment

- **Service:** `run_qualitative_assessment()`
- **Model:** `claude-sonnet-5`
- **System prompt:** `QUALITATIVE_ASSESSMENT_SYSTEM_PROMPT`
- **User prompt:** `build_qualitative_assessment_prompt(regulatory_text, policy_text)` — **full** both documents
- **Tool name:** `record_assessment`
- **Schema:** `QUALITATIVE_ASSESSMENT_TOOL_SCHEMA`
- **Calls:** **1** per assessment (not per clause)

**Exact system prompt (QUALITATIVE_ASSESSMENT_SYSTEM_PROMPT):**

```
You are a compliance documentation reviewer. You assess whether a bank's internal policy manual is written well enough that the staff member who must apply it could read it and be ready to implement the regulatory requirements it is meant to address.

Rate exactly these five dimensions, each "strong", "adequate", or "weak", with commentary and at least one quoted or closely-paraphrased example from the policy text for each:
- clarity_and_tone: plain, unambiguous writing; consistent terminology.
- structure_and_navigation: logical order; sections are easy to find.
- depth_of_implementation_detail: requirements explained operationally (who does what, when, how) rather than merely restated from the regulation.
- alignment_with_regulatory_language: uses/tracks the regulator's own language and intent.
- actionability_for_staff: procedures, responsibilities, and timelines concrete enough to execute.

Also give an overall_rating (strong/adequate/weak), 2-5 strengths, and 2-5 concrete improvement_recommendations.
```

**User prompt (`build_qualitative_assessment_prompt`):**

```
--- REGULATORY DOCUMENT ---
{full regulatory extracted_text}
--- END REGULATORY DOCUMENT ---

--- INTERNAL POLICY DOCUMENT ---
{full policy extracted_text}
--- END INTERNAL POLICY DOCUMENT ---

Assess the internal policy document per the rubric. Cover all of: clarity_and_tone, structure_and_navigation, depth_of_implementation_detail, alignment_with_regulatory_language, actionability_for_staff.
```

**Tool schema (required top-level):** `overall_rating`, `dimensions[5]`, `strengths[]`, `improvement_recommendations[]`

**Save in DB:** `qualitative_assessments` (one row per assessment — summary card in UI, not findings table rows)

---

## Step 11 — Results + downstream workflow

- **UI:** workbench findings table, qualitative card, detail dialog
- **Status:** `checking` → checker accept/override → optional reviewer → **`finalized`**
- **Export:** `GET /assessments/{id}/export/xlsx` (Book 6 layout) when `finalized`
- **Model:** none (human workflow + Excel writer)

| Action | API |
|--------|-----|
| List findings | `GET /assessments/{id}/findings` |
| Accept finding | `POST /findings/{id}/accept` |
| Override finding | `POST /findings/{id}/override` |
| Finalize | `POST /assessments/{id}/finalize` |
| Export Excel | `GET /assessments/{id}/export/xlsx` |

---

# What each AI step is for

| Step | Name | Purpose |
|------|------|---------|
| **5** | Extract clauses (Claude) | Turn regulatory text into numbered clauses for review |
| **8** | Forward judgment (Claude) | For each clause: does policy cover it? → compliant / partial / non_compliant + evidence |
| **9** | Reverse coverage | Each internal section vs all rules → **`INT …`** rows for conflicts / no basis / unverified basis (mapping ≠ `covered`) |
| **10** | Qualitative (Claude) | One overall score of policy writing quality |

**Short path:** Upload (local OCR) → Claude extracts clauses → human confirms → Claude judges + reverse + qualitative → humans finalize & export.

---

# DB tables

| Table | Role |
|-------|------|
| `regulatory_documents` / `regulatory_document_versions` | Regulatory library + extracted text |
| `policies` / `policy_versions` | Policy library + extracted text |
| `assessments` | Workflow status; pins to version IDs |
| `clauses` | Regulatory clauses + reverse synthetic rows (`INT `) |
| `findings` | Per-clause gap analysis result |
| `qualitative_assessments` | One qualitative summary per assessment |
| `users` / `auth_sessions` | Login and roles (maker, checker, reviewer, admin) |

---

# Code map

| Piece | Regul.ai | BCP V3 |
|-------|----------|--------|
| Analysis prompts | `app/backend/llm/prompts.py` | `bcp-api/Services/NewDashboard/NdRegulPromptDefaults.cs` |
| Extraction tool schema (internal) | `EXTRACTION_TOOL_SCHEMA` in `schemas.py` | `bcp-api/Schemas/policy-clauses.schema.json` |
| Reg extraction | Claude + `record_clauses` | `LandingAiGovExtractService` + gov schema |
| Internal section extract | Claude + `record_clauses` | `LandingAiPolicyClauseExtractService` |
| INT reverse rows | `pipeline.py` `_reverse_coverage_findings` | `NdRegulReverseIntRows` |
| Pipeline | `pipeline.py` `run_pipeline()` | `NdRegulAnalysisProcessor` |
| Tool schemas (judgment/reverse/qual) | `app/backend/llm/schemas.py` | (planned — port when LLM calls wired) |
| Anthropic adapter | `app/backend/llm/provider.py` | Admin LLM via `RegulWorkflowLlmSettingsService` |
| Policy retrieval | `app/backend/llm/retrieval.py` | (planned for forward judgment) |
| Analyze API | `app/backend/api/analyze.py` | `AnalysisRunsController` `/start` → `regul_pipeline` |
| Ingest / parse | `app/backend/ingest/parsers.py` | `NdInternalParseService` + Landing parse cache |

---

# Assessment status

| status | User action |
|--------|-------------|
| `draft` | Created; extraction may be in progress |
| `extraction_review` | Review clauses on `/review/{id}` |
| `analyzing` | Click **Run analysis** on workbench |
| `checking` | Review findings; checker workflow |
| `amending` / `reviewing` | Send-back or optional reviewer |
| `finalized` | Export Excel |
