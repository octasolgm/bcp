# Regul forward matching — problem, fix plan, and runbook

**Context:** CBUAE §8.4 (Independent Audit Function) marked **Non-Compliant** even though the internal AML Manual covers it under **Internal Audit AML Rule 9.4.1** with different wording (“internal audit” vs regulator “independent audit”). Same pattern affects other points where policy is covered under different section numbers or terminology.

**Example docs:** `CBUAE_EN_3945_VER2.pdf` (regulation) · `Internal AML Manual 290626.pdf` (internal policy)

---

## 1. What was the problem?

### Symptom (what you saw)

| Signal | Meaning |
|--------|---------|
| Policy extract is a generic intro mentioning “internal and external auditors” | Forward pass did **not** retrieve Rule **9.4.1** |
| Reference `section 14.4, p.1` | Wrong section/page — bad parse/OCR or wrong retrieval chunk |
| Confidence ≤ 69% | Model uncertain; post-processor may downgrade unverified quotes |
| Long gap action (“add a dedicated Independent Audit section…”) | Model restated **regulatory requirements** as missing because excerpts did not show internal policy |

The gap text describes what **§8.4 asks for**, not what the manual **lacks** — because **9.4.1 was not in the context** the model judged against.

### Root causes (pipeline)

| # | Cause | Where in code |
|---|--------|----------------|
| **R1** | **Keyword retrieval misses synonyms** — regulation says “independent audit”; manual says “Internal Audit Rule 9.4.1”. For manuals **> 50 pages**, only top **12** keyword-matched chunks are sent, not the full manual. | `NdRegulPolicyContextService.cs` |
| **R2** | **User block 2 biases false negatives** — instructs model to prefer `non_compliant` when excerpts are thin. | `NdRegulPromptDefaults.cs` → DB prompt `regul_judgment_user_query` |
| **R3** | **Element-level checking amplifies misses** — §8.4 has many sub-elements; if 9.4.1 is missing from excerpts, model lists all elements missing. | System prompt `regul_judgment_system` |
| **R4** | **Parse / section extraction quality** — spaced filename (`A M L M a n u a l`), wrong section refs. | Landing AI parse + `policy-clauses` extract |
| **R5** | **Forward-only run** — reverse mapping (internal section → regulatory clause) never runs to reconcile coverage. | `NdRegulAnalysisProcessor.cs` (forward-only path) |
| **R6** | **Old V3 compare had full markdown + semantic rules** — Dual Verify V3 attached **entire** internal PDF and said “search every document / different wording OK”. Regul forward uses excerpts + stricter query. | `LandingAiComparePromptBuilder.PromptTemplateV3` vs Regul forward |

### What is NOT the problem

- The Regul workflow LLM itself is not necessarily wrong — it was given **incomplete context** and **instructions that favor non-compliant** when context is thin.
- A real gap may still exist if 9.4.1 only says “internal audit exists” but omits frequency factors, subsidiary scope, or minimum audit scope — that would be **partial**, not a retrieval bug.

---

## 2. Fix plan (phases)

### Phase 0 — Baseline (do first)

**Goal:** Confirm human ground truth vs system output.

| Step | Action |
|------|--------|
| 0.1 | Internal documents → open manual → **Sections** → search `9.4.1`, `Internal Audit` |
| 0.2 | Read 9.4.1 text vs §8.4 requirements → note **compliant / partial / non-compliant** (human) |
| 0.3 | Note current prompt versions: Admin → **Analysis prompts** → which version is **Current** for System, User block 1, User block 2 |
| 0.4 | Run analyse-regul on **§8.4 only** (forward) → save run ID, screenshot, status |

**Gate:** If 9.4.1 missing in Sections → do **Phase 2** before prompts. If 9.4.1 exists and covers §8.4 → **Phase 1 + 3**.

---

### Phase 1 — Prompt fixes (no deploy; Admin UI)

**Goal:** Align Regul forward prompts with old Dual Verify V3 semantic rules; remove false-negative bias.

| Block | Key | Change |
|-------|-----|--------|
| **User block 2** | `regul_judgment_user_query` | **Remove** “prefer non_compliant/partial when excerpts don’t clearly address”. **Add** semantic intent, search all excerpts, equivalent wording OK, thin excerpts → low confidence not auto non-compliant. |
| **System** | `regul_judgment_system` | **Add** semantic matching + **independent ↔ internal audit** synonym rule + soften element-level (operational equivalents under different headings count as covered). |
| **User block 1** | `regul_judgment_user_context` | **Add** reminder: before non_compliant, check audit/governance sections under different names (Rule 9.x, Internal Audit). Keep `{policy_context}`. |

**How:** Admin → **Analysis prompts** → edit each block → **Save as new version** → **Set as current**.

**Acceptance:** Re-run §8.4 — status moves toward compliant/partial; extract should reference audit substance, not intro boilerplate.

---

### Phase 2 — Document parse & section extraction

**Goal:** Rule 9.4.1 exists as a clean, retrievable section with correct page.

| Step | Action |
|------|--------|
| 2.1 | `/nd/internal-documents` → select manual → **Run parse** (re-parse PDF) |
| 2.2 | **Extract sections** (policy-clauses) |
| 2.3 | Sections panel: confirm row for **9.4.1** / Internal Audit with full text, sensible `source_page` |
| 2.4 | Regulation doc: confirm §8.4 extraction text is correct (`/nd/regulation-documents`) |

**Acceptance:** Sections list contains 9.4.1; page is not `p.1` for substantive audit content.

---

### Phase 3 — Retrieval code (deploy API)

**Goal:** Forward pass includes 9.4.1 when judging §8.4 on large manuals.

| Change | File |
|--------|------|
| Synonym expansion for ranking (`independent audit` → `internal audit`, `audit function`, `AML audit`, `testing programme`, etc.) | `NdRegulPolicyContextService.cs` |
| Boost chunks whose `section_ref` / label matches audit patterns | same |
| Increase `RetrievalTopChunks` (12 → 20+) for dense clauses | same |
| Optional: raise `FullManualMaxPages` (50 → 80) or run flag | same + analyse-regul UI later |
| Unit tests for §8.4-like clause retrieving audit section | `NdRegulPolicyContextServiceTests.cs` |
| Log retrieved chunk labels per clause in forward phase | `NdRegulAnalysisProcessor.cs` |

**Acceptance:** API log shows §8.4 forward context includes 9.4.1 chunk text.

---

### Phase 4 — Reverse reconciliation

**Goal:** Catch forward misses; surface internal sections that implement regulatory clauses.

| Step | Action |
|------|--------|
| 4.1 | Run **full** Regul pipeline (forward + reverse), not forward-only |
| 4.2 | Verify reverse maps 9.4.1 section → clause `8.4` as `covered` |
| 4.3 | (Future) UI/API: flag when forward = non_compliant but reverse maps `covered` for same clause |

**Acceptance:** Reverse mapping for 9.4.1 shows §8.4 in `mapped_clause_nos`.

---

### Phase 5 — Post-processor & UI

**Goal:** No misleading refs or confident gaps when evidence is weak.

| Change | File |
|--------|------|
| `needs_review` when confidence < 70% and non_compliant | `NdRegulJudgmentPostProcessor.cs` |
| UI badge: “Possible coverage elsewhere — verify manually” | `analyse-regul` component |
| Stricter grounded reference when quote is generic | `ApplyGroundedDocumentReference` |

---

### Phase 6 — Golden regression set

Maintain 5–10 clause pairs (reg section → expected internal section → expected status). Re-run after each phase.

| Reg clause | Expected internal | Notes |
|------------|-------------------|--------|
| §8.4 | Rule 9.4.1 | independent vs internal audit |
| §8.5 | (map after review) | |
| §10 | (map after review) | |

---

## 3. Priority order

| Priority | Phase | Effort | Impact |
|----------|-------|--------|--------|
| 1 | **0** Baseline | ½ day | Know if extraction or retrieval/prompt |
| 2 | **2** Re-parse / re-extract | ½ day | Fixes wrong refs & missing chunks |
| 3 | **1** Prompts (esp. User block 2) | ½ day | Immediate false-negative reduction |
| 4 | **3** Retrieval code | 2–3 days | Sustainable fix for large manuals |
| 5 | **4** Full pipeline + reverse | 1 day | Reconciliation |
| 6 | **5** UI / post-processor | 1 day | Safer UX |
| 7 | **6** Golden set | ongoing | Regression guard |

---

## 4. What to run

### 4.1 Start local services

```powershell
# API (port 5100)
cd "c:\Users\Pc\Documents\GitHub\bcp new\bcp-api"
.\scripts\restart-api.ps1 -Detached

# Web (port 3002)
cd "c:\Users\Pc\Documents\GitHub\bcp new\bcp-web"
npm start
```

Open: **http://localhost:3002**

---

### 4.2 Document library (before analysis)

| Step | URL / action |
|------|----------------|
| Upload / select regulation | `/nd/regulation-documents` → upload CBUAE PDF → **Run extraction** |
| Upload / select internal manual | `/nd/internal-documents` → upload manual → **Run parse** → **Extract sections** |
| Verify 9.4.1 | Internal doc → **Sections** panel → search `9.4.1` |

---

### 4.3 Prompt updates (Phase 1)

| Step | URL / action |
|------|----------------|
| Open prompts admin | `/nd/admin/prompts` (super_admin) |
| Edit **User block 2** | Remove false-negative sentence; add semantic rules → Save v2 → **Set as current** |
| Edit **System** | Add synonym + semantic blocks → Save v2 → **Set as current** |
| Edit **User block 1** (optional) | Add audit-section reminder → Save v2 → **Set as current** |

No API restart required — next analysis run loads current DB versions.

---

### 4.4 Analysis runs

| What | Where | Notes |
|------|--------|------|
| **Quick test (forward only)** | `/nd/analyse-regul` | Select **only §8.4** + internal manual → confirm clauses → **Run forward only** |
| **Full test (recommended)** | `/nd/analyse-regul` | Same selection → **Run** (forward + reverse + qualitative if enabled) |
| **Re-run one point** | analyse-regul → point actions | After prompt/retrieval fix, re-run §8.4 without new run |

**Prerequisites for run:**
- Regulation points extracted and §8.4 selected
- Internal doc parsed + sections extracted
- `regul_clauses_confirmed_at` set (confirm clauses in UI)
- Regul workflow LLM configured: Admin → **System settings** → Regul workflow LLM (API keys in appsettings)

---

### 4.5 Verify results

| Check | How |
|-------|-----|
| Prompt versions used | API console log: `Regul forward phase using admin prompt versions ... regul_judgment_system=vX ...` |
| Retrieval mode | Log: `retrieval=True/False`, `policyPages=N` |
| §8.4 status | analyse-regul gap list / point detail |
| Policy extract | Should quote **9.4.1** audit text, not intro |
| Document reference | Should cite **9.4.1** section/page, not `14.4 p.1` |
| Reverse (if full run) | Reverse progress → mapping for 9.4.1 section → `8.4` |

**API status poll (optional):**

```http
GET http://localhost:5100/nd/analysis-runs/{runId}/status?resume=true
```

---

### 4.6 After Phase 3 code changes

```powershell
cd "c:\Users\Pc\Documents\GitHub\bcp new\bcp-api"
dotnet build Bcp.Api.csproj -v q
.\scripts\restart-api.ps1 -Detached

cd "c:\Users\Pc\Documents\GitHub\bcp new\bcp-api\tests\Reguliq.Api.Tests"
dotnet test --filter "NdRegulPolicyContextService"
```

Then repeat **§8.4 forward run** (section 4.4).

---

## 5. Success criteria

| Metric | Target |
|--------|--------|
| §8.4 policy extract | Verbatim text from **Rule 9.4.1** (or correct audit section) |
| §8.4 status | **Compliant or partial** per human ground truth — not false non-compliant |
| Document reference | Correct section (e.g. 9.4.1) and page — not generic intro |
| Confidence | Higher when extract is substantive; low confidence → **needs_review**, not silent non-compliant |
| Reverse (full run) | 9.4.1 → §8.4 mapped `covered` when policy implements requirement |
| Golden set | Fewer false non-compliant on “different wording” clauses after fixes |

---

## 6. Code reference map

| Concern | Primary files |
|---------|----------------|
| Forward judgment + policy context | `NdRegulAnalysisProcessor.cs` |
| Keyword retrieval | `NdRegulPolicyContextService.cs` |
| Default prompts (Base v1) | `NdRegulPromptDefaults.cs` |
| DB prompt versions (runtime) | `NdAnalysisPromptVersionService.cs` |
| Old semantic compare (reference) | `LandingAiComparePromptBuilder.PromptTemplateV3` |
| Quote / reference post-process | `NdRegulJudgmentPostProcessor.cs` |
| Internal section extract | `LandingAiPolicyClauseExtractService.cs` |
| Admin prompts UI | `nd-admin-prompts.component.*` |
| Analysis UI | `analyse-regul.component.*` |

---

## 7. Suggested prompt text (Phase 1 — copy into Admin)

### User block 2 — replace closing instruction

Keep `REGULATORY CLAUSE {clause_no}:` and `{clause_text}`; replace the last paragraph with:

```
Judge this clause against the excerpts above using semantic intent analysis, not keyword matching.
Search all excerpts thoroughly before concluding non_compliant.
Different wording and section numbers are acceptable when the control is equivalent (e.g. regulator "independent audit" vs internal "Internal Audit" rule).
If excerpts are incomplete or ambiguous, set low confidence and note that coverage may exist elsewhere in the manual — do not mark non_compliant solely because the excerpts are thin.
Mark non_compliant only when no substantive procedural equivalent appears in the excerpts for the regulatory intent.
```

### System — add after vendor due-diligence block

```
Semantic matching (AML-CFT):
- Compare by regulatory meaning and operational control, not keyword overlap.
- Different wording, section numbers, and document structure are acceptable when the control outcome is equivalent.
- Do not mark non_compliant when the internal policy clearly implements the regulatory intent with different terminology.

Independent vs internal audit:
- Regulator "independent audit function" is often implemented as Internal Audit with independence, AML/CFT programme testing scope, competent staffing, and (where applicable) qualified external auditors.
- Do not require the exact phrase "independent audit" if internal audit plus independence/testing language satisfies the regulatory intent. Section numbers may differ (e.g. regulator §8.4 vs internal Rule 9.4.1).

When checking enumerated elements, search for operational equivalents under different headings. Only mark an element not covered if no substantive equivalent exists in the excerpts.
```

---

*Last updated: 2026-08-04 — for Regul Analysis V3 forward matching / §8.4 case.*
