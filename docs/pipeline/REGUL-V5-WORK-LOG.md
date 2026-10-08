# Regul V5 - Work Log

Running record of the work on the V5 analysis (`/nd/analyse-regul-full-v2`): what was changed, why, where, and its
status. Newest work at the bottom of each day. **Keep this file up to date**: every session that changes the V5
pipeline, prompts or results adds its tasks here (what, why, commit, status), and updates the "Open items" list.

Related documents (all in `docs/pipeline/`):

| Document | What it is |
|---|---|
| `REGUL-V5-PIPELINE-REVIEW.md` | First review of every step, with findings |
| `REGUL-V5-STEPS-VERSIONS-COSTS.md` | Every step from upload to finalize: what it does, version in use, free or paid, cost |
| `REGUL-V5-FIX-PLAN.md` | Main fix plan (22 points, phases P0-P3) |
| `REGUL-V5-FIX-PLAN-V1.md` | Accuracy plan from the audit of the first v3 run (done before the rest of the main plan) |

Version switches (Admin > Analysis prompts):

| Switch | Versions | Default |
|---|---|---|
| Retrieval pipeline | v1 original, v2 expanded wording, **v3** whole clause + no limits, **v4** passages + equivalent terms + in-memory, **v5** v4 + gap check | stored admin choice (v2 unless changed) |
| Judgment prompt | ... v8, **v9** (current), **v10** (seeded, not current) | v9 |
| Embedding model | `RegulRetrieval:EmbeddingProvider`: `local` / `azure-openai` | local |

---

## 08 Oct 2026

### 1. Review of the whole V5 workflow - commit 05511fc

- **What:** wrote `REGUL-V5-PIPELINE-REVIEW.md`: every step (parse, extract, index, Steps 1-9, gaps, actions,
  finalize, embed) with how it works and whether it is right.
- **Why:** gaps were reported for content the internal documents already contain (3.3, 3.5) and actions asked for
  things the clause never states.
- **Findings:** the clause splitter dropped the end of long clauses; small embedding model reading only part of each
  section; too much unordered context; the AI re-deciding the requirements every run; the 3.3 "reporting deadline"
  action text was the demo template, not the AI.
- **Status:** done (analysis only).

### 2. Steps, versions and costs sheet - commit 61063d6

- **What:** `REGUL-V5-STEPS-VERSIONS-COSTS.md`: every step from upload to finalize, engine/version in use, free or
  paid, measured context size (~40-55k input tokens per clause), cost per clause today vs suggested.
- **Why:** you asked which steps cost money and what each one does.
- **Also found:** the Azure Document Intelligence cost is logged at the Read-model price while V5 uses the Layout
  model (log only; Azure billing unaffected).
- **Status:** done.

### 3. Fix plan and the no-limit decision - commit 7f9c96e

- **What:** `REGUL-V5-FIX-PLAN.md` (22 points with file, line, current behaviour, change, cost before/after).
- **Decision recorded:** no count limits in query expansion, clause split and section selection; relevance decides.
- **Found while planning:** the fusion maths dropped almost every section found only by keyword search.
- **Status:** done.

### 4. Pipeline v3 (fix plan P0, points 1-9) - commit 63c014d

| Change | Why | Where |
|---|---|---|
| Clause split searches every paragraph and list item, no 8-part limit, nothing dropped, lead-in once per item | Clause 3.5 lost its last parts (timeframe, independent offence) before the search | `SubObligationSplitter.SplitComplete` |
| Every section scored; relevance gate (2.5 standard deviations above the documents' average); rank fusion keeps keyword-only matches; no minimum / maximum | 60-section cap and fusion maths dropped exact-wording evidence | `HybridFusionSelector`, `RegulEmbeddingRetrievalService` |
| Running page headers removed from section text | The header line sat inside clause 3.5 and distorted search | `LocalSectionSplitter` |
| Harvested acronyms inactive until approved; 3 wrong seed synonyms retired; whole-word matching | Unreviewed acronyms affected every workspace; "policy" matched "policyholder" | `DictionaryExpansionService`, seed file |
| "Replace sample actions" button for real accounts | Old real-account runs still carried demo template actions | gap analysis page |
| Azure DI price per page as a setting (value unchanged) | Log-only cost figure | `AzureDocumentIntelligence:UsdPerPage` |
| Clause outline lists every sibling / sub-clause | No-limit decision | `NdRegulClauseContextService` |

- **Status:** done; tested by you on run f8a76442 (3.3, 3.5, 3.6).

### 5. Gap report UI - commit d107e25

- **What:** clause heading shows the title as well as the number (e.g. "§3.3 - Protection against Liability ...");
  "Expand all / Collapse all" for every gap and action in the Corrective Action Plan.
- **Why:** requested after the v3 run.
- **Status:** done.

### 6. Audit of the v3 run and Fix Plan V1 - commit 2b7fd3c

- **What:** every verdict, gap, action and quote of run f8a76442 checked against the 5 internal PDFs; root causes
  measured; `REGUL-V5-FIX-PLAN-V1.md` written.
- **Result:** verdicts 3/3 right; 2 of 5 gaps false (3.5 asset scope incl. crypto, 3.5 timeframe); evidence on
  AML Manual p.14 (3.3) never seen; main cause: sections up to 3,635 words while the embedding reads ~380.
- **Your decisions:** duplicate documents and per-document citations stay as they are.
- **Status:** done.

### 7. Fix Plan V1 built (pipeline v4, v5, prompt v10, retrieval check) - commit d6ef0ec

| Task | Change | Why |
|---|---|---|
| 1 | Retrieval check on the analysis report: expected snippets per clause, Steps 1-6 only, no AI | Test the search for free before paying for AI runs |
| 2 | Search passages (~150-300 words, heading path), new table `nd_local_document_passages` | Root cause: long sections invisible to the meaning search |
| 3 | 27 equivalent-term seed pairs; one search per alternative | "timeframe" vs "duration", "assets" vs "virtual assets", ... |
| 4 | Embedding provider setting (local default) | Option for a stronger model later |
| 5 | Vectors scored in memory, caches, per-clause time in the log | ~6-minute retrieval |
| 6 | Same no-limit relevance gate on passages | 3.3 had only 4 sections |
| 9 | Prompt v10 seeded, not current | Literal rules for definition clauses; one concept counted as 3 gaps |
| 10 | Pipeline v5 gap check (wider search + short AI question per gap, verbatim quote required) | False gaps went straight to the report |

- **Measured here:** passages max 271 words, nothing lost; keywords alone now select 7 of 9 previously missed
  passages; new tables tested on PostgreSQL 16 + pgvector; 19 new unit tests; web build passes.
- **Status:** built; **waiting for your test** (retrieval check, then Checkpoint A on v4, Checkpoint B on v5 + v10).

### 8. Plan V1 rewritten in the task format, this work log, embedding explained - this commit

- **What:** `REGUL-V5-FIX-PLAN-V1.md` restructured (what was wrong -> root causes -> tasks with bug, reason, fix,
  how it works, test, status); this log created; section 6 of the V1 plan explains embeddings and pgvector.
- **Status:** done.

---

## Open items

| Item | Next step | Owner |
|---|---|---|
| Retrieval check on run f8a76442 (pipeline v4) | Paste the snippets from Plan V1 section 4, Run, send the result | You |
| Checkpoint A (v4 + prompt v9) | Run 3.3, 3.5, 3.6 after the retrieval check looks right | You |
| Checkpoint B (v5 + prompt v10) | Run 3.3, 3.5, 3.6; compare with the expected results in Plan V1 section 4 | You |
| Re-tune the relevance gate if needed | Based on the retrieval check results | Claude |
| Azure DI price setting | Set `AzureDocumentIntelligence:UsdPerPage` once the Layout rate is confirmed (log only) | You |
| Stronger embedding model | Only if the retrieval check shows meaning-based misses | Decision |
| Main plan, after Checkpoint B | Workspace self-names (13), clause profile (14), structured judgment (16), finalize points (19-22) | Claude |
