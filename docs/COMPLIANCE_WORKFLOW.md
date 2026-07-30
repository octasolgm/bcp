# BCP ND — Complete Workflow (Analysis V3 Regul)

**Scope:** `/nd/analyse-regul` (Analysis Version **V3 — Regul Workflow**) + shared `/nd/*` APIs.  
**Production V8 (unchanged):** `/nd/analyse-v8` — Landing AI compare + dual verify (`workflow_engine = bcp_landing`).  
**Regul.ai source prompts:** `C:\Users\Pc\Documents\GitHub\Regul.ai\app\backend\llm\prompts.py` (ported in pipeline implementation).

**Config**
- App URL (local): `http://localhost:3002/nd/analyse-regul`
- API (local): `http://localhost:5100`
- Analysis versions menu: `/nd/analysis-versions` → **V3 — Regul Workflow**
- Setup parse/extract: **Landing AI** (`https://api.va.landing.ai`) — same as V8 (`dpt-2-latest` parse, `extract-latest` extract, schema `gov-requirement-points`)
- Analysis pipeline: **Regul.ai-style** — forward judgment → reverse coverage → optional qualitative
- LLM provider: **Admin-selected** (`nd_system_settings` key `regul_workflow_llm`) — Google / OpenAI / Anthropic / xAI via `RegulWorkflowLlmSettingsService`
- Model: **whatever admin saves** (not hardcoded `claude-sonnet-5`; Regul.ai app uses fixed Sonnet 5)
- Output mode (planned): **structured tool calls** — same schemas as Regul.ai (`record_judgment`, `record_mapping`, `record_assessment`)
- API keys: server `appsettings` / env (`Gemini:ApiKey`, `Anthropic:ApiKey`, etc.)
- Policy context for judgment (planned): **full internal manual** if total policy pages ≤ **50**; else **keyword retrieval** per clause (Regul.ai `retrieval.py` logic to port)
- Judgment concurrency (planned): **5** parallel; rate limit **20** req/min (Regul.ai defaults)
- Default dev login: ND auth (`/nd/auth/login`) — maker / checker / reviewer roles

**Implementation status**
| Phase | Status |
|-------|--------|
| UI clone, routes, Analysis Versions, confirm + qualitative | **Done** |
| `regul_*` tables + `workflow_engine` on `analysis_runs` | **Done** |
| Admin Regul LLM setting | **Done** |
| Forward / reverse / qualitative LLM calls | **Not yet** (processor placeholders) |
| Gap UI reads `regul_*` | **Not yet** |

---

# BOX DIAGRAM

```
┌─────────────────────────────────────┐
│ 1. Open Analysis V3               │
│    /nd/analyse-regul                │
│    (Analysis Version → V3)          │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 2. Select / upload Regulatory doc   │
│    Regulation documents library     │
│    (or upload → extract)            │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 3. PARSE + EXTRACT regulatory       │
│    Landing AI parse + extract       │
│    DB: regulation_points            │
│    Model: dpt-2-latest / extract    │
│    (NOT Regul.ai Claude extract)    │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 4. Select / upload Internal policy  │
│    Internal documents library       │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 5. PARSE internal policy            │
│    Landing AI parse only            │
│    DB: landing_ai_parse_cache       │
│    Model: dpt-2-latest              │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 6. Select regulation points         │
│    Doc or library + point checkboxes│
│    (replaces Regul clause extract)  │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 7. Confirm + RUN dialog             │
│    Type "start"                     │
│    [ ] Qualitative (optional)       │
│    Shows admin-selected LLM         │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 8. Create run + start pipeline      │
│    POST /nd/analysis-runs           │
│    POST .../start                   │
│    workflow_engine = regul_pipeline │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 9. Live progress on analyse-regul   │
│    regul_pipeline_phase updates     │
│    forward → reverse → qualitative  │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 10. For EACH regulatory clause:     │
│                                     │
│   ┌─────────────────────────────┐   │
│   │ Load policy context         │   │
│   │ full manual OR retrieved    │   │
│   │ chunks (planned)            │   │
│   └──────────────┬──────────────┘   │
│                  ▼                  │
│   ┌─────────────────────────────┐   │
│   │ Forward judgment (AI)       │   │
│   │ JUDGMENT_SYSTEM_PROMPT      │   │
│   │ tool: record_judgment       │   │
│   └──────────────┬──────────────┘   │
│                  ▼                  │
│   ┌─────────────────────────────┐   │
│   │ Post-process (code)         │   │
│   │ verify_quotes, retries      │   │
│   └──────────────┬──────────────┘   │
│                  ▼                  │
│   DB: regul_forward_findings        │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 11. Reverse coverage                │
│     A) Extract internal sections    │
│        (EXTRACTION_SYSTEM_PROMPT)   │
│     B) Map each section → clauses   │
│        REVERSE_MAPPING_SYSTEM_PROMPT│
│     Rows with INT prefix in UI      │
│     DB: regul_internal_sections     │
│         regul_reverse_mappings      │
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 12. Qualitative assessment (AI)      │
│     Only if enable_qualitative=true  │
│     QUALITATIVE_ASSESSMENT_*        │
│     one call, full reg + policy     │
│     DB: regul_qualitative_assessments│
└──────────────────┬──────────────────┘
                   ▼
┌─────────────────────────────────────┐
│ 13. Analysis complete               │
│     analysis_runs.status            │
│     All analysis list               │
│     Maker → checker → reviewer      │
│     Gap analysis (adapter planned)  │
└─────────────────────────────────────┘
```

**Important notes from diagram**
- **Setup (steps 2–5)** uses **Landing AI** — same as Analyse V8. We do **not** run Regul.ai `EXTRACTION_SYSTEM_PROMPT` on the regulatory PDF at run time; points already live in `regulation_points` / library snapshots.
- **Internal policy** is parsed once per `file_hash`; text loaded from `landing_ai_parse_cache` at analysis time — no re-upload on Run.
- **Point selection (step 6)** replaces Regul.ai’s “Claude extract + confirm clauses” gate — you pick from pre-extracted points instead of AI-segmenting the PDF again.
- **Qualitative (step 12)** runs only when the user checked **Include qualitative assessment** in step 7.
- There is **no dual-verify second model** in this workflow — one admin-selected LLM per phase (same design as Regul.ai).
- **Run analysis** is one user button after confirm; backend runs judgment → reverse → qualitative (if enabled) in `NdRegulAnalysisProcessor`.
- **Umbrella row** stays in `analysis_runs` so **All analysis** and maker/checker queues are shared with V8 runs.

---

# STEPS (implementation detail)

Every step below includes an **Input** block: what the user or client sends (forms, JSON, files) and what the server loads from DB or sends to the **LLM** (no PDF bytes to the LLM — only parsed **text**).

**Auth on API calls (all steps except login):**
- Header: `Authorization: Bearer <token>` from `POST /nd/auth/login`

**Supported upload file types:** PDF, Word (same as ND regulation/internal upload endpoints)

**Quick reference — where files vs IDs vs text go**

| Step | User sends file bytes? | User sends JSON body? | Text sent to LLM |
|------|------------------------|----------------------|---------------------|
| 1 Entry | No | No | — |
| 2 Regulatory | **Yes** if uploading | Yes if creating doc row | — |
| 3 Extract reg | No | No (POST extract empty) | — (Landing AI, not LLM admin setting) |
| 4 Policy | **Yes** if uploading | Yes | — |
| 5 Parse internal | No | — | — |
| 6 Select points | No | No (browser selection) | — |
| 7 Confirm + run | No | Yes on create (step 8) | — |
| 8 Create + start | No | Yes (run payload) | Loads reg + policy text from cache |
| 9 Progress | No | — | — |
| 10 Judgment | — | — | Policy context + each clause text |
| 11 Reverse | — | — | Internal sections + clause list |
| 12 Qualitative | — | — | Full reg + full policy text |
| 13 Workflow | No | Review / status updates | — |

**Login (before any step):** `POST /nd/auth/login` — credentials → `{ token, profile }`

---

## Step 1 — Open Analysis V3 (analyse-regul)

- **UI:** `/nd/analysis-versions` → **V3 — Regul Workflow** → `/nd/analyse-regul`
- **API:** none
- **Model:** none
- **Prompt:** none

**Input**

| Source | What you send | Sent to server? |
|--------|----------------|-----------------|
| Navigation | Open route as **maker** or **super_admin** | No body |

**Note:** Unlike Regul.ai’s assessment wizard (title, entity, regulator, period), BCP names the run automatically from selected internal + regulation labels when you create the run (Step 8).

---

## Step 2 — Select or upload regulatory document

- **UI:** `/nd/regulation-documents` or picker on `/nd/analyse-regul`
- **API (list):** `GET /nd/regulation-documents`
- **API (upload):** `POST /nd/regulation-documents/upload`
- **Model:** none
- **Prompt:** none

**Input**

**Path A — pick existing document (no file this step)**

| Source | What you send |
|--------|----------------|
| Analyse-regul UI | Select regulation doc or library; points loaded from `regulation_points` |

**Path B — upload regulatory file**

`POST /nd/regulation-documents/upload` — `multipart/form-data`

| Part | Type | Required | Notes |
|------|------|----------|--------|
| `file` | binary | yes | PDF/Word |
| `departmentId` | string | no | Optional UUID |

**What happens on upload**
1. File → Supabase Storage bucket `doc`
2. Rows: `stored_documents`, `regulation_documents`, `extraction_status = pending`
3. User must run **Extract** (Step 3) before points appear

**Save in DB — example**

`regulation_documents`
| field | example |
|-------|---------|
| id | `(uuid)` |
| name | `CBUAE TFS Guidelines` |
| extraction_status | `pending` → `completed` |
| stored_document_id | `(uuid)` |

`regulation_points` (after Step 3)
| field | example |
|-------|---------|
| regulation_document_id | `(uuid)` |
| point_number | `3.2-a` |
| point_text | `The licensed institution shall...` |
| page_hint | `12` |

---

## Step 3 — PARSE + EXTRACT regulatory points (Landing AI)

- **UI:** Extract button on regulation document detail, or background after upload
- **API:** `POST /nd/regulation-documents/{id}/extract`
- **Response:** `202 Accepted` (background)
- **Model:** Landing AI `dpt-2-latest` (parse), `extract-latest` (extract)
- **Prompt:** none — JSON schema is the instruction
- **Schema:** `bcp-api/Schemas/gov-requirement-points.schema.json`

**Input**

| Layer | What is sent |
|-------|----------------|
| **From user** | Document id only — `POST .../extract` empty body |
| **To Landing AI** | PDF bytes from storage; markdown chunks; extract schema |
| **To Regul LLM** | **Nothing** — this step does not use `regul_workflow_llm` |

**Not Regul.ai Step 5:** Regul.ai uses Claude `EXTRACTION_SYSTEM_PROMPT` + `record_clauses`. BCP V3 uses **Landing AI** point extraction (same as V8). See `docs/ND-ANALYSE-V8-WORKFLOW.md` Steps 3–4 for parse/extract detail.

**Save in DB:** `regulation_points`, `landing_ai_parse_cache`, `landing_ai_extract_cache`

---

## Step 4 — Select or upload internal policy document

- **UI:** `/nd/internal-documents` or picker on `/nd/analyse-regul`
- **API (list):** `GET /nd/internal-documents`
- **API (upload):** `POST /nd/internal-documents/upload`
- **Model:** none
- **Prompt:** none

**Input**

**Path A — pick existing**

| Source | What you send |
|--------|----------------|
| Analyse-regul | `selectedInternalDocIds` — one or more UUIDs |

**Path B — upload**

`POST /nd/internal-documents/upload` — `multipart/form-data`, field `file`

**Save in DB — example**

`stored_documents` + `internal_documents` + parse cache keyed by `file_hash`

Policy text is **not** sent to the Regul LLM until **Run analysis** (Steps 10–12).

---

## Step 5 — PARSE internal policy (Landing AI)

- **API:** Automatic on internal doc upload/attach, or parse-on-demand
- **Model:** `dpt-2-latest`
- **Prompt:** none

**Input**

| Layer | What is sent |
|-------|----------------|
| **From DB** | Internal doc file from storage |
| **To Landing AI** | Parse only — **no** point extract on internal docs in setup |

**Save in DB:** `landing_ai_parse_cache` (markdown + page markers per `file_hash`)

---

## Step 6 — Select regulation points on analyse-regul

- **UI:** `/nd/analyse-regul` — regulation panel + point tree / library mode
- **API:** none until Run
- **Model:** none
- **Prompt:** none

**Input**

| Source | What you send | Sent to server? |
|--------|----------------|-----------------|
| Point checkboxes | Selected `point_id` / regulation point UUIDs | **No** — held in browser until Step 8 |

**Replaces Regul.ai Steps 5–6** (Claude clause extract + human clause review confirm). Here you select from **already extracted** `regulation_points` or library snapshots. There is no separate `POST .../confirm-clauses` API.

**Example browser state:**

```json
{
  "selectedPointIds": ["uuid-point-1", "uuid-point-2"],
  "selectedInternalDocIds": ["uuid-internal-1"],
  "selectedRegulationDocIds": ["uuid-reg-1"]
}
```

---

## Step 7 — Confirm dialog + RUN

- **UI:** Modal on `/nd/analyse-regul`
- **API:** none (gates Step 8)
- **Model:** none (displays admin LLM label from `GET /nd/settings/regul-workflow-llm`)
- **Prompt:** none

**Input**

| User action | Effect |
|-------------|--------|
| Read LLM line | e.g. `Anthropic · claude-sonnet-5` from admin setting |
| Checkbox | `enableQualitativeAssessment` → stored as `enable_qualitative` on run |
| Type `start` | Required to proceed |
| Confirm | Triggers Step 8 |

**Qualitative default:** unchecked (skip Phase C unless user opts in).

---

## Step 8 — Create run and start pipeline

- **UI:** Same page after confirm
- **API:** `POST /nd/analysis-runs` then `POST /nd/analysis-runs/{id}/start`
- **Response:** create → `{ id }`; start → `{ success, message }` (background processor)
- **Status:** `draft` → `running` → `completed` / `failed` / `cancelled`
- **Processor:** `NdRegulAnalysisProcessor` when `workflow_engine = regul_pipeline`

**Input**

**8A — Create run**

`POST /nd/analysis-runs` — `application/json` — **requires maker**

```json
{
  "name": "IMPTFS × CBUAE TFS",
  "description": null,
  "libraryId": null,
  "departmentId": null,
  "selectedPointsSnapshot": [
    {
      "pointNumber": "3.2-a",
      "pointText": "The licensed institution shall...",
      "regulationDocumentId": "uuid-reg-doc"
    }
  ],
  "selectedInternalDocIds": ["uuid-internal-doc"],
  "selectedRegulationDocIds": ["uuid-reg-doc"],
  "workflowEngine": "regul_pipeline",
  "enableQualitative": false
}
```

| Field | Meaning |
|-------|---------|
| `workflowEngine` | Must be `regul_pipeline` for this workflow |
| `enableQualitative` | From Step 7 checkbox |
| `selectedPointsSnapshot` | Clauses/points to judge (from Step 6) |

**8B — Start pipeline**

`POST /nd/analysis-runs/{id}/start` — **empty body**

| Server loads | From |
|--------------|------|
| Point list | `analysis_points` + snapshot JSON |
| Regulatory text | `regulation_documents` → parse/extract cache / points |
| Policy text | `internal_documents` → `landing_ai_parse_cache` |
| LLM config | `nd_system_settings.regul_workflow_llm` → snapshot on run |

**Pipeline text inputs (from DB, not from POST body):**

| Document | Source | Used in |
|----------|--------|---------|
| Regulatory clauses | `selectedPointsSnapshot` / `regulation_points` | Forward, reverse context |
| Policy full text | Parsed markdown per internal doc | Forward, reverse, qualitative |

**Save in DB — example**

`analysis_runs`
| field | example |
|-------|---------|
| id | `(uuid)` |
| workflow_engine | `regul_pipeline` |
| enable_qualitative | `false` |
| regul_llm_provider | `anthropic` |
| regul_llm_model | `claude-sonnet-5` |
| regul_pipeline_phase | `forward` → `reverse` → `done` |
| status | `running` → `completed` |

`analysis_points` (shell rows for ND compatibility)
| field | example |
|-------|---------|
| analysis_run_id | `(uuid)` |
| point_snapshot | `{ pointNumber, pointText, ... }` |

---

## Step 9 — Live progress on analyse-regul

- **UI:** `/nd/analyse-regul` — same page polls run status
- **API:** `GET /nd/analysis-runs/{id}` (polling)
- **Model:** none
- **Prompt:** none

**Input:** run id from query `?run=` or after create.

**Progress fields:** `status`, `regul_pipeline_phase`, `regul_pipeline_error`, point counts.

**Note:** Regul.ai uses SSE `GET .../analyze/stream`. BCP ND currently uses **polling** on run detail (SSE planned optional).

---

## Step 10 — Phase A: Forward judgment (per regulatory clause)

- **Service (planned):** `NdRegulAnalysisProcessor.RunForwardPhaseAsync` — port of Regul.ai `judge_clause()` / `_call_judgment()`
- **Service (current):** placeholder — findings marked `skipped`
- **Model:** admin `regul_workflow_llm` model at run time
- **System prompt:** `JUDGMENT_SYSTEM_PROMPT` (Regul.ai — copy exact)
- **User content (two blocks):**
  1. `build_judgment_context_text(policy_context)` — cacheable
  2. `build_judgment_query_text(clause_no, clause_text)`
- **Tool name:** `record_judgment`
- **Schema:** `JUDGMENT_TOOL_SCHEMA`

**Input**

| Layer | What is sent |
|-------|----------------|
| **Per clause (from DB)** | `clause_no`, `clause_text` from snapshot / `regul_forward_findings` |
| **Policy context** | If total policy pages ≤ **50**: full policy markdown. If **>50**: retrieved chunks per clause (planned) |
| **Quote verification (code)** | Concatenated policy source text for `verify_quotes()` after model returns |

**Exact system prompt (judgment / compare) — same as Regul.ai:**

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

**User block 1 example:**

```
--- INTERNAL POLICY DOCUMENT EXCERPTS (retrieved as the sections most likely relevant to a clause -- they may not be the full manual, and if nothing here addresses a given clause it may still be covered elsewhere) ---
=== DOCUMENT: IMPTFS_Manual.pdf ===
{policy markdown text}
--- END EXCERPTS ---
```

**User block 2 example:**

```
REGULATORY CLAUSE 3.2-a:
The licensed institution shall maintain...

Judge this clause against the excerpts above. If the excerpts don't clearly address the clause, prefer a lower confidence and non_compliant/partial design_status rather than assuming coverage that isn't shown.
```

**Schema fields (required):** `design_status`, `operating_status`, `overall_status`, `confidence`, `interpretation`, `policy_extract`, `document_reference`, `gap_description`, `suggested_action`, `gap_direction`

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

**After judgment — clean-up (planned, same as Regul.ai)**
| Code | Why |
|------|-----|
| `verify_quotes()` | Quotes must appear in policy source |
| Retry loop | Empty `gap_description` on partial/non_compliant |
| `needs_review` flags | Low confidence, unverified quotes |

**Save in DB — example**

`regul_forward_findings`
| field | example |
|-------|---------|
| analysis_run_id | `(uuid)` |
| clause_no | `3.2-a` |
| clause_text | `...` |
| status | `completed` |
| result_json | `(tool output above)` |

---

## Step 11 — Phase B: Reverse coverage

### 11A — Extract internal sections

- **Service (planned):** port `extract_internal_sections()` — reuses Regul `extract_clauses()` on policy text
- **Model:** admin `regul_workflow_llm`
- **Prompts:** same **`EXTRACTION_SYSTEM_PROMPT`** + `build_extraction_prompt()` on **internal** text
- **Chunk size:** **15** pages (`INTERNAL_SECTION_PAGE_CHUNK_SIZE` in Regul.ai)

**Input (11A)**

| Layer | What is sent |
|-------|----------------|
| **From DB** | Internal doc markdown from `landing_ai_parse_cache` |
| **To LLM** | Internal policy text chunks; tool `record_clauses` → `InternalSection` |

### 11B — Map each internal section

- **Service (planned):** `reverse_map_section()`
- **Model:** admin `regul_workflow_llm`
- **System prompt:** `REVERSE_MAPPING_SYSTEM_PROMPT`
- **Tool:** `record_mapping`

**Exact system prompt (reverse mapping) — same as Regul.ai:**

```
You are a compliance analyst performing reverse-coverage analysis: given a section of a bank's internal policy manual and the full list of extracted regulatory requirement clauses, determine which regulatory clause(s) (if any) this internal section implements.

Rules:
- mapping: "covered" if this section clearly implements one or more of the listed regulatory clauses (list them in mapped_clause_nos). "no_regulatory_basis" if this is legitimate operational content (e.g. definitions, contact lists, system configuration, internal escalation paths) that simply isn't required by any of the listed clauses -- this is NORMAL, not a defect; internal policies routinely contain more detail than the regulation demands. "basis_not_verifiable" if the section's own text claims or implies it addresses a specific regulatory requirement, but none of the listed clauses actually match that claim.
- mapped_clause_nos: for "covered", the clause_no values (exactly as given below) this section implements. For "no_regulatory_basis" or "basis_not_verifiable", this is normally empty -- BUT if you can identify a SPECIFIC regulatory clause this section relates to or conflicts with (e.g. the section states a numeric threshold, timeline, or rule that differs from one stated in a particular listed clause), include that clause_no here even though the section doesn't "implement" it. This is what lets a human reviewer see exactly which regulatory requirement is at odds with this internal content -- e.g. a section claiming a 25% ownership threshold when clause 3.4-1 states 50% should list "3.4-1" here even though mapping is "basis_not_verifiable" or contradicts_regulation is true.
- confidence: your calibrated confidence (0-1) in this mapping decision.
- contradicts_regulation: true ONLY if this section actively conflicts with a regulatory requirement -- e.g. it permits something the regulation prohibits, states a threshold or timeline that contradicts one in the listed clauses, or otherwise directs staff to do something the regulation forbids. This is a stronger claim than "not required by the regulation" -- ordinary no_regulatory_basis/basis_not_verifiable content is NOT a contradiction, just unrelated or unconfirmed. Always false when mapping is "covered".
- commentary: one or two sentences explaining the decision -- if contradicts_regulation is true, state exactly what conflicts with what.
```

**Example tool output:**

```json
{
  "mapped_clause_nos": ["3.4-1"],
  "mapping": "basis_not_verifiable",
  "commentary": "Section states 25% ownership threshold; clause 3.4-1 requires 50%.",
  "confidence": 0.88,
  "contradicts_regulation": true
}
```

**Sections that become gap rows:** `no_regulatory_basis` or `basis_not_verifiable` → synthetic **`INT x.x`** rows (planned in gap UI).

**Save in DB:** `regul_internal_sections`, `regul_reverse_mappings`

---

## Step 12 — Phase C: Qualitative assessment

- **When:** only if `enable_qualitative = true` on `analysis_runs`
- **Service (planned):** `run_qualitative_assessment()`
- **Model:** admin `regul_workflow_llm`
- **System prompt:** `QUALITATIVE_ASSESSMENT_SYSTEM_PROMPT`
- **Tool name:** `record_assessment`

**Exact system prompt — same as Regul.ai:**

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

**User message shape:**

```
--- REGULATORY DOCUMENT ---
{full regulatory text from selected docs/points context}
--- END REGULATORY DOCUMENT ---

--- INTERNAL POLICY DOCUMENT ---
{full policy markdown}
--- END INTERNAL POLICY DOCUMENT ---

Assess the internal policy document per the rubric. Cover all of: clarity_and_tone, structure_and_navigation, depth_of_implementation_detail, alignment_with_regulatory_language, actionability_for_staff.
```

**Save in DB — example**

`regul_qualitative_assessments`
| field | example |
|-------|---------|
| analysis_run_id | `(uuid)` |
| status | `completed` |
| result_json | `overall_rating`, `dimensions`, `strengths`, `improvement_recommendations` |

---

## Step 13 — Results + downstream workflow

- **UI:** `/nd/analysis-runs` (All analysis), `/nd/gap-analysis`, checker/reviewer panels
- **Status flow:** same ND workflow as V8 (`draft` → `running` → `completed` → submitted to checker → …)
- **Export:** ND Excel/PDF export (V8 paths; Regul-specific export planned)
- **Model:** none on human review steps

**Input**

| Action | Request |
|--------|---------|
| List runs | `GET /nd/analysis-runs` |
| Run detail | `GET /nd/analysis-runs/{id}` |
| Submit to checker | ND review APIs (same as V8) |
| Gap view | `/nd/gap-analysis?run={id}` |

Regul V3 runs appear in the **same** All analysis list as V8 (`workflow_engine` distinguishes engine in API).

---

# Extra APIs

| Action | API |
|--------|-----|
| List analysis runs | `GET /nd/analysis-runs` |
| Get run | `GET /nd/analysis-runs/{id}` |
| Create run | `POST /nd/analysis-runs` |
| Start pipeline | `POST /nd/analysis-runs/{id}/start` |
| Stop run | `POST /nd/analysis-runs/{id}/stop` |
| Active Regul LLM | `GET /nd/settings/regul-workflow-llm` |
| Admin Regul LLM | `GET/PUT /nd/admin/settings/regul-workflow-llm` |
| V8 dual verify LLM | `GET/PUT /nd/admin/settings/dual-verify-llm` |
| Auth | `POST /nd/auth/login` |

---

# DB tables

| Table | Role |
|-------|------|
| `analysis_runs` | Umbrella run (V8 + Regul V3); `workflow_engine`, `enable_qualitative`, `regul_pipeline_*` |
| `analysis_points` | Shell point rows per run (V8 fills `landing_ai_*`; Regul uses `regul_*` for AI results) |
| `regulation_documents` | Regulatory doc catalog |
| `regulation_points` | Landing AI extracted points (setup) |
| `internal_documents` | Internal policy catalog |
| `stored_documents` | File metadata + storage paths |
| `landing_ai_parse_cache` | Parsed markdown per `file_hash` |
| `regul_forward_findings` | Forward judgment per clause (Regul V3 only) |
| `regul_internal_sections` | Internal sections (reverse A) |
| `regul_reverse_mappings` | Section → clause mapping (reverse B) |
| `regul_qualitative_assessments` | One qualitative summary per run (optional) |
| `nd_system_settings` | `regul_workflow_llm`, `dual_verify_llm` |
| `profiles` / ND review tables | Same maker/checker/reviewer as V8 |

### Database migration (SQL)

**Do you need to run SQL manually?**

| Case | Action |
|------|--------|
| API restarted on Postgres/Supabase after this branch | **Usually no** — `SupabaseSchemaBootstrap` applies patches on startup |
| Bootstrap failed or hosted Supabase without restart | **Yes** — run once: `bcp-api/scripts/supabase/009_regul_workflow.sql` |

**Verify (optional):**

```sql
SELECT column_name FROM information_schema.columns
WHERE table_name = 'analysis_runs'
  AND column_name IN ('workflow_engine','enable_qualitative','regul_llm_provider',
                      'regul_llm_model','regul_pipeline_phase','regul_pipeline_error');

SELECT table_name FROM information_schema.tables
WHERE table_schema = 'public' AND table_name LIKE 'regul_%' ORDER BY table_name;
```

---

# Code map

| Piece | File |
|-------|------|
| Regul pipeline processor | `bcp-api/Services/NewDashboard/NdRegulAnalysisProcessor.cs` |
| Workflow engine constant | `bcp-api/Services/NewDashboard/AnalysisWorkflowEngine.cs` |
| Regul LLM settings | `bcp-api/Services/Llm/RegulWorkflowLlmSettingsService.cs` |
| Run create/start | `bcp-api/Controllers/NewDashboard/AnalysisRunsController.cs` |
| Regul entities | `bcp-api/Data/NewDashboard/RegulWorkflowEntities.cs` |
| Analyse Regul UI | `bcp-web/src/app/pages/analyse-regul/` |
| Analysis versions list | `bcp-web/src/app/pages/nd/analysis-versions/` |
| Admin LLM UI | `bcp-web/src/app/pages/nd/admin/nd-admin-settings.component.*` |
| Regul.ai prompts (source) | `Regul.ai/app/backend/llm/prompts.py` |
| Regul.ai schemas (source) | `Regul.ai/app/backend/llm/schemas.py` |
| V8 setup reference | `docs/ND-ANALYSE-V8-WORKFLOW.md` |

---

# Analysis run status

| status | Meaning |
|--------|---------|
| `draft` | Run created, not started |
| `running` | `NdRegulAnalysisProcessor` in progress |
| `completed` | Pipeline finished (`regul_pipeline_phase = done`) |
| `failed` | `regul_pipeline_error` set |
| `cancelled` | User stopped run |
| (+ ND review statuses) | Same submit/checker/reviewer fields as V8 |

---

# What each step is for

| Step | Name | Purpose |
|------|------|---------|
| **1** | Open Analysis V3 | Entry point for Regul workflow (not V8). |
| **2** | Regulatory document | Choose or upload the **rulebook**; Landing AI extract → `regulation_points`. |
| **3** | Extract regulatory points | Landing AI parse+extract (**not** Regul.ai Claude extract). |
| **4** | Internal policy | Choose or upload bank **policy manual**. |
| **5** | Parse internal | Landing AI parse → markdown cache. |
| **6** | Select points | Pick which regulation points to analyze (replaces Regul clause extract+confirm). |
| **7** | Confirm + RUN | Type `start`; optional qualitative checkbox; shows LLM. |
| **8** | Create + start | `analysis_runs` row + background `NdRegulAnalysisProcessor`. |
| **9** | Live progress | Poll run phase on same page. |
| **10** | Forward judgment | Each clause vs policy → compliant / partial / non_compliant. |
| **11** | Reverse coverage | Internal sections vs rule list → `INT …` rows. |
| **12** | Qualitative | Optional holistic policy writing score. |
| **13** | Results & finish | All analysis list → maker/checker/reviewer (same as V8). |

**Short path:** Landing setup (like V8) → pick points → confirm → Regul.ai compare pipeline (admin LLM) → humans review in ND workflow.

---

# Example flow (tiny fake bank)

### Documents

**Regulation (selected points):**

| Clause | Text |
|--------|------|
| **1.1** | Review the sanctions list at least **once a year**. |
| **2.1** | Beneficial ownership threshold is **50%**. |
| **3.1** | Keep a written escalation path to Compliance. |

**Internal policy (handbook):**

| Section | Text |
|---------|------|
| **A** | We review sanctions lists **annually**. |
| **B** | Beneficial ownership threshold is **25%**. |
| **C** | For IT outages, call the weekend helpdesk. |

---

## Phase 0 — Before analysis

1. Upload + extract regulation (Landing AI) → points in library.
2. Upload + parse internal policy.
3. On `/nd/analyse-regul`: select points 1.1, 2.1, 3.1 + internal doc.
4. Run → confirm → optional qualitative off → type `start`.

---

## Phase 1 — Forward judgment

**Clause 1.1** → section A → **Compliant**  
**Clause 2.1** → section B (25%) → **Non-compliant**  
**Clause 3.1** → no Compliance escalation → **Non-compliant**

---

## Phase 2 — Reverse coverage

**Section A** → maps to 1.1 → covered (no new row)  
**Section B** → conflicts with 2.1 → **`INT B`**  
**Section C** → no rule → **`INT C`** (no regulatory basis)

---

## Phase 3 — Qualitative (if enabled)

One summary card — not extra table rows.

---

## What you see at the end

| Row / UI | From |
|----------|------|
| 1.1 Compliant | Forward |
| 2.1 Non-compliant | Forward |
| 3.1 Non-compliant | Forward |
| INT B | Reverse |
| INT C | Reverse |
| Qualitative card | Step 12 (if enabled) |
| All analysis list | `analysis_runs` umbrella |

---

## Flow diagram for this example

```
Select points: 1.1, 2.1, 3.1 + internal doc
        │
        ▼
FORWARD (admin LLM, JUDGMENT_SYSTEM_PROMPT)
  1.1 → OK
  2.1 → FAIL (25% in B)
  3.1 → FAIL (no Compliance escalation)
        │
        ▼
REVERSE
  A → 1.1 covered
  B → conflicts 2.1 → INT row
  C → no rule → INT row
        │
        ├──► regul_forward_findings + reverse mappings
        │
QUALITATIVE (optional) ──► regul_qualitative_assessments
        │
        ▼
All analysis → maker / checker / reviewer
```

---

**Remember:**  
Forward = “Is **each rule** in the handbook?”  
Reverse = “What about **each handbook section** — match / miss / conflict?”  
Setup = **V8 Landing AI**; analysis = **Regul.ai prompts** with **admin-selected LLM**.
