# ND Analyse V8 — Backend Execution Flow (Technical)

Simple map of **what the API does** from upload → final result.  
**Scope:** `/nd/analyse-v8` + `/nd/*` APIs (not legacy `/old/*` or `dual-verify-kafka`).

---

## Stack (one line each)

| Layer | What |
|-------|------|
| **Web** | Angular — polls run status every **2s** |
| **API** | ASP.NET — controllers under `/nd/*` |
| **Processor** | `NdAnalysisProcessor` — background job per run |
| **AI** | Landing AI (Phase 1) + Gemini (Phase 2) |
| **Storage** | Supabase bucket (PDFs) |
| **DB** | PostgreSQL — runs, points, plans, reviews |

---

## End-to-end (boxes)

```
┌──────────────┐   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
│ 1. UPLOAD    │   │ 2. CREATE    │   │ 3. PROCESS   │   │ 4. RESULTS   │
│    DOCS      │──►│    RUN       │──►│    (AI)      │──►│    + REVIEW  │
└──────────────┘   └──────────────┘   └──────────────┘   └──────────────┘
```

---

## 1. Document upload (before analysis)

### Regulation PDF

```
POST /nd/regulation-documents/upload
  │
  ├─► Save PDF → Supabase (`regulations/nd/...`)
  ├─► Row → stored_documents + regulation_documents
  ├─► Landing AI extract (NdRegulationUploadService)
  └─► Rows → regulation_points (+ extraction_result markdown)
```

- Re-extract: `POST /nd/regulation-documents/{id}/extract`
- Status on doc: `pending` → `processing` → `extracted` | `failed`

### Internal (policy) PDF

```
POST /nd/internal-documents/upload
  │
  ├─► Save PDF → Supabase (`documents/nd/...`)
  └─► Row → stored_documents (used at analysis time, not extracted to points)
```

### Library (optional)

```
POST /nd/libraries  +  library_points
  │
  └─► Snapshots of regulation points (no AI — points already in DB)
```

---

## 2. Create & start analysis run

### Web (`/nd/analyse-v8` → Run)

```
User selects: internal doc + regulation doc(s) or library + points
  │
  ├─► POST /nd/analysis-runs
  │     • analysis_runs  (status = draft)
  │     • analysis_points (one row per selected point, point_snapshot JSON)
  │
  └─► POST /nd/analysis-runs/{id}/start
        • Returns immediately
        • NdAnalysisProcessor.ProcessRunAsync() runs in background Task
```

### Poll while running

```
GET /nd/analysis-runs/{id}/status   (every 2s from web)
  │
  └─► status, processedPointsCount, totalPointsCount, per-point fields
```

---

## 3. Per-point pipeline (`NdAnalysisProcessor`)

**Important:** The processor loops **one regulation point at a time**. It does **not** send all gov points in one AI call.

```
For each analysis_points row:
  1 × gov requirement (from point_snapshot JSON)
  1 × internal policy PDF (same file for whole run, parsed once)
```

---

### 3.0 What is NOT sent

| Not sent to compare AI | Why |
|------------------------|-----|
| Full regulation PDF markdown (all points) | Gov text is already extracted → stored in `point_snapshot` per point |
| All points in one prompt | Each point = separate Landing AI + Gemini call |
| Library object | Only the selected point snapshots are copied into the run |

Regulation PDF extraction (`/nd/regulation-documents/upload`) is a **separate step** that fills `regulation_points`. Analysis uses the **text of the one point** you selected.

---

### 3.1 Phase 1 — Landing AI (always first)

**Service:** `LandingAiCompareService.ComparePointAsync()`

#### Step A — Internal PDF → markdown (once per file, cached)

```
Internal PDF bytes (from Supabase / stored_documents)
  │
  ├─► Hash file → check landing_ai_parse_cache
  ├─► If miss → Landing AI ParseDocumentAsync (whole PDF → markdown)
  └─► Cache full internal markdown by file hash
```

#### Step B — Build prompt for **this one point**

**Builder:** `LandingAiComparePromptBuilder.Build()`

```
┌─────────────────────────────────────────────────────────────┐
│ SENT TO LANDING AI (as markdown text)                        │
├─────────────────────────────────────────────────────────────┤
│ • Auditor system rules (CBUAE/TFS, semantic match, etc.)     │
│ • FULL internal policy markdown (entire parsed PDF)          │
│ • ONE requirement block:                                     │
│     - point number + title + full requirement text           │
│     (from analysis_points.point_snapshot)                    │
└─────────────────────────────────────────────────────────────┘
```

**API call:** `ExtractComparisonAsync(markdown)` → structured JSON → formatted text saved as:

```
landing_ai_result = { "message": "<formatted text block>" }
landing_ai_status = compliant | partial_compliant | non_compliant | failed
landing_ai_action_plan = CAP text from message
```

**Output format** (text fields inside message): `Output/Response`, `Comply Yes/No (Status)`, `Compliance Confidence %`, `Corrective Action Plan`, `Responsibility`.

#### If Phase 1 fails

```
landing_ai_status = failed
dual_verify_status = skipped     ← Gemini is NOT called
→ move to next point
```

User must **Rerun point** to retry Landing AI.

---

### 3.2 Phase 2 — Gemini dual verify (auto after Phase 1 success)

**On first Run:** If Phase 1 status is compliant / partial / non_compliant → Phase 2 runs **automatically** for that point (not user-optional on initial run).

**Service:** `DualVerifyPromptBuilder.Build()` + `GeminiService.AnalyzeWithPdfAsync()`

#### What is sent to Google Gemini

```
┌─────────────────────────────────────────────────────────────┐
│ TEXT PROMPT (DualVerifyPromptBuilder)                        │
├─────────────────────────────────────────────────────────────┤
│ • "PASS 2 — INDEPENDENT verifier" instructions             │
│ • LANDING AI PASS 1 message (full Phase 1 output text)       │
│ • ONE requirement point (number, title, body text)           │
│ • (No internal markdown in ND path — legacy jobs may add it) │
├─────────────────────────────────────────────────────────────┤
│ PDF ATTACHMENT                                              │
│ • Same internal policy PDF (binary, base64 inline_data)      │
└─────────────────────────────────────────────────────────────┘
```

Gemini re-reads the **PDF** and produces its own assessment. Backend compares Pass 1 vs Pass 2 with `DualVerifyAgreementService` → `agreement` JSON (aligned / status_mismatch / confidence_gap / …).

**Saved:**

```
google_ai_result = { "message": "<phase2 text>", "agreement": { ... } }
dual_verify_status = passed | failed
final_status       = from agreement rules
final_action_plan  = from Phase 1 CAP (initial)
```

#### If Phase 2 fails

```
google_ai_status = failed
dual_verify_status = failed
landing_ai_result is KEPT          ← Phase 1 not lost
→ user can "Rerun dual verify only" (Phase 2 only)
```

---

### 3.3 Flow diagram (correct rerun rules)

```
                    START (per point)
                         │
                         ▼
              ┌──────────────────────┐
              │  PHASE 1 Landing AI │
              │  1 gov point +       │
              │  full internal MD    │
              └──────────┬───────────┘
                         │
            ┌────────────┴────────────┐
            ▼                         ▼
      [ failed ]              [ compliant / partial / non ]
            │                         │
            │                         ▼
            │               ┌──────────────────────┐
            │               │  PHASE 2 Gemini      │
            │               │  prompt + PDF +      │
            │               │  Phase 1 message     │
            │               └──────────┬───────────┘
            │                         │
            ▼                         ▼
   dual_verify = skipped      passed or failed
   User: Rerun POINT          User: Rerun DV only (optional)
   → Phase 1 + 2 again       → Phase 2 only
```

---

### 3.4 Rerun buttons (what actually runs)

| User action | API | Landing AI | Gemini |
|-------------|-----|------------|--------|
| **Run analysis** (first time) | `POST .../start` | ✅ each pending/failed point | ✅ after each Phase 1 success |
| **Rerun this point** | `POST .../rerun-point/{id}` | ✅ full again | ✅ full again |
| **Rerun dual verify** | `POST .../rerun-dual-verify/{id}` | ❌ skipped | ✅ only |
| **Rerun all failed DV** | `POST .../rerun-dual-verify/all` | ❌ skipped | ✅ only for DV-failed points |

Resume run (`/start` on incomplete run): points with Landing success but `dual_verify_status = pending` get **Phase 2 only** (Landing already done).

---

### 3.5 Run-level status (after all points)

| DB `analysis_runs.status` | When |
|---------------------------|------|
| `running` | Processor started |
| `landing_ai_complete` | Some Phase 1 done, not all points finished |
| `completed` | All points done, no DV failures |
| `dual_verify_failed` | All processed, ≥1 DV failed |

---

## 3.6 Prompt source files (code)

| Phase | Prompt builder | File |
|-------|----------------|------|
| Landing AI | `LandingAiComparePromptBuilder` | `bcp-api/Services/LandingAi/LandingAiComparePromptBuilder.cs` |
| Gemini Pass 2 | `DualVerifyPromptBuilder` | `bcp-api/Services/GovPointsService.cs` |
| Agreement math | `DualVerifyAgreementService` | `bcp-api/Services/DualVerifyAgreementService.cs` |
| Orchestration | `NdAnalysisProcessor` | `bcp-api/Services/NewDashboard/NdAnalysisProcessor.cs` |

---

## 4. Read results

```
GET /nd/results/{runId}
  │
  ├─► run summary + all analysis_points (AI JSON fields)
  ├─► analysis_reviews, analysis_point_comments
  ├─► action_plan_item_reviews (checker per-action)
  └─► analysis_status_history
```

Gap analysis UI uses same data + maps points for display.

---

## 5. Maker edits action plan

```
PUT /nd/results/{runId}/action-plan/{pointId}
  Body: { content }
  │
  ├─► analysis_points.final_action_plan = content
  └─► action_plan_history new row (maker_edit), version++
```

- History: `GET .../action-plan-history/{pointId}`
- Blocked when run status is `submitted_for_review` | `checker_approved` | `reviewer_approved`
- Allowed again when `pulled_back`

---

## 6. Review workflow (API only)

```
POST /nd/analysis-runs/{id}/submit-for-review
  └─► status → submitted_for_review

POST /nd/checker/review/{runId}/approve
  └─► status → checker_approved
      + analysis_reviews, optional comments / action item reviews

POST /nd/checker/review/{runId}/pull-back
  └─► status → pulled_back  (comments optional)

POST /nd/reviewer/review/{runId}/finalize
  └─► status → reviewer_approved

POST /nd/reviewer/review/{runId}/pull-back
  └─► status → submitted_for_review
```

---

## Main DB tables

```
regulation_documents ──► regulation_points
stored_documents       (internal PDFs)
libraries ──► library_points

analysis_runs ──► analysis_points
                    ├── landing_ai_* / google_ai_* / final_status
                    ├── final_action_plan / original_ai_action_plan
                    └── action_plan_history

analysis_reviews
analysis_point_comments
action_plan_item_reviews
analysis_status_history
```

---

## API cheat sheet (ND v8 path)

| Step | Method | Path |
|------|--------|------|
| Upload regulation | POST | `/nd/regulation-documents/upload` |
| Upload internal | POST | `/nd/internal-documents/upload` |
| Create run | POST | `/nd/analysis-runs` |
| Start run | POST | `/nd/analysis-runs/{id}/start` |
| Poll status | GET | `/nd/analysis-runs/{id}/status` |
| Get results | GET | `/nd/results/{runId}` |
| Edit action plan | PUT | `/nd/results/{runId}/action-plan/{pointId}` |
| Submit review | POST | `/nd/analysis-runs/{id}/submit-for-review` |
| Checker approve | POST | `/nd/checker/review/{runId}/approve` |
| Checker pull back | POST | `/nd/checker/review/{runId}/pull-back` |

---

## Legacy note

| Route | Backend |
|-------|---------|
| `/nd/analyse-v8` Run | **`/nd/analysis-runs`** + `NdAnalysisProcessor` |
| `/old/analyse-v8` Run | **`/dual-verify-kafka/jobs`** (unchanged) |

---

*Product overview: [WORKFLOW.md](./WORKFLOW.md) · Full spec: [ND_WORKFLOW.md](./ND_WORKFLOW.md)*
