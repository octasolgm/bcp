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
| Judgment prompt | ... v8, **v9** (current), **v10**, **v11** (seeded, not current; use v11) | v9 |
| Embedding model (search passages) | Admin > Analysis prompts > "Search embedding model": local bge-micro-v2 / Azure OpenAI text-embedding-3-small (config `RegulRetrieval:EmbeddingProvider` is the default when Admin has no choice) | **Azure OpenAI** since entry 13 (local when Azure is not configured) |

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

### 9. First v5 + v10 run on 3.5, prompt v11, passage build fixes - this commit

**Your test (3.5 only, pipeline v5, prompt v10):** status **compliant**, 80%, 4 covered points, no gaps. Wrong:

| Covered point in the result | Evidence cited | Check against the PDFs | Right? |
|---|---|---|---|
| [1] ML definition (4 acts) | AML Manual p.6 | Article (2) lists the same 4 acts | Yes |
| [2] Broad meaning of "funds" and "proceeds" | Document.pdf p.45, AML Manual p.59 / p.62 (typologies: commodities, assets, crypto) | Typologies show the asset scope in practice, but no document defines "funds" or "proceeds" or refers to the law's definitions | Scope yes, **definitions no** |
| [3] Size, timeframe and nature irrelevant | Document.pdf p.4 "regardless of the amount" | The quote covers the **amount only**; nothing quoted for timeframe or nature | **No** (only 1 of 3 parts) |
| [4] Independent offence, no proof of predicate | CandNM p.2 | Matches | Yes |

**Why (my prompt v10, not the search):**
- v10 said a definition clause is covered when the documents "state the definition ... OR apply the concept in
  practice", and told the AI to merge "a missing definition and a missing statement of the same scope" into one
  point. So the typologies covered the scope and the definitions went with it.
- v10 had no rule that a requirement with several parts needs evidence for each part, so one quote on the amount
  covered "size, timeframe and nature".
- The gap check never ran: the AI reported no gaps, so there was nothing to check.

**Fix - prompt v11** (`JudgmentSystemPromptV11`, `JudgmentUserQueryTemplateV11`, seeded **not current**):
- A term the clause formally defines ("X means / is defined as / the law defines X as ...") is its own requirement.
  It is covered only when the documents state that definition (any wording, same meaning) or adopt the law's
  definition by reference. Uses of the term, examples, typologies, red flags do not cover it. Missing = low gap.
  Never folded into a scope point.
- The scope a definition clause states (which assets, what is irrelevant, what need not be proven) is still covered
  by practice (typologies, red flags, procedures), as in v10.
- A requirement naming several elements is covered only when every element has its own quoted evidence; the rest is
  a gap naming exactly the missing elements. A requirement is covered only when policy_extract holds a quote for it.
- Gap check prompt (code, applies to v5 at once): a missing definition is covered only by an excerpt that states or
  adopts it.
- Dynamic: no clause, bank or document names in the rules; the examples in the rule are generic ("X means").

**Expected 3.5 on v5 + v11:** partial; covered: ML acts, asset scope (typologies), amount irrelevant, independent
offence; gaps: definitions of funds and proceeds (low); timeframe and nature irrelevant (low/medium) unless the gap
check finds and quotes it (Implementation Manual p.22 "describe the duration of the activity" is the candidate). Either
way the result now shows a quote for every covered part.

**Passage build (the long wait before Steps 1-6):**
- The first v4/v5 run built the passages of every document that had none, one passage at a time on the CPU, while the
  panel showed all steps at 0%. Now: a background job builds them for already-indexed documents (1 minute after
  start, then every 10 minutes, only while the pipeline version is v4 or v5); a run that still has to build them sets
  phase "passages" and the panel says "Preparing search passages (one-time per document)"; the log shows document
  i of n and the build time.
- Two runs started together could both build the same document's passages and double them. Builds are now one at a
  time per document, a doubled set is detected and rebuilt, and the build no longer keeps thousands of vectors
  tracked in the run's database context.
- Tests: 3 new (v11 rules, gap check definition rule, one extraction per document); suite 308 pass, same 24 old
  failures; web build passes.
- **Status:** built; waiting for your 3.5 run on v5 + **v11**.

### 10. Copyable pipeline report in the browser console; v11 checked against the expected results - this commit

- **What:** on the V5 analysis page, type in the browser console (F12 > Console):
  - `copy(bcpReport('3.5'))` - one plain-text report for the clause, ready to paste in the chat: Steps 1-6 time,
    term expansions, every part searched, keyword / meaning matches and the passages SELECTED for the AI (document,
    section, page, score, text), Step 7 passages sent with the prompt versions used, every AI call (model, time,
    size, raw answer), every gap check (the gap, the passages searched, the answer) and the saved result.
  - `copy(bcpReport('3.5', true))` - the same plus the full context, system prompt and every request (large).
  - `copy(bcpReport())` - every clause logged on the page.
  - Only shows what the page has loaded: open the run's result (AI traces load when a clause finishes; platform super
    admins only, as before).
- **Backend:** each clause's retrieval record now stores the Steps 1-6 time; the Step 7 trace notes the prompt
  version of each judgment prompt key.
- **Why:** you could not hand me the exact retrieval, prompt and AI answer of a run; the existing console groups do
  not copy well.

**Why prompt v10 produced a wrong 3.5 (my mistake, not a missing input):** you gave me everything needed, and
Plan V1 section 4 already had the right expected result (partial, definitions gap). I then wrote prompt v10 with a
rule that contradicts it ("definition covered when the concept is applied in practice" plus "merge a missing
definition into the scope point"), and I had no rule that every part of a multi-part requirement needs its own
evidence, although my own audit separated amount (covered) from timeframe / nature. I tested that the prompt was
valid and generic, but I did not check each rule against each expected result before you paid for the run.

**Check done now (v11 against Plan V1 section 4, rule by rule):**

| Clause | Expected | v11 rules that decide it | Agrees? |
|---|---|---|---|
| 3.3 | Compliant (p.42, p.14) | statutory protection; named parties are one requirement | Yes, after a fix: the new every-element rule could have required evidence per named party (board, employees, representatives). Fixed: the rule now excludes illustrative lists and named parties |
| 3.5 | Partial, definitions of funds / proceeds gap (low) | defined term needs its definition stated or adopted; scope covered by typologies; each condition (size, timeframe, nature) needs its own quote | Yes. Timeframe / nature: covered only if p.22 / p.24 are quoted, else a gap the gap check re-searches |
| 3.6 | Partial, predicate offence definition gap (low) | defined term rule | Yes |

- v11 had already been pushed (commit 4148904) with the first wording; if your API already added it, startup now
  refreshes that row to the corrected text (only rows still carrying the first v11 label, so an admin's own v11 is
  never overwritten).
- **Process from now on:** before any paid run, I check every prompt rule against every expected result in the plan
  and write the table above into this log.
- Tests: 1 new (v11 refresh); the prompt version test class now runs on the in-memory database (vector columns
  ignored), so 3 of its old tests moved from "cannot start" to "assertion fails" (pre-existing, unrelated to v11);
  suite 312 pass, 21 old failures; web build passes.
- **Status:** built; waiting for your 3.5 run on v5 + v11, with `copy(bcpReport('3.5'))` pasted back.

### 11. First 3.5 run on v5 + v11: review against the PDFs, action / risk fixes - this commit

**Result:** partial, 72%, 6 covered points, 2 gaps.

| Item | Result | Check against the PDFs | Right? |
|---|---|---|---|
| ML acts (4 acts, knowledge) | Covered, AML Manual p.6 | Article (2) | Yes |
| Asset scope (any tangible / intangible asset) | Covered by typologies (AML p.5, p.59, p.62; Document p.50-51) | Real estate, securities, artwork, property, vehicles, crypto | Yes |
| Amount irrelevant | Covered, Document p.4 | "regardless of the amount" | Yes |
| Nature of funds irrelevant | Covered by asset typologies | Applied in practice (v11 scope rule) | Yes |
| Independent offence / no proof of predicate / indicators | Covered, AML p.6, CandNM p.2 | Matches | Yes |
| NRA 2018 sentence | Not a gap | Context only | Yes |
| **Gap 1** funds / proceeds definitions, low | Gap | No document defines or adopts them | **Yes** (the expected gap) |
| **Gap 2** timeframe irrelevant, low | Gap | No document says the timeframe is irrelevant. In practice: Implementation Manual p.22 "If the activity takes place over a period of time ... describe the duration of the activity", p.24 review period "from 30 days to 90 days", red flags "over a short period of time" | **Borderline**: a low gap is defensible (nothing states it), but the answer should at least say "partly addressed: p.22", as it accepted practice for the nature of funds. Need `bcpReport('3.5')` to see whether p.22 reached the AI or the gap check |

Verdict: right (partial). Gaps: 1 right, 1 borderline. Plan V1 expected "timeframe covered"; corrected expectation: timeframe
either covered by practice with a p.22 quote, or a low gap that cites p.22 as partly addressed.

**Bugs on the gap report (not the judgment):**

| Bug | Cause | Fix |
|---|---|---|
| Gap 1 got a generic "Update the internal policy to address: ..." action; Gap 2 got Gap 1's definitions action; the timeframe action was lost | The AI keyed gaps and actions by requirement number ([2], [4]); the page lists gaps as 1, 2 and matched action [1] / [2] | `NdRegulGapVerifier.NormalizeGapNumbering` (V5 runs, before the gap check): gaps renumbered 1..n in order, each action follows its gap (or, when keys match no gap but there is one key per gap, the order) |
| Both gaps shown as Medium risk / 30 days although the AI said "Materiality: low" | The page's regul gap parser ignored the materiality and defaulted to medium | `capPriorityForRegulCapSegment` reads "Materiality: high / medium / low" first (demo gap text never contains it, so demo is unchanged) |
| Gap 1 text carried the whole definitions paragraph as its clause quote | Prompt asks for the clause words; the AI quoted ~90 words | Same normalisation: a clause quote over 25 words is cut to its first 15 words + "..." |

- The run already saved stays as it is: its draft actions were created with the old numbering. Run 3.5 again (new
  analysis) after pulling to see the fixed actions and risk.
- Tests: 4 new (renumbering from your run, order fallback, long quote, brackets inside a quote); suite 316 pass, same
  21 old failures; web build passes.
- **Status:** built; waiting for the 3.5 rerun and `copy(bcpReport('3.5'))` to settle gap 2.

### 12. Pipeline panel, embedding switch in Admin, timeframe re-checked, every plan task re-checked - this commit

#### 12.1 Pipeline panel (page `/nd/analyse-regul-full-v2`)

| Bug | Cause | Fix |
|---|---|---|
| On "Run", Steps 1-7 showed **done** for a moment, then **processing** | The page sets a local phase "forward" when it launches a run, before the server reports anything; the panel read "forward" as "retrieval finished". The previous run's clause data also stayed in the panel | The V5 page shows the run as queued until this run's own phase arrives from the server (new no-op hook `onNdServerPipelinePhase` in the shared page code, used only by V5), and clears the previous run's Steps 1-6 data when a different run is shown. Demo and other pages unchanged |
| Panel did not follow the run | - | The panel scrolls to Step 1 when a run starts and to Step 8 when the AI judgment starts |

#### 12.2 Embedding model: admin switch (no re-parse, no re-extract)

- Admin > Analysis prompts > **Search embedding model**: Local bge-micro-v2 (free) or Azure OpenAI
  (`AzureOpenAI:EmbeddingDeployment`, e.g. text-embedding-3-small). The Azure option is disabled when
  `AzureOpenAI:Endpoint / ApiKey / EmbeddingDeployment` are not configured; if the setting says Azure but the keys are
  missing, the local model is used and the log says so (nothing breaks).
- The model is fixed once per job (one analysis, one indexing job), so a switch never mixes models inside a run.
- Azure calls for passages go in batches of 16 with retry on 429 / 5xx (Retry-After respected). The existing
  single-text Azure call used by semantic extraction is **unchanged**.
- **Extraction and chunking are not changed.** Structural extraction (sections) stays exactly as it is; no semantic
  chunking is used anywhere in this work. Passages are a search index cut inside each structural section.

**Where embedding happens (the whole picture):**

| When | What is embedded | Model | Stored in | Used by |
|---|---|---|---|---|
| Indexing (after Extract, automatic) | each structural section | local bge-micro-v2 (fixed) | `nd_local_document_extraction_sections.embedding` | pipelines v1-v3 |
| Indexing, right after the sections | each search passage (~150-300 words inside a section, with its heading path) | the admin choice | `nd_local_document_passages.embedding` (+ model name) | pipelines v4 / v5 |
| Background, every 10 min (v4 / v5 only) | passages of documents that have none for the chosen model (older documents, or after a model switch) | the admin choice | same table | v4 / v5 |
| Each analysis, Steps 3-4 | every searched wording of every clause part (cached per job) | the same model as the passages | memory only | meaning search: compared with every passage vector |
| Gap check (v5) | the gap's requirement and clause words | same | memory only | wider search per gap |

Switching the model: set it in Admin, and passages are re-embedded in the background (or before the next search at
the latest). Old vectors of the other model are replaced, never compared. Cost on Azure: ~60k tokens for a
114-page document, about $0.0012 once, plus a fraction of a cent per analysis for the clause wording.

**Will Azure give better results?** It is a much stronger model (bge-micro-v2 is a 384-dimension micro model that reads
~380 words; text-embedding-3-small has 1,536 dimensions and reads ~6,000 words). It helps only where the policy uses
different words for the same idea and no equivalent-term pair exists (in the audit: p.36 "accept assets ... proceeds of
criminal activity", p.24 "30 days to 90 days"). It does not change the timeframe question below, which is a judgment
question, not a search miss. Recommendation: switch to Azure (cost is negligible, your documents already go to Azure
Document Intelligence), then compare one 3.5 run with `bcpReport` before and after.

#### 12.3 Timeframe (3.5): the AI or my earlier audit?

The clause: "the size ..., the **timeframe** during which it took place, and the nature of the funds ... are
**irrelevant** to the suspicion and reporting of a suspicious transaction."

| Passage | What it says | Does it cover "timeframe is irrelevant"? |
|---|---|---|
| Implementation Manual p.22 (STR drafting) | "If the activity takes place over a period of time, provide the date when the suspicious activity ... was first observed and describe the duration of the activity" | **Partly**: activity over any period is reported, with its duration; it does not say the timeframe is irrelevant to suspicion |
| Implementation Manual p.24 | "expanding the time period for reviewing alerted transactions (e.g., from 30 days to 90 days) ... to make the determination that an STR or SAR is required" | **Partly**: older activity is looked at before deciding; it is an alert-review practice, not a statement |
| AML Manual p.41 | "Submit a SAR within a reasonable timeframe of identifying the suspicious activity" | **No**: a filing deadline, another subject |
| Red flags (AML p.59, Impl p.48) | "transactions made over a short period of time" | **No**: timing used as an indicator |

**Verdict:** no document states that the timeframe is irrelevant, so the AI's **low gap is correct**. My audit of 08 Oct
called that gap "wrong" because of p.22 / p.24: that was overstated (I counted practice that only touches the subject).
The most accurate result is the AI's gap **plus** a "partly addressed: Implementation Manual p.22 / p.24" note, which
the gap check adds when it sees those passages and quotes them. Corrected in Plan V1 (sections 1.3 and 4). Whether the
gap check saw p.22 / p.24 on this run is in `copy(bcpReport('3.5'))` (gap check section); no prompt change before that.

#### 12.4 Every plan task re-checked

**Plan V1 (accuracy plan):**

| # | Task | Status | Verified on a real run |
|---|---|---|---|
| 1 | Free retrieval check | Done | Not used yet (optional) |
| 2 | Search passages with heading path (v4) | Done | Yes: 3.5 now cites AML p.5, p.59, p.62 typologies that the v3 run never saw |
| 3 | Equivalent-term groups | Done | Partly (timeframe / duration pair present; effect visible in `bcpReport`) |
| 4 | Embedding model option | Done, now an **admin switch** (12.2) | Not yet on Azure |
| 5 | Retrieval speed | Done + passages built ahead (entry 9) | First v5 run: wait was the one-time passage build |
| 6 | Selection on passages (no-limit relevance gate) | Done | Yes (3.5 context) |
| 7 | Duplicate documents | Not done by decision (evidence from every file is fine) | - |
| 8 | Per-document citation check | Not done by decision | - |
| 9 | Judgment prompt | v10 replaced by **v11** (entries 9-10) | Yes: 3.5 partial with the definitions gap |
| 10 | Gap check (v5) | Done | Ran on 3.5; outcome per gap visible in `bcpReport` |

**Main plan (22 points):**

| # | Point | Status |
|---|---|---|
| 1-9 | P0 bugs and no-limit (split, fusion, caps, running headers, dictionary, demo template text, Azure DI price setting, regulation docs, outline caps) | **Done** (v3, commit 63c014d). Point 4 applies to documents extracted after it; existing documents keep their text unless re-extracted (your choice, not required) |
| 10 | Search passages with heading path | **Done** (= V1 task 2) |
| 11 | Stronger embedding model | **Done as an option** (12.2); your switch |
| 12 | BM25 stemming, stop words, heading field | **Partly**: heading path is now in every passage's searched text; stemming not done |
| 13 | Concept groups + workspace self-names | **Partly**: 27 equivalent-term pairs; self-names (the bank's own name for itself) not done, prompt v10/v11 rule covers it meanwhile |
| 14 | Clause profile (requirement list, cached) | Not started |
| 15 | Evidence per requirement | Not started |
| 16 | Structured judgment, verdict in code | **Partly**: prompts v10/v11 (clause types, materiality); verdict still from the AI |
| 17 | Gap verification over all documents | **Done** (v5) |
| 18 | Eval labels + retrieval recall | **Partly**: retrieval check with expected snippets; no recall dashboard |
| 19-22 | Finalize loop (writer context, placement, inline DOCX, re-index corrected copy) | Not started |

**Flaws found in this session and their state:** all fixed except (a) the timeframe "partly addressed" note depends
on the gap check seeing p.22 / p.24 (check with `bcpReport`), (b) a context over ~150k tokens is flagged, not split
(point 16), (c) 3 old prompt-version unit tests fail on their own outdated assertions (pre-existing, unrelated).
Nothing in this session changed extraction, chunking, demo accounts or the v1-v3 pipelines.

- Tests: 6 new (embedding setting values, Azure configured check); suite 322 pass, same 21 old failures; web build
  passes.
- **Status:** built; next: pull, restart, (optional) switch the embedding model to Azure, run 3.5, send
  `copy(bcpReport('3.5'))`.

---

### 13. Azure text-embedding-3-small is the default for search passages - this commit

- `appsettings.json` `RegulRetrieval:EmbeddingProvider` = `azure-openai`: after pull + restart, search passages use
  Azure OpenAI `AzureOpenAI:EmbeddingDeployment` (text-embedding-3-small) unless Admin > Analysis prompts says
  otherwise. If `AzureOpenAI:Endpoint / ApiKey` are not set on the server, the local model is used and the log says so.
- **Chunking is not changed.** The passages are cut exactly as before (same splitter, same passages); only their
  vectors are recomputed with the new model, in the background (1 minute after start, then every 10 minutes while the
  pipeline is v4 / v5) or before the next search. Extraction (structural sections) is not touched. Section vectors used
  by pipelines v1-v3 stay on the local model.
- Cost: about $0.001 once per 114-page document, plus a fraction of a cent per analysis for the clause wording.

**Gap re-verification (asked again): yes, built in pipeline v5** (V1 Task 10, `NdRegulGapVerifier` +
`NdRegulAnalysisProcessor.VerifyGapsAsync`). After the AI's judgment of a clause, for every gap it reports:
1. the gap's missing requirement and the clause words it comes from are searched again over **every passage of every
   selected document** (with equivalent terms, no count limit), not only the passages the first judgment saw;
2. one short AI question per gap: "does any of these passages cover this gap? quote it";
3. the answer counts only if its quote is word for word in the passage it names (invented quotes are rejected);
4. covered: the gap is removed, becomes a covered point with that quote and page, its action is removed, the rest
   renumbered; partly covered: the gap stays with "Partly addressed: [document page] "quote""; not covered: unchanged;
   no gap left: the clause becomes compliant. A failed check never changes the result.
It runs only on **pipeline v5** (Admin > Analysis prompts > Retrieval pipeline version). In `bcpReport` each check is a
"Gap check AI call" with the gap, the passages searched and the answer.

### 14. 3.5 on the latest code (v5 + v11): review, report download - this commit

**Run (08 Oct, 8:39 PM):** Step 7 context 155,890 chars; Step 8 judgment 49.5 s; 3 gap checks 7.1 s, 4.5 s, 7.5 s;
saved 8:41 PM. Step 8 in total about 1 min 16 s (the 5+ minute run before was on code from before the pull).

| Item | Result | Check against the PDFs | Right? |
|---|---|---|---|
| ML acts with knowledge | Covered, AML Manual p.6 Article (2) | Matches | Yes |
| Independent offence | Covered, AML p.6, CandNM p.2 | Matches | Yes |
| No proof of predicate / inferred from indicators | Covered, AML p.6, Implementation p.3-4 | Matches | Yes |
| Asset scope (any tangible / intangible asset) | Covered by typologies (Implementation 2.2 p.45 / p.50) | Matches | Yes |
| Amount and nature of funds irrelevant | Covered, "regardless of the amount" (p.4) + asset typologies (AML 7.11 p.46) | Matches | Yes |
| NRA 2018 sentence | Not a gap (context only) | - | Yes |
| Gap 1 "funds" definition, low | Gap | No document defines it | Yes |
| Gap 2 "proceeds" definition, low | Gap | No document defines it | Yes (v11 allows one gap per defined term) |
| Gap 3 timeframe irrelevant, low | Gap | No document states it; Implementation p.22 / p.24 partly address it | Yes, but no "partly addressed" note (decision pending: option 1 / 2, entry 12.3 and chat) |

Verdict partial, 70%: matches the corrected expectation (Plan V1 section 4). **Fixes confirmed on a real run:** gaps
numbered 1-3 with their own actions; risk Low / 45 days from the AI's "Materiality: low"; long clause quotes cut;
asset typologies now found (they were missed in the v3 run).

**Seen, not changed (decided earlier):** some policy extracts carry the page of a neighbouring passage (for example the
crypto typology shown under Implementation "section 32, p.3"); citations stay as they are by decision (V1 Task 8).

**Not yet known:** whether this run used Azure embeddings, and whether the timeframe gap check saw p.22 / p.24 (DevTools
hung on `copy(bcpReport('3.5'))`).

**Changes:**
- **Download report** (and **Full**) buttons in the pipeline panel save the report as a .txt file; `bcpDownload('3.5')`
  does the same from the console. No DevTools copy needed.
- Each clause's retrieval record and the report show the **embedding model** used (`azure-openai:...` or
  `local:bge-micro-v2`).
- The Admin "Search embedding model" card shows a load error instead of disappearing when the API does not answer
  (for example an API not rebuilt after a pull), and is shown on every prompt tab.
- Gap checks stay sequential: 4-8 s each on this run, no need for parallel calls.
- Tests: suite 322 pass, same 21 old failures; web build passes.

---

## Pending tasks (kept up to date)

### A. Your tests (no code)

| # | Task | Detail | Why |
|---|---|---|---|
| A1 | Run 3.5 on the latest code (done once, entry 14; repeat after this pull with the Download report button) | Pull (entry 13 commit), restart the API (adds prompt v11, starts Azure passage vectors), Admin: pipeline **v5**, prompt **v11** current for all 3 judgment prompts; new analysis with only 3.5; then `copy(bcpReport('3.5'))` in the browser console and paste it | Confirms Azure vectors, whether Implementation Manual p.22 / p.24 reach the AI and the gap check, gap numbering and Low risk on the new run |
| A2 | Checkpoint B on 3.3 and 3.6 | Same settings; compare with Plan V1 section 4 (3.3 compliant with AML p.42 / p.14; 3.6 partial, predicate offence definition gap, CandNM p.2) | v11 checked on paper against these; needs a real run |
| A3 | Old runs | Runs before entry 11 keep their mis-numbered draft actions and Medium risk; delete them or run the clause again | Saved data is not rewritten |
| A4 | Azure DI price | Set `AzureDocumentIntelligence:UsdPerPage` once the Layout rate on the invoice is confirmed | Log-only cost figure |
| A5 | Optional re-extract | Documents extracted before 08 Oct keep repeated page headers in their section text; re-extract (free, no re-parse) if you want them removed | Point 4; extraction logic itself unchanged |

### B. Depends on the A1 report

| # | Task | Detail |
|---|---|---|
| B1 | Timeframe "partly addressed" | If p.22 / p.24 reached the gap check but no "Partly addressed" note was added, fix the gap check's partial handling (checked first against 3.3 / 3.5 / 3.6 expectations) |
| B2 | Relevance gate | If too many or too few passages are selected (Step 5+6 list in the report), re-tune the 2.5 standard-deviation gate on passages |
| B3 | Embedding comparison | Compare the selected passages with the earlier local-model run; record in this log |

### C. Main plan, not started or partly done

| # | Point | What is left | Why it matters |
|---|---|---|---|
| C1 | 12 BM25 | Word stemming (report / reported / reporting), stop words per domain; heading already searched | Keyword search misses inflected words |
| C2 | 13 Self-names | Learn each workspace's own name for itself (e.g. "DIFC", "UAE" used as the institution's name in its policies) and treat it as "the institution" | Prompt rule covers it today; making it data is more reliable |
| C3 | 14 Clause profile | One AI pass per regulation clause that lists its requirements (with clause words, type: obligation / definition / scope / context), cached and reused by every run | Same requirement list every run: stable numbering, no re-decomposition, cheaper |
| C4 | 15 Evidence per requirement | Search per requirement of the profile, evidence pack with ids per requirement | AI sees the right evidence next to each requirement; smaller context |
| C5 | 16 Structured judgment | Per-requirement verdict in JSON, overall status computed in code; split an oversized context into several calls (today only flagged above ~150k tokens) | Status always consistent with the gaps; no silent context limits |
| C6 | 18 Evals | Expected result labels per clause and a retrieval recall figure per run | Measures every change before it reaches you |
| C7 | 19 Finalize writer | Give the writer the document's own section text and terms | Inserted wording matches the policy's style |
| C8 | 20 Placement | Place the note from the requirement's passage, not a guess | Text lands in the right section |
| C9 | 21 Inline DOCX | Corrected copy as DOCX for PDF sources | Usable corrected document |
| C10 | 22 Re-index and re-check | Index the corrected copy as the current version and re-check the finalized clauses | Closes the loop: fixed gaps disappear |

### D. Housekeeping

| # | Task | Detail |
|---|---|---|
| D1 | Prompt v11 as default | After A1 / A2 pass, make v11 the current version for everyone (today an admin switch) |
| D2 | 3 old prompt-version unit tests | Their assertions predate automatic seeding of newer versions; update them (pre-existing failures, unrelated to this work) |
| D3 | 18 other old test failures | Pre-existing (query translation and others); not caused by this work, to be fixed separately |

### Decided not to do

- Duplicate-document detection (V1 Task 7): evidence from every file is shown.
- Per-document citation check (V1 Task 8): citations stay as they are.
- Any change to extraction or chunking, semantic chunking, demo accounts or pipelines v1-v3.
