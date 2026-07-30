# ND Analyse V8 — Complete Workflow

**Scope:** `/nd/analyse-v8` + `/nd/*` APIs (not legacy `/old/*` or `dual-verify-kafka`).

**Config**
- Landing AI base: `https://api.va.landing.ai`
- Parse model: `dpt-2-latest`
- Extract model: `extract-latest`
- Compare prompt version: **V2**
- Storage bucket: `doc`
- Dual verify model: **set by admin** in Settings (`nd_system_settings` key `dual_verify_llm`) — Google / Gemini, xAI / Grok, OpenAI, Anthropic, etc. Not fixed.

---

# BOX DIAGRAM

```
┌─────────────────────────────────────┐
│ 1. Upload Regulation Document       │
│    (user uploads PDF/Word)          │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 2. Store file                       │
│    Supabase Storage bucket: doc     │
│    DB: stored_documents             │
│    DB: regulation_documents         │
│    extraction_status = pending      │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 3. User clicks Extract              │
│    POST /nd/regulation-documents/   │
│         {id}/extract                │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 4. PARSE (Landing AI)               │
│    API: POST /v1/ade/parse          │
│    model: dpt-2-latest              │
│    PDF → markdown + page markers    │
│    Save: landing_ai_parse_cache     │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 5. EXTRACT (Landing AI)             │
│    API: POST /v1/ade/extract        │
│    model: extract-latest            │
│    schema: gov-requirement-points   │
│    Save: regulation_points          │
│    extraction_status = completed    │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 6. Upload Internal Document         │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 7. Store + PARSE internal           │
│    POST /v1/ade/parse               │
│    model: dpt-2-latest              │
│    Save: landing_ai_parse_cache     │
│    ★ Parse once per file_hash       │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 8. Analyse V8 page                  │
│    Select regulation points         │
│    Select internal doc(s)           │
│    Click RUN                        │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 9. Create run + Start run           │
│    POST /nd/analysis-runs           │
│    POST .../start                   │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 10. For EACH selected point:        │
│   Phase 1: Landing AI Compare V2    │
│   Phase 2: Dual Verify LLM          │
│   Agreement → final_status          │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 11. Run finished → gap analysis UI  │
└─────────────────────────────────────┘
```

---

# STEPS

---

## Step 1 — Upload Regulation Document

- **UI:** `/nd/regulation-documents`
- **API:** `POST /nd/regulation-documents/upload`
- **Input:** file upload (PDF or Word) + optional `departmentId`
- **Model:** none
- **Prompt:** none

**What happens**
1. File goes to Supabase Storage (`doc` bucket, path `regulations/nd/...`)
2. Rows created in `stored_documents` + `regulation_documents` with `extraction_status = pending`

---

## Step 2 — User clicks Extract on regulation

- **API:** `POST /nd/regulation-documents/{id}/extract`
- **Response:** `202 Accepted` (background job)
- **Status in DB:** `extraction_status = processing`

This job runs **Step 3 (Parse)** then **Step 4 (Extract)** automatically.

---

## Step 3 — Parse regulation (Landing AI)

- **API:** `POST https://api.va.landing.ai/v1/ade/parse`
- **Model:** `dpt-2-latest`
- **Prompt:** none
- **Input:** PDF/Word bytes (if PDF > 99 pages → split into chunks)

**Output example (markdown + page markers)**

```
<!-- BCP_PDF_PAGE:1 -->
# Central Bank Regulation

<!-- BCP_PDF_PAGE:2 -->
## 2.6 Internal Controls
2.6.5 The licensed institution shall maintain...
```

**Save in DB:** `landing_ai_parse_cache` keyed by `file_hash`

---

## Step 4 — Extract regulation points (Landing AI)

- **API:** `POST https://api.va.landing.ai/v1/ade/extract`
- **Model:** `extract-latest`
- **Prompt:** none — schema is the instruction
- **Schema file:** `bcp-api/Schemas/gov-requirement-points.schema.json`
- **Input:** markdown from Step 3 + schema

**Exact schema:**

```json
{
  "type": "object",
  "properties": {
    "points": {
      "type": "array",
      "description": "Every official numbered regulatory section and requirement clause in the document. Emit each point_id exactly once at every hierarchy level present (e.g. 2, 2.1, 2.1.1, 3.1, 3.1.1). Extract section headings (2.1, 8.6, 11.1) when they have introductory or requirement body text. Extract unnumbered callout boxes using their exact heading as point_id. Skip table-of-contents lines and bare part titles with no body (e.g. Part III only).",
      "items": {
        "type": "object",
        "properties": {
          "point_id": {
            "type": "string",
            "description": "Official document numbering when present (e.g. 2.1, 3.1, 3.1.4). For unnumbered callout boxes use the exact heading text from the document. Never invent IDs like Part III or Elements of an AML/CFT Program."
          },
          "title": {
            "type": "string",
            "description": "Short heading/title for the point when present in the document"
          },
          "text": {
            "type": "string",
            "description": "Full verbatim body text for the point, including all sub-bullets, conditions, and citations. Do not summarize."
          },
          "section": {
            "type": "string",
            "description": "Parent section reference with heading, e.g. 3.1 Summary of obligations"
          },
          "page_hint": {
            "type": "integer",
            "description": "1-based PDF viewer page index where this requirement appears (not printed/footer page numbers). Use 0 if unknown."
          },
          "point_type": {
            "type": "string",
            "enum": ["mandatory", "informational", "definition"],
            "description": "mandatory = enforceable requirement to compare; informational = introduction/purpose/background (skip compare); definition = glossary term only (skip compare)"
          }
        },
        "required": ["point_id", "text"]
      }
    }
  },
  "required": ["points"]
}
```

**After extract — code clean-up (not AI)**
| Code | Why |
|------|-----|
| `GovPointsParser.ParseFromExtraction` | JSON → DB rows |
| `PolicyPageResolver.ResolveGovPointPage` | Fix page using `<!-- BCP_PDF_PAGE:N -->` |
| `GovPointClassifier` | Mark intro/annex points (often skip compare) |

**Save in DB:** `regulation_points`, `landing_ai_extract_cache`, `regulation_documents.extraction_status = completed`

---

## Step 5 — Upload Internal Document

- **UI:** `/nd/internal-documents` (or upload on Analyse V8)
- **API:** `POST /nd/internal-documents/upload`
- **Input:** file upload (PDF or Word)
- **Model:** none
- **Prompt:** none

**Save in DB:** `stored_documents` with `parse_status = pending`

---

## Step 6 — Parse internal document (Landing AI)

Triggered by:
- `POST /nd/internal-documents/{id}/parse` (manual), **OR**
- automatically first time analysis needs it

- **API:** `POST https://api.va.landing.ai/v1/ade/parse`
- **Model:** `dpt-2-latest`
- **Prompt:** none

**Output:** markdown with `<!-- BCP_PDF_PAGE:N -->` markers

**Cache rule (`NdInternalParseService.EnsureParsedAsync`)**
1. Look up `landing_ai_parse_cache` by `file_hash`
2. If markdown exists → return cache (no Landing AI call)
3. Only on cache miss → parse once, then save

For 100 points + 1 internal doc:
- Landing AI **parse** = **1 time** (or 0 if cached)
- Landing AI **compare** = up to **100 times** (one per point)
- Dual verify LLM = up to **100 times** (one per point)

---

## Step 7 — Click RUN on Analyse V8

- **UI:** `/nd/analyse-v8` — one **Run** button
- **User selects:** regulation points + internal doc(s)

Behind one click, UI does two calls:

### 7A — Create run (save selection)

- **API:** `POST /nd/analysis-runs`
- **Status:** `draft` (momentary — immediately followed by Start)

**Body example**

```json
{
  "name": "Internal_Policy.pdf × CBUAE TFS",
  "selectedPointsSnapshot": [
    {
      "pointNumber": "2.6.5",
      "pointId": "2.6.5",
      "pointTitle": "Internal Controls",
      "pointContent": "The licensed institution shall maintain...",
      "pageReference": "2.6.5 · p. 12",
      "regulationPointId": "44444444-...",
      "regulationDocumentId": "22222222-..."
    }
  ],
  "selectedInternalDocIds": ["33333333-3333-3333-3333-333333333333"],
  "selectedRegulationDocIds": ["22222222-2222-2222-2222-222222222222"]
}
```

**Save in DB:** `analysis_runs` + `analysis_points` (one row per point, `landing_ai_status = pending`, `dual_verify_status = pending`)

### 7B — Start run (real work)

- **API:** `POST /nd/analysis-runs/{id}/start`
- **Service:** `NdAnalysisProcessor.ProcessRunAsync` (background)
- **UI polls:** `GET /nd/analysis-runs/{id}/status`
- **Save in DB:** `analysis_runs.status = running`

---

## Step 8 — Phase 1: Landing AI Compare (per point)

- **Service:** `LandingAiCompareService.ComparePointAsync`
- **API:** `POST https://api.va.landing.ai/v1/ade/extract`
- **Model:** `extract-latest`
- **Schema:** `bcp-api/Schemas/compliance-comparison-v2.schema.json`
- **Prompt version:** **V2** (`ComparePromptVersion.V2` — default for ND / analyse-v8)
- **Input:** V2 prompt + internal markdown (from `landing_ai_parse_cache`) + requirement point text
- **PDF sent?** No — ADE extract accepts markdown only. PDF is used in Step 9 (Dual Verify)
- **Multiple internal files?** All markdowns appended (`--- DOCUMENT 1: ... ---`, `--- DOCUMENT 2: ... ---`)

**Exact prompt (V2):**

```
You are an expert regulatory compliance auditor. Evaluate the ENTIRE requirement point — including every sub-obligation in the clause — against ALL attached internal process documents. Use semantic intent analysis, not keyword matching.

Rules:
- Regulatory framework: infer from REQUIREMENT POINT TO CHECK (law, regulation, guideline, circular, or policy). Do not assume a fixed framework or document name.
- Search EVERY attached internal document before concluding Non-Compliant. Compliant if ANY attached document fully addresses all sub-obligations.
- Compare by regulatory meaning and operational control. Different wording is acceptable when the control is equivalent.
- Do not mark Non-Compliant when detailed internal policy clearly covers the same obligations with different wording.
- Compliant: all sub-obligations operationally covered. Partial Compliant: only some covered. Non-Compliant: no equivalent procedure in any attached document after searching all of them.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status (Compliant 86-100, Partial Compliant 31-85, Non-Compliant 0-30)
- uae_response_compliance_level: primary evidence citation(s). Use one line per source when evidence spans multiple documents or pages.
  Format each line: [Document Name], Section [header or number], Page [N]: 'verbatim internal quote'
  Page [N] MUST be the 1-based PDF viewer page index. Include section numbers from document headers when present (e.g. Section 7.28).
  Use a multi-sentence quote when needed to show intent is met. If Non-Compliant, output exactly: No corresponding procedure found.
- fulfilled_clauses: one bullet (•) per sub-obligation that IS satisfied. Each bullet MUST include its source ref:
  • [sub-obligation summary] — [Document Name], Section [X], Page [N]: 'verbatim quote'
  Output None if nothing is covered.
- corrective_action_plan: required when Partial or Non-Compliant. Start with Gap(s): then numbered items (1) Missing: [sub-intent not met], Fix: [action]. Empty string when Compliant.
- suggested_responsibility: department or role for corrective action. Empty string when Compliant.
- reference_pdf: exact internal document file name(s) that contain the cited evidence. For multiple documents, comma-separate the file names.
- Return structured JSON matching the provided schema only (no markdown fences)
```

**Full text sent to Landing AI — 1 internal file:**

```
{prompt above}

---
INPUT DATA:

ATTACHED INTERNAL PROCESS DOCUMENT (Internal_Policy.pdf — parsed markdown from internal policy PDF; search this entire document before concluding Non-Compliant):

<!-- BCP_PDF_PAGE:1 -->
# Internal Policy
<!-- BCP_PDF_PAGE:7 -->
## 7.28 Control Framework
The bank shall maintain internal controls...
(...entire cached markdown from landing_ai_parse_cache...)

REQUIREMENT POINT TO CHECK:

2.6.5 Internal Controls

The licensed institution shall maintain...
```

**Full text sent to Landing AI — multiple internal files:**

```
{prompt above}

---
INPUT DATA:

ATTACHED INTERNAL PROCESS DOCUMENTS (2 PDFs — evaluate compliance across ALL documents; search every document before concluding Non-Compliant; cite evidence from any document with its exact file name):

--- DOCUMENT 1: Policy_A.pdf (parsed markdown) ---

<!-- BCP_PDF_PAGE:1 -->
...full markdown of Policy_A...

--- DOCUMENT 2: Policy_B.pdf (parsed markdown) ---

<!-- BCP_PDF_PAGE:1 -->
...full markdown of Policy_B...

REQUIREMENT POINT TO CHECK:

2.6.5 Internal Controls

The licensed institution shall maintain...
```

**Exact schema fields (required):** `requirement_id`, `requirement_text`, `uae_response_compliance_level`, `comply_status`, `compliance_confidence_percentage`, `fulfilled_clauses`, `corrective_action_plan`, `suggested_responsibility`

**Output example (formatted message saved for UI / dual verify)**

```
2.6.5 Internal Controls
The licensed institution shall maintain...

Reference PDF :
Internal_Policy.pdf

Output/Response :
[Internal_Policy.pdf], Section 7.28, Page 7: 'The bank shall maintain internal controls...'

Fulfilled clauses :
• Maintain internal controls — [Internal_Policy.pdf], Section 7.28, Page 7: 'The bank shall maintain...'

Comply Yes/No (Status) : Compliant
Compliance Confidence % : 92%
Corrective Action Plan :
N/A
Responsibility :
N/A
```

**After compare — code clean-up (not extra AI)**
| Code | Why |
|------|-----|
| `LandingAiComparisonNormalizer` | Clean status / confidence / weak CAP |
| `PolicyPageResolver` | Fix cited page using markdown page markers |
| `LandingAiComparisonFormatter` | Build message block above |

**Save in DB:** `analysis_points.landing_ai_status = success`, `landing_ai_result`, `landing_ai_action_plan`, cache in `landing_ai_extract_cache` (`schema_key = compliance_comparison`)

---

## Step 9 — Phase 2: Dual Verify (per point)

- **Service:** `DualVerifyLlmService` via `NdAnalysisProcessor`
- **Model:** whatever admin set in Settings (`dual_verify_llm`) — e.g. `google` + `gemini-3.5-flash`, `anthropic` + `claude-...`, `openai` + `gpt-...`, `xai` + `grok-...`
- **Prompt version:** **V2** Pass 2 rules
- **Input:** internal PDF(s) as base64 + Pass 2 prompt + Landing Phase 1 message + markdown supplement
- **PDF sent?** Yes — original internal PDF file(s) attached to the LLM call
- **`Attached PDF:` line** = internal policy PDF name(s) selected for this run
- **`Reference PDF :` inside Pass 1 block** = from Step 8 Landing AI result (which internal file(s) had evidence)
- **If no PDF bytes:** falls back to text-only (`AnalyzeTextAsync`)

**Exact prompt (V2) — full assembled text:**

```
DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)
You are the second verifier. Landing AI (Pass 1) already analyzed this requirement.
Re-read the attached internal PDF(s) and produce your own assessment.

Pass 2 rules (V2):
- Independently search ALL attached internal PDF(s) and markdown for evidence on EVERY sub-obligation.
- Use the same semantic standards as Pass 1 — confirm or correct, not stricter keyword matching.
- Search every attached document before concluding Non-Compliant. Compliant if any document satisfies all sub-obligations.
- If Pass 1 evidence is accurate and complete, align with the same status and similar confidence.
- Cite each source with: [Document Name], Section [X], Page [N]: "verbatim quote". One line per document/page when multiple sources apply.

Attached PDF: Internal_Policy.pdf
  (if 2+ internal files: Attached PDFs (2): Policy_A.pdf, Policy_B.pdf)

LANDING AI PASS 1 (reference only):
---
2.6.5 Internal Controls
The licensed institution shall maintain...

Reference PDF :
Internal_Policy.pdf

Output/Response :
[Internal_Policy.pdf], Section 7.28, Page 7: 'The bank shall maintain internal controls...'

Fulfilled clauses :
• Maintain internal controls — [Internal_Policy.pdf], Section 7.28, Page 7: '...'

Comply Yes/No (Status) : Compliant
Compliance Confidence % : 92%
Corrective Action Plan :
N/A
Responsibility :
N/A
---

REQUIREMENT POINT TO CHECK:
2.6.5 Internal Controls
The licensed institution shall maintain...

INTERNAL DOCUMENT MARKDOWN (parsed text — use with attached PDF(s) for accuracy):
---
{cached markdown}
---

Your response MUST use exactly this block format (field labels matter for automated comparison):

[point_id and title]
[full requirement text]

Reference PDF :
[document file name(s) with evidence — comma-separate when multiple]

Output/Response :
[Document Name], Section [X], Page [N]: "verbatim quote"
(one line per source when multiple documents/pages apply; or exactly: No corresponding procedure found.)

Fulfilled clauses :
• [sub-obligation] — [Document Name], Section [X], Page [N]: "quote"
(use None only if nothing is covered)

Comply Yes/No (Status) : Compliant | Partial Compliant | Non-Compliant
Compliance Confidence % : [0-100]%
Corrective Action Plan :
Gap(s): ... OR empty / N/A if Compliant
Responsibility :
[role] OR empty / N/A if Compliant
```

**Output:** same style text block as Phase 1 (from second model)

---

## Step 10 — Agreement + final_status

After Phase 1 and Phase 2, code compares both answers.

**Agreement** = do Pass 1 and Pass 2 agree?

| agreement value | meaning |
|-----------------|--------|
| `aligned` | same status (or close) → dual verify **passed** |
| `confidence_gap` | same direction but confidence far apart → dual verify **failed** |
| `status_mismatch` | different status (e.g. Compliant vs Partial) → dual verify **failed** |
| `both_non_compliant` | both say Non-Compliant → dual verify **failed** (but clear non-compliant) |

**final_status** = status shown as the point result

| agreement | final_status |
|-----------|--------------|
| `both_non_compliant` | `non_compliant` |
| `status_mismatch` or `confidence_gap` | `partial_compliant` |
| `aligned` | same as Landing Phase 1 (`compliant` / `partial_compliant` / `non_compliant`) |

If Phase 1 failed → Phase 2 skipped (`dual_verify_status = skipped`).

**Save in DB — example**

| field | example |
|-------|---------|
| `google_ai_status` | `compliant` (Pass 2 status; legacy field name) |
| `google_ai_result` | `{ "message": "...", "agreement": { "status": "aligned" } }` |
| `dual_verify_status` | `passed` |
| `final_status` | `compliant` |

---

## Step 11 — Run finished

- **Save in DB (`analysis_runs.status`):**
  - `completed` — normal finish
  - `dual_verify_failed` — some points failed dual verify
  - `landing_ai_complete` — Landing done but dual verify incomplete
- **UI:** results / gap analysis for that run

---

# Extra APIs

| Action | API |
|--------|-----|
| Stop run | `POST /nd/analysis-runs/{id}/stop` |
| Rerun one point | `POST /nd/analysis-runs/{id}/rerun-point/{pointId}` |
| Rerun dual verify one point | `POST /nd/analysis-runs/{id}/rerun-dual-verify/{pointId}` |
| Rerun dual verify all | `POST /nd/analysis-runs/{id}/rerun-dual-verify/all` |
| Stop / resume reg extract | `POST /nd/regulation-documents/{id}/extract/stop` |
| Refresh page refs (no AI) | `POST /nd/regulation-documents/{id}/refresh-page-references` |

---

# DB tables

| Table | Role |
|-------|------|
| `stored_documents` | file metadata; `parse_status` for internal; hash/path |
| `regulation_documents` | regulation row; `extraction_status` |
| `regulation_points` | extracted gov points |
| `landing_ai_parse_cache` | PDF → markdown (+ page markers), keyed by `file_hash` |
| `landing_ai_extract_cache` | gov extract + compare cache |
| `analysis_runs` | run header / status |
| `analysis_points` | per-point Phase 1 + Phase 2 + `final_status` |
| `action_plan_histories` | CAP versions |
| `nd_system_settings` | admin dual-verify provider/model (`dual_verify_llm`) |

---

# Code map

| Piece | File |
|-------|------|
| Compare V2 prompt | `bcp-api/Services/LandingAi/LandingAiComparePromptBuilder.cs` → `PromptTemplateV2` |
| Dual verify V2 prompt | `bcp-api/Services/GovPointsService.cs` → `DualVerifyPromptBuilder` |
| Run processor | `bcp-api/Services/NewDashboard/NdAnalysisProcessor.cs` |
| Internal parse + cache | `bcp-api/Services/NewDashboard/NdInternalParseService.cs` |
| Page markers | `bcp-api/Services/LandingAi/PolicyPageResolver.cs` |
| Gov extract schema | `bcp-api/Schemas/gov-requirement-points.schema.json` |
| Compare schema | `bcp-api/Schemas/compliance-comparison-v2.schema.json` |
| Analyse V8 UI (Run click) | `bcp-web/src/app/pages/analyse-v8/analyse-v8.component.ts` |
