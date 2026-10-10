# Regul V5 - Work Log (task list)

Every task done on the V5 analysis (`/nd/analyse-regul-full-v2`), written the same way as the plans: what was wrong,
why, what was changed and how it works, where in the code, how it was verified, status and commit.

**Keep this file up to date**: every session that changes the V5 pipeline, prompts, UI or results adds its tasks to
the overview table and as a task section, records test runs in section 3, and updates the pending list (section 5).

Related documents (all in `docs/pipeline/`):

| Document | What it is |
|---|---|
| `REGUL-V5-PIPELINE-REVIEW.md` | First review of every step, with findings |
| `REGUL-V5-STEPS-VERSIONS-COSTS.md` | Every step from upload to finalize: what it does, version in use, free or paid, cost |
| `REGUL-V5-FIX-PLAN.md` | Main fix plan (22 points, phases P0-P3) |
| `REGUL-V5-FIX-PLAN-V1.md` | Accuracy plan from the audit of the first v3 run |
| `REGUL-V5-PLAN-V2.md` | Next tasks (1-12) after the 3.5 runs: page references, search, answer keys, requirement-based judgment (pipeline v6 / prompt v12), finalize |
| `REGUL-V5-PLAN-V2-IMPLEMENTATION.md` | How to build Plan V2 in a new session: rules, commands, test baseline, code map, one copy-paste prompt per task |

Settings that decide how an analysis runs (Admin > Analysis prompts, `/nd/admin/prompts`):

| Setting | Options | Use for testing |
|---|---|---|
| Retrieval pipeline version | v1 original, v2 expanded wording, v3 whole clause + no limits, v4 search passages + equivalent terms, v5 = v4 + gap check | **v5** |
| Judgment prompts (system, user block 1, user block 2) | ... v9 (default), v10 (replaced), **v11** | **v11** on all three |
| Search embedding model | Local bge-micro-v2 (free) or Azure OpenAI text-embedding-3-small | **Azure OpenAI** (default since T25; local when Azure keys are missing) |

---

## 1. Task overview

Type: **Bug** = something gave wrong results or broke; **Feature** = new capability; **Analysis** = investigation
or document, no code.

| # | Task | Type | Status | Verified on a real run | Commit |
|---|---|---|---|---|---|
| T1 | Review of the whole V5 workflow | Analysis | Done | - | 05511fc |
| T2 | Steps, versions and costs sheet | Analysis | Done | - | 61063d6 |
| T3 | Fix plan (22 points) and the no-limit decision | Analysis | Done | - | 7f9c96e |
| T4 | Clause split dropped the end of long clauses | Bug | Done | Yes (3.5 searched in full) | 63c014d |
| T5 | Fusion dropped keyword-only matches; count caps | Bug | Done | Yes | 63c014d |
| T6 | Running page headers inside clause and section text | Bug | Done | On re-extract only | 63c014d |
| T7 | Dictionary: unreviewed acronyms, wrong synonyms, substring matches | Bug | Done | Yes | 63c014d |
| T8 | Demo template action text on real-account runs | Bug | Done | Yes | 63c014d |
| T9 | Azure Document Intelligence price logged at the wrong rate | Bug (log only) | Setting added, value to confirm | - | 63c014d |
| T10 | Clause outline capped at 80 headings | Bug (no-limit) | Done | - | 63c014d |
| T11 | Clause title in the heading; Expand all / Collapse all | Feature | Done | Yes | d107e25 |
| T12 | Audit of the first v3 run and Plan V1 | Analysis | Done | - | 2b7fd3c |
| T13 | Free retrieval check | Feature | Done | Not used yet | d6ef0ec |
| T14 | Long sections invisible to the meaning search: search passages (v4) | Bug | Done | Yes (asset typologies now found) | d6ef0ec |
| T15 | Different words for the same idea: equivalent-term pairs | Bug | Done | Partly | d6ef0ec |
| T16 | Retrieval took ~6 minutes for 3 clauses | Bug | Done | Yes (Steps 1-6 ~8 s) | d6ef0ec |
| T17 | False gaps went straight to the report: gap check (v5) | Feature | Done | Yes (3 checks on 3.5) | d6ef0ec |
| T18 | Judgment rules: prompt v10, replaced by v11 | Bug | Done (v11) | Yes (3.5 correct on v11) | d6ef0ec, 4148904, 6d11a2c |
| T19 | Long wait before Steps 1-6 and doubled passages | Bug | Done | Yes | 4148904 |
| T20 | No way to see what the AI was given and answered: pipeline report | Feature | Done | Console hung, download added; empty download fixed | 6d11a2c, f16abf5, this commit |
| T21 | Actions attached to the wrong gap | Bug | Done | Yes | 99282ad |
| T22 | Gap risk always Medium | Bug | Done | Yes (Low / 45 days) | 99282ad |
| T23 | Gap lines quoting the whole clause paragraph | Bug | Done | Yes | 99282ad |
| T24 | Pipeline panel showed done, then processing; did not follow the run | Bug | Done | To confirm | c332a6d |
| T25 | Stronger embedding model: Admin switch, Azure default | Feature | Done | To confirm (model now in the report) | c332a6d, d0fd31e, f16abf5 |
| T26 | Timeframe on 3.5: AI or audit right? | Analysis | Done (decision pending) | - | c332a6d |
| T27 | Plan V1 task format and this work log | Analysis | Done | - | d4225f3 and later |
| T28 | Gap check said "not covered" for evidence that partly addresses a gap | Bug | Done | To confirm on the next run | this commit |
| T29 | Steps 1-6 took 33 s with Azure (one call per search text) | Bug | Done | To confirm | this commit |
| T30 | Wrong acronym pairs produced nonsense search wording | Bug | Done (v4 / v5) | To confirm | this commit |
| T31 | Plan V2 task 1: page reference next to the wrong quote | Bug | Done (all pipelines) | To confirm on the next run | branch feature/regul-plan-v2 |
| T32 | Plan V2 task 2: keyword search understands word forms; pipeline v6 added | Bug | Done (v6 only) | To confirm on a v6 run | branch feature/regul-plan-v2 |
| T33 | Plan V2 task 3: the bank's own name for itself | Bug | Done (v6, automatic detection) | To confirm on a v6 run of 3.3 | branch feature/regul-plan-v2 |
| T34 | Gap re-check costs one AI call per gap: setting to skip low-risk gaps | Feature | Done (on by default) | To confirm | branch feature/regul-plan-v2 |
| T35 | "SAR" detected as one of the bank's own names | Bug | Done | To confirm | branch feature/regul-plan-v2 |
| T36 | Excel / PDF "Identified Gaps" column showed the action text instead of the gaps; export now matches the gap analysis page (edits, resolutions) | Bug | Done | To confirm on the next export | branch feature/regul-plan-v2 |

---

## 2. Tasks

### T1 - Review of the whole V5 workflow (Analysis, commit 05511fc)

- **Problem:** gaps were reported for content the internal documents already contain (3.3 good-faith protection,
  3.5 timeframe) and actions asked for things the clause never states (3.3 "reporting deadline").
- **What I did:** reviewed every step (parse, extract, index, Steps 1-9, gaps, actions, finalize, embed): how it
  works and whether it is right. Written to `REGUL-V5-PIPELINE-REVIEW.md`.
- **Found:** the clause splitter dropped the end of long clauses; the small embedding model read only part of each
  section; too much unordered context; the AI re-decides the requirements every run; the 3.3 "reporting deadline"
  action was the demo template, not the AI.
- **Status:** done.

### T2 - Steps, versions and costs sheet (Analysis, commit 61063d6)

- **Problem:** no single view of which steps cost money and which version each step uses.
- **What I did:** `REGUL-V5-STEPS-VERSIONS-COSTS.md`: every step from upload to finalize, engine and version, free or
  paid, measured context (~40-55k input tokens per clause), cost per clause today and after the plan.
- **Found:** the Azure Document Intelligence cost is logged at the Read-model price while V5 uses the Layout model
  (log only, billing unaffected; see T9).
- **Status:** done.

### T3 - Fix plan and the no-limit decision (Analysis, commit 7f9c96e)

- **What I did:** `REGUL-V5-FIX-PLAN.md`: 22 points, each with file, current behaviour, change, cost before and after.
- **Decision recorded:** no count limits in query expansion, clause split and section selection; relevance decides.
- **Found while planning:** the fusion maths dropped almost every section found only by keyword search (T5).
- **Corrected later:** point 8 (regulation documents embedded for nothing) was wrong; Extract already skips them.
- **Status:** done.

### T4 - Clause split dropped the end of long clauses (Bug, commit 63c014d)

- **Problem:** on 3.5 the parts "size / timeframe / nature of funds are irrelevant", "independent offence" and "no
  proof of the predicate offence" were never searched, so they could only be judged without their evidence.
- **Cause:** Step 2 kept at most 8 parts, dropped short pieces and repeated the lead-in.
- **Fix:** every paragraph and list item is a part (OCR "." bullets and inline (a)(b) items too), the lead-in is added
  once per item, short pieces are merged, nothing is dropped, no count limit. 3.5 now gives 15 parts.
- **Where:** `SubObligationSplitter.SplitComplete` (pipeline v3+).
- **Verified:** unit tests on the 3.5, 3.3 and 3.10 text; 3.5 runs search every part.
- **Status:** done.

### T5 - Fusion dropped keyword-only matches; count caps (Bug, commit 63c014d)

- **Problem:** passages that use the clause's exact words, but are not close in meaning, never reached the AI.
- **Cause:** the 0.4 / 0.6 fusion score gave keyword-only matches at most 0.4, below the cutoff; plus a 300-candidate
  cap per search and a 60-section cap.
- **Fix:** every section is scored on both sides; a section is selected when it scores at least 2.5 standard
  deviations above the documents' average for any part of the clause (best match always kept); results combined by
  rank (reciprocal rank fusion); no minimum or maximum count. A context over ~150k tokens is flagged on the trace.
- **Where:** `HybridFusionSelector.SelectRelevant`, `FuseByRank`; `RegulEmbeddingRetrievalService`.
- **Status:** done.

### T6 - Running page headers inside clause and section text (Bug, commit 63c014d)

- **Problem:** the page header ("Anti-Money Laundering ... Guidelines for Financial Institutions") sat in the middle of
  clause 3.5's text and of policy sections, distorting the search.
- **Fix:** lines repeated in the first or last 3 lines of 3 or more pages are removed (table rows kept).
- **Where:** `LocalSectionSplitter.PageEdgeRunningLines`.
- **Note:** applies to documents extracted after the change; older documents keep their text unless re-extracted
  (free, no re-parse, optional). Extraction logic otherwise unchanged.
- **Status:** done.

### T7 - Dictionary: unreviewed acronyms, wrong synonyms, substring matches (Bug, commit 63c014d)

- **Problem:** acronyms harvested from documents were live in every workspace without review; three seed synonyms were
  wrong (PEP = high-risk customer, money laundering = financial crime, policy = manual); "policy" matched inside
  "policyholder".
- **Fix:** harvested acronyms are inactive until approved on the dictionary page; the three wrong pairs are
  deactivated once at startup and removed from the seed file; full forms and synonyms match whole words only.
- **Where:** `DictionaryExpansionService`, `SeedData/synonym-seed.json`.
- **Status:** done.

### T8 - Demo template action text on real-account runs (Bug, commit 63c014d)

- **Problem:** the 3.3 action "define the escalation path and reporting deadline" came from the demo action template,
  seeded on real-account runs before real accounts switched to the AI's own actions.
- **Fix:** "Replace N sample action(s)" button on the gap analysis page (workspace admins, real accounts only) deletes
  unresolved drafts that still carry template text and seeds the AI's own action. Demo accounts unchanged.
- **Where:** `action-plan-seed.ts` (`isSeedTemplateActionText`), gap analysis page.
- **Status:** done.

### T9 - Azure Document Intelligence price at the wrong rate (Bug, log only, commit 63c014d)

- **Problem:** the parse cost in the usage log uses the Read price ($0.0015 / page); V5 uses the Layout model.
- **Fix:** the price per page is a setting, `AzureDocumentIntelligence:UsdPerPage`, value unchanged until the invoice
  rate is confirmed.
- **Status:** setting done; **your action**: set the value (pending A4).

### T10 - Clause outline capped at 80 headings (Bug, commit 63c014d)

- **Fix:** the outline sent with each clause lists every sibling and sub-clause heading (no-limit decision).
- **Where:** `NdRegulClauseContextService`.
- **Status:** done.

### T11 - Clause title in the heading; Expand all / Collapse all (Feature, commit d107e25)

- **Request:** show the clause title above "Regulatory requirement", and one button to open or close every action.
- **Fix:** the clause card heading shows "§3.3 - Protection against Liability ..."; Expand all / Collapse all in the
  Corrective Action Plan toolbar. Demo rendering unchanged.
- **Where:** `nd-gap-point-detail.component`, `nd-action-plans-section.component`.
- **Status:** done, seen on your runs.

### T12 - Audit of the first v3 run and Plan V1 (Analysis, commit 2b7fd3c)

- **What I did:** every verdict, gap, action and quote of run f8a76442 (3.3, 3.5, 3.6) checked against the 5 internal
  PDFs; root causes measured; `REGUL-V5-FIX-PLAN-V1.md` written.
- **Result:** verdicts 3 of 3 right; on 3.5 the asset-scope gap was false (crypto and other assets are covered by
  typologies); 3.3 evidence on AML Manual p.14 was never seen; main cause: sections up to 3,635 words while the
  embedding model reads ~380.
- **Corrected later (T26):** I also called the 3.5 timeframe gap false; that was overstated.
- **Your decisions:** duplicate documents and per-document citations stay as they are.
- **Status:** done.

### T13 - Free retrieval check (Feature, commit d6ef0ec)

- **Problem:** every change had to be tested with a paid AI run, and a run cannot tell whether a false gap came from
  the search or from the AI.
- **Fix:** "Retrieval check" on the analysis report (platform super admins): save expected evidence snippets per
  clause, run Steps 1-6 only (no AI), and see for each snippet: selected (rank), not selected, or not in the text.
- **Where:** `RegulEmbeddingRetrievalService.CheckClauseAsync`, `EvalsController` (retrieval-check endpoints), table
  `nd_retrieval_expectations` (workspace-scoped), `nd-retrieval-check-drawer`.
- **Status:** done; optional, not used yet.

### T14 - Long sections invisible to the meaning search: search passages (Bug, commit d6ef0ec)

- **Problem:** evidence in the middle or end of long sections (crypto typology at word 3,286 of Annex 1; AML Manual
  p.14 for 3.3) was never found.
- **Cause:** the embedding model reads ~380 words; sections are up to 3,635 words; everything after word ~380 was
  invisible to the meaning search.
- **Fix:** each structural section is also cut into **search passages** of ~150-300 words, along its own sub-headings
  and paragraphs, each carrying its heading path ("AML Manual > Annex 1 > B.18 Other payment technologies"). Pipelines
  v4 / v5 search and send passages. **Extraction and sections are unchanged**; passages are a search index on top.
- **Where:** `LocalPassageSplitter`, `NdPassageIndexService`, table `nd_local_document_passages` (workspace-scoped),
  `RegulEmbeddingRetrievalService.LoadPassageCorpusAsync`.
- **Verified:** offline, longest passage 271 words, nothing lost; on the v11 runs 3.5 cites the asset typologies (AML
  p.5, p.59, p.62) that the v3 run never saw.
- **Status:** done.

### T15 - Different words for the same idea: equivalent-term pairs (Bug, commit d6ef0ec)

- **Problem:** the clause says "timeframe", the policy says "duration" or "time period"; "assets" vs "virtual assets".
- **Fix:** 27 equivalent-term seed pairs; a term with several equivalents is searched once with each of them.
- **Where:** `SeedData/synonym-seed.json`, `RegulEmbeddingRetrievalService.BuildExpandedWordingVariants`.
- **Status:** done.

### T16 - Retrieval took ~6 minutes for 3 clauses (Bug, commit d6ef0ec)

- **Cause:** every search went to the database; dictionary matchers and query vectors were rebuilt every time.
- **Fix:** passage vectors loaded once per run and scored in memory; dictionary matchers and query vectors cached;
  Steps 1-6 time logged per clause.
- **Verified:** last 3.5 run, Steps 1-6 in ~8 s.
- **Status:** done.

### T17 - Gap check before a gap is saved, pipeline v5 (Feature, commit d6ef0ec)

- **Problem:** a gap went straight from the AI's first answer to the report, even when another passage covered it.
- **How it works:** after the judgment, for every gap:
  1. the missing requirement and its clause words are searched again over every passage of every selected document;
  2. one short AI question: "does any of these passages cover it? quote it";
  3. the answer counts only if the quote is word for word in the passage it names;
  4. covered: the gap becomes a covered point with the quote and page, its action is removed, the rest renumbered;
     partly covered: the gap stays with "Partly addressed: [page] "quote""; not covered: unchanged; a failed check
     never changes the result.
- **Where:** `NdRegulGapVerifier`, `NdRegulAnalysisProcessor.VerifyGapsAsync`, trace step `gap_verify`.
- **Verified:** last 3.5 run, 3 gap checks of 4-8 s each, all 3 gaps kept (correct). A missing definition is covered
  only by an excerpt that states or adopts it (rule added with T18).
- **Status:** done.

### T18 - Judgment rules: prompt v10, replaced by v11 (Bug, commits d6ef0ec, 4148904, 6d11a2c)

- **Problem (v9):** too literal for definition clauses; each example of an illustrative list counted as a requirement;
  one concept reported as 3 gaps; examples from one bank's documents inside the prompt.
- **v10 fix:** clause type first (obligation, definition / interpretation, statutory protection, penalty, summary,
  context-only), illustrative lists as one requirement, one gap per concept with the clause words and a materiality,
  no document-specific examples.
- **v10 was wrong on your first run:** 3.5 came back **compliant**. v10 let typologies cover the "funds" / "proceeds"
  definitions and merged them into the scope point; and one quote on the amount covered "size, timeframe and nature".
  My mistake: I did not check each rule against the expected results before your run.
- **v11 fix:**
  - a term the clause formally defines ("X means ...", "the law defines X as ...") is its own requirement, covered only
    when the documents state that definition or adopt the law's definition by reference; examples and typologies do
    not cover it; missing = low gap;
  - the scope a definition clause states (which assets, what is irrelevant) is still covered by practice;
  - a requirement with several distinct conditions (size, timeframe, nature) needs quoted evidence for each one;
  - this rule never applies to illustrative lists or to parties named with the institution (found by checking v11
    against 3.3 before your run: it would have asked evidence for board, employees and representatives separately).
- **Where:** `NdRegulPromptDefaults` (`JudgmentSystemPromptV11`, `JudgmentUserQueryTemplateV11`),
  `NdAnalysisPromptVersionService.EnsureJudgmentSemanticV11Async` (seeded not current; a first-seed v11 row is
  refreshed to the corrected text, an admin's own v11 never), gap check prompt in `NdRegulGapVerifier.BuildPrompt`.
- **Verified:** 3.5 on v11: partial with the definitions gaps (section 3).
- **Status:** done; v11 must be set current in Admin (pending D1 to make it the default).

### T19 - Long wait before Steps 1-6 and doubled passages (Bug, commit 4148904)

- **Problem:** the first v4 / v5 run sat at "Processing 0%" for minutes; running twice could write a document's
  passages twice.
- **Cause:** the run built the passages of every older document one by one before searching, invisibly; two runs could
  build the same document at the same time.
- **Fix:** a background job builds missing passages (1 minute after start, then every 10 minutes, while the pipeline is
  v4 / v5); a run that still has to build them shows "Preparing search passages (one-time per document)"; builds are
  one at a time per document; a doubled set is detected and rebuilt.
- **Where:** `Workers/PassageBackfillHosted`, `NdPassageIndexService.EnsureCurrentAsync`,
  `RegulEmbeddingRetrievalService.PrepareSearchPassagesAsync`, pipeline panel.
- **Status:** done.

### T20 - Pipeline report (Feature, commits 6d11a2c, f16abf5)

- **Problem:** you could not hand me what the search selected, what the AI was given and what it answered.
- **Fix:** one plain-text report per clause: Steps 1-6 time and **embedding model**, terms expanded, every part
  searched, keyword and meaning matches, passages selected for the AI, passages sent with the **prompt versions**,
  every AI call (model, time, size, answer), every gap check (gap, passages searched, answer), the saved result.
  - **Download report** / **Full** buttons above "Pipeline steps" in the panel save it as a .txt file;
  - console: `bcpDownload('3.5')` (file) or `copy(bcpReport('3.5'))` (clipboard; DevTools hung on a large report,
    hence the download).
- **Where:** `pipeline-console-log.ts`, pipeline panel; retrieval record stores the Steps 1-6 time and embedding model;
  the Step 7 trace notes the prompt versions.
- **Bug found on first use:** the first download was an empty file. The report only held what the page had logged
  while polling, and a reloaded page with a finished run logs nothing. **Fix:** Download report now fetches the run's
  retrieval records (run status) and all AI traces (clause traces) from the server; if there is still nothing, the
  file says so instead of being empty. Works on any existing run, no new analysis needed.
- **Status:** done; first download pending (A1).

### T21 - Actions attached to the wrong gap (Bug, commit 99282ad)

- **Problem:** on 3.5, gap 1 got a generic "Update the internal policy to address: ..." action, gap 2 got gap 1's
  definitions action, and the timeframe action was lost.
- **Cause:** the AI numbered gaps and actions by requirement number ([2], [4]); the page lists gaps as 1, 2 and looked
  for actions [1], [2].
- **Fix:** on V5 runs, before the gap check, gaps are renumbered 1..n in order and every action follows its gap (or
  the order, when the keys match no gap but there is one key per gap).
- **Where:** `NdRegulGapVerifier.NormalizeGapNumbering`, called in `NdRegulAnalysisProcessor`.
- **Verified:** last 3.5 run, 3 gaps with their own actions.
- **Status:** done (runs saved before keep their old actions, pending A3).

### T22 - Gap risk always Medium (Bug, commit 99282ad)

- **Cause:** the page's gap parser ignored the AI's "Materiality: low / medium / high" and defaulted to Medium
  (30 days).
- **Fix:** the materiality in the gap line sets the gap risk (demo gap text never contains it, so demo is unchanged).
- **Where:** `cap-gap-count.ts` (`capPriorityForRegulCapSegment`).
- **Verified:** last 3.5 run, Low / priority 20 / 45 days.
- **Status:** done.

### T23 - Gap lines quoting the whole clause paragraph (Bug, commit 99282ad)

- **Fix:** a clause quote over 25 words inside a gap line is cut to its first 15 words + "...".
- **Where:** `NdRegulGapVerifier.NormalizeGapNumbering`.
- **Verified:** last 3.5 run.
- **Status:** done.

### T24 - Pipeline panel showed done, then processing (Bug, commit c332a6d)

- **Problem:** on Run, Steps 1-7 showed done for a moment and then processing; the panel did not follow the run.
- **Cause:** the page sets a local phase "forward" when it launches a run, before the server reports anything; the
  panel read it as "search finished"; the previous run's data also stayed in the panel.
- **Fix:** the V5 page shows the run as queued until the server reports its phase, and clears the previous run's data;
  the panel scrolls to Step 1 when a run starts and to Step 8 when the AI judgment starts. Shared page code only gains a
  hook that does nothing on other pages; demo unchanged.
- **Where:** `analyse-regul-full-v2.component.ts` (`panelPhase`, `onNdServerPipelinePhase`),
  `nd-pipeline-panel.service.ts` (`clearRunData`), `nd-pipeline-progress-panel.component.ts` (`currentStepKey`).
- **Status:** done; to confirm on your next run.

### T25 - Stronger embedding model: Admin switch, Azure default (Feature, commits c332a6d, d0fd31e, f16abf5)

- **Problem:** the local bge-micro-v2 model is small (384 dimensions, ~380 words); it misses passages that say the same
  thing in other words.
- **Fix:**
  - Admin > Analysis prompts > **Search embedding model**: Local or Azure OpenAI (text-embedding-3-small). The Azure
    option is disabled when `AzureOpenAI:Endpoint / ApiKey / EmbeddingDeployment` are missing; the card shows an error
    if it cannot load.
  - **Default is Azure** (`appsettings.json` `RegulRetrieval:EmbeddingProvider`); falls back to local when the keys are
    missing (logged).
  - The model is fixed once per job, so a run never mixes models; Azure calls in batches of 16 with retry on 429 / 5xx.
  - **No re-parse, no re-extract, chunking unchanged:** the same passages get new vectors in the background (T19) or
    before the next search. Section vectors for pipelines v1-v3 stay local. The Azure call used by semantic extraction
    is unchanged.
- **Where embedding happens:**

| When | What | Model | Used by |
|---|---|---|---|
| Indexing (after Extract) | each structural section | local (fixed) | pipelines v1-v3 |
| Indexing, right after | each search passage | the Admin choice | v4 / v5 |
| Background every 10 min | passages missing for the chosen model | the Admin choice | v4 / v5 |
| Each analysis, Steps 3-4 | the wording of each clause part | same as passages | meaning search |
| Gap check | the missing requirement | same | wider search per gap |

- **Cost:** about $0.001 once per 114-page document, plus a fraction of a cent per analysis.
- **Where:** `PassageEmbeddingService`, `AzureOpenAIEmbeddingClient.EmbedBatchAsync`,
  `RegulWorkflowLlmSettingsService` (embedding provider setting), `SystemSettingsController`
  (`regul-embedding-provider`), `nd-admin-prompts.component`.
- **Status:** done; confirm "Azure OpenAI (current)" in Admin and the model line in the report (A1).

### T26 - Timeframe on 3.5: the AI or my audit? (Analysis, commit c332a6d)

- **The clause:** "The size or monetary value ..., the **timeframe** during which it took place, and the nature of the
  funds ... **are irrelevant** to the suspicion and reporting of a suspicious transaction."

| Passage | Text | Covers it? |
|---|---|---|
| Implementation Manual p.22 (STR drafting) | "If the activity takes place over a period of time ... describe the duration of the activity" | Partly: activity over time is reported |
| Implementation Manual p.24 | "expanding the time period for reviewing alerted transactions (e.g., from 30 days to 90 days) ... to make the determination that an STR or SAR is required" | Partly: older activity is reviewed, within a window |
| Implementation Manual p.26 | "This RFI timeframe is within 20 working days" | No: FIU request deadline |
| AML Manual p.41 | "Submit a SAR within a reasonable timeframe" | No: filing deadline |

- **Verdict:** no document says the timeframe is irrelevant (the amount is: "regardless of the amount"), so the AI's
  low gap is right; my audit overstated p.22 / p.24. Best output: the gap plus "partly addressed: Implementation
  Manual p.22 / p.24".
- **Your decision pending:** option 1, partly addressed + low gap with the quotes (recommended), or option 2, counted as
  covered (a general rule for all clauses, tested on 3.3 / 3.5 / 3.6 first).
- **Status:** analysis done; fix after the decision and the report (B1).

### T27 - Plan V1 task format and this work log (Analysis, commit d4225f3 and later)

- **What I did:** `REGUL-V5-FIX-PLAN-V1.md` in task format (what was wrong, root causes, tasks with bug, reason, fix,
  test, status), embeddings and pgvector explained; this log, rewritten task by task.
- **Status:** done; kept up to date.

### T28 - Gap check said "not covered" for partial evidence (Bug, this commit)

- **Problem:** on 3.5 the timeframe gap had no "partly addressed" note although Implementation Manual p.22 ("describe
  the duration of the activity") and p.24 ("expanding the time period for reviewing alerted transactions ... from 30
  days to 90 days") deal with it.
- **Cause (from the downloaded report):** the search was fine: the gap check searched 52 passages including p.22 and
  p.24. The AI answered "not_covered" because they are "STR narrative details" and "review periods". The prompt defined
  partial only as "meets part of it", which the AI read narrowly.
- **Fix:** the gap check now defines the three answers precisely: covered = fully meets it; **partial = deals with the
  same subject and goes part of the way** (applies it in practice, covers some elements, or states a narrower version
  such as a limited period); not_covered = nothing on the subject. For a missing definition, partial only when part of
  the definition is stated; using the term stays not_covered. A partial answer keeps the gap and shows the quote with
  it ("Partly addressed: [page] "quote""); it never removes a gap.
- **Checked against the expected results before your run:** 3.3 compliant, no gaps, not affected; 3.5 "funds" /
  "proceeds" stay not covered (documents only use the terms), timeframe becomes partly addressed with p.22 / p.24;
  3.6 predicate offence definition gets CandNM p.2 ("inside or outside the UAE") as partial, as Plan V1 expects.
- **Where:** `NdRegulGapVerifier.BuildPrompt`.
- **Status:** done; this is option 1 for the timeframe. Option 2 (count it as covered) is still your decision (A6).

### T29 - Steps 1-6 took 33 s with Azure (Bug, this commit)

- **Problem:** Steps 1-6 for 3.5 took 33 s in the downloaded report, against ~8 s on the local model.
- **Cause:** every search text (15 parts + 37 reworded variants) was embedded with its own Azure call, one after the
  other.
- **Fix:** before searching, every text a clause (or a gap check) will search is embedded in a few batched calls
  (16 per call, 4 at a time); Step 1 expansions are cached per job. Results are identical; only the calls are grouped.
- **Where:** `RegulEmbeddingRetrievalService.PrewarmQueryVectorsAsync`, `ExpandAsync`, `SearchTexts`.
- **Status:** done; expected a few seconds for Steps 1-6.

### T30 - Wrong acronym pairs produced nonsense search wording (Bug, this commit)

- **Problem:** the report shows the dictionary pairs "Money Laundering -> GPML" and "AML -> Anti-Money Laundering,
  Counter-Terrorist Financing and Sanctions Module"; 3.5 was also searched as "in order to be considered GPML, ...".
- **Cause:** acronym entries whose letters do not match their full form (harvested from documents before T7 made new
  ones inactive, or approved by mistake).
- **Fix:** on pipelines v4 / v5 an acronym is used only when its letters equal the initials of its full form (small
  words optional, hyphenated parts count, an all-capitals word gives all its letters: AML = Anti-Money Laundering,
  CFT = Combating the Financing of Terrorism, CBUAE = Central Bank of the UAE pass; GPML = Money Laundering fails).
  Synonyms are not affected; v1-v3 unchanged.
- **Where:** `RegulEmbeddingRetrievalService.IsPlausibleAcronymPair`, used in `ExpandAsync`.
- **Also for you (A7):** deactivate those two entries on the dictionary page so other screens stop showing them.
- **Status:** done.

### T31 - Page reference next to the wrong quote (Plan V2 task 1, Bug)

- **Problem:** on 3.5 "Purchase of valuable commodities" showed AML p.62 (it is p.58-59), the crypto typology
  "Implementation section 32, p.3", the last quote no page.
- **Cause:** the gap report pairs reference line i with quote i; the back end removed repeated pages (`Distinct`) and
  skipped quotes it could not ground, so later pages moved onto the wrong quotes. The gap check also split references
  on ";" while the post-processor wrote one per line.
- **Fix:** exactly one reference line per quote, in quote order, repeats kept, "page not found" for an ungrounded quote;
  the gap check adds each new quote together with its own page and keeps the lines aligned (newline separated).
- **Where:** `NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference`, `JoinReferencesPerQuote`,
  `SplitReferenceLines`; `NdRegulGapVerifier.Apply`.
- **Verified:** 2 unit tests (repeats + missing page; gap check alignment); suite 334 pass, same 21 old failures.
- **Status:** done; applies to new results on every pipeline (display bug).

### T32 - Keyword search understands word forms; pipeline v6 (Plan V2 task 2, Bug)

- **Problem:** keyword search compared exact words: "report" did not match "reported" / "reporting", "suspicion" did
  not match "suspicious".
- **Fix:** new **pipeline v6** (Admin > Analysis prompts, "v6 - v5 plus word roots and institution names (in
  progress)"); on v6 both the policy passages and the clause wording are reduced to word roots before keyword scoring
  (one suffix per word, roots of at least 3 letters, words of 3 letters or fewer such as STR / AML unchanged), and a
  query word found in more than 60% of the passages is ignored. v1-v5 unchanged.
- **Where:** `EnglishStemmer` (new), `Bm25Scorer` (stemmed mode, `Corpus.Stemmed`), `RegulEmbeddingRetrievalService`
  (`LoadPassageCorpusAsync` stems on v6), `NdRegulPipelineVersions.V6RequirementJudgment`.
- **Verified:** 13 unit tests (report / reported / reporting / reports, suspicion / suspicious, proceeds / proceeded,
  policies / policy, business / businesses, committed / commit, short words unchanged; stemmed search finds "shall be
  reported" for "report", v5 does not); suite 347 pass, same 21 old failures.
- **Status:** done; v6 = v5 + this for now (tasks 3, 5-7 extend it).

### T33 - The bank's own name for itself (Plan V2 task 3, Bug)

- **Problem:** clauses say "financial institutions" / "the institution"; the policies say "DIFC is protected ...", "UAE
  is obliged to report ...". Only a prompt rule bridged this.
- **Fix (pipeline v6):** each run detects the bank's own names in its selected documents: a capitalised name used as
  the subject of a duty ("X shall / must / will / is obliged / is required / is protected ...") at least 5 times; at most
  the 3 most frequent; people, functions, documents, regulators and authorities excluded (Employees, MLRO, FIU, CBUAE,
  DFSA, ...). A clause part with "financial institution(s)", "licensed financial institution(s)", "the institution",
  "institutions", "FI(s)" or "LFI(s)" is also searched with each name. The names are shown in the downloaded report.
- **Design change from Plan V2:** no stored list and no admin card. Names come only from the run's own documents, so
  they stay inside the workspace, need no review step and no new table. An admin list can be added later if a wrong
  name appears in a report.
- **Where:** `InstitutionNames` (new), `RegulEmbeddingRetrievalService` (`LoadedCorpus.SelfNames`, `SearchTexts`,
  `PrewarmQueryVectorsAsync`, `RetrievalPreview.InstitutionNames`), `pipeline-console-log.ts` (report line).
- **Verified:** 2 unit tests (DIFC and UAE found, FIU / CBUAE / Employees not; variants built only for institution
  terms); suite 349 pass, same 21 old failures; web build passes.
- **Status:** done; confirm with a v6 run of 3.3 (report line "Bank's own names searched ...").

### T34 - Skip the gap re-check for low-risk gaps (Feature)

- **Problem:** on 3.5 the gap re-check made 3 extra AI calls (~$0.07 of ~$0.15 for the clause) and changed nothing:
  all 3 gaps were low risk and correct.
- **Fix:** Admin > Analysis prompts > "Gap re-check: skip low-risk gaps" (on by default). On: gaps the judgment rated
  "Materiality: low" keep their result without the extra AI call; medium and high gaps are re-checked as before. Off:
  every gap is re-checked exactly as before. Skipped checks show in the report as "Gap check not run: ... skipped"
  and are not counted as AI calls.
- **Trade-off:** a false low-risk gap (definition or wording) is not removed automatically; a reviewer dismisses it.
- **Where:** `RegulWorkflowLlmSettingsService` (`IsGapCheckSkipLowEnabledAsync`, `SetGapCheckSkipLowAsync`),
  `NdRegulGapVerifier` (`IsLowMateriality`, `GapsToCheck`), `NdRegulAnalysisProcessor.VerifyGapsAsync`,
  `SystemSettingsController` (`regul-gap-check-skip-low`), admin prompts page, `pipeline-console-log.ts`.
- **Verified:** 2 unit tests (on: only medium / high checked; off: every gap checked); suite 351 pass, same 21 old
  failures; web build passes.
- **Cost:** 3.5 drops from ~$0.15 to ~$0.08 per clause.

### T35 - "SAR" detected as one of the bank's own names (Bug)

- **Problem:** the 10 Oct v6 run listed "UAE, SAR, DIFC" as the bank's names, so clauses were also searched as "SAR are
  prohibited from ...".
- **Cause:** "SAR shall be submitted ..." looks like a duty of a capitalised name.
- **Fix:** report, process and AML abbreviations (SAR, STR, CTR, CDD, EDD, KYC, PEP, UBO, TFS, NRA, MLRO, goAML, ...)
  are never taken as the bank's name.
- **Where:** `InstitutionNames.NotSelfNames`; test extended.
- **Status:** done.

---

### T36 - Excel "Identified Gaps" column showed the action text (Bug)

- **Problem:** in the 10 Oct export, the "Interpretation and expected action (Identified Gaps)" column read
  "Gap 1 - Missing: [1] Amend ... [2] Amend ..." for every partial clause: the drafted actions, all under one "Gap 1",
  and never what is actually missing. The Actions sheet was right.
- **Cause:** the export built the column from the clause's action plan text (the old engines keep their gaps there).
  On the new analysis page the gaps are stored separately, as one "[n] ..." line per gap in the "Gap analysis" field.
- **Fix:** on the new analysis page (real accounts only), the export shows what the gap analysis page shows,
  including everything users edit or resolve there. Gaps and actions are kept apart (your call, 10 Oct: actions
  belong on the Actions sheet, not in the gaps column):
  - gaps column: each gap from the page's own gap list (the AI's gap lines, or the user's edited gaps) as
    "Gap n (Risk High/Medium/Low, Pending/Resolved): <gap>", risk as saved on the page (else the AI's), Resolved once
    all its actions are resolved or the gap was resolved by hand, and the latest "Rerun this gap" result under it;
    no actions in this column;
  - Actions sheet: new "Gap #" column after "Clause #" (gap number as on the page), actions ordered by gap; it is
    kept even when the export dialog picks its own columns; status, priority, due date and owner as before;
  - status column: the page's status (manual change, or automatic "compliant" once every gap is resolved); a
    clause closed automatically lists its resolved gaps instead of the compliant note;
  - PDF: same gaps text, "What this reference fulfills", and each action prefixed with its gap number.
  Other pages, older engines and demo accounts keep the previous export (no "Gap #" column, same gaps text).
- **Where:** `gap-analysis-export-rows.ts` (`regulHybridGapsCell`, `RegulExportPageState`), `gap-analysis-export.ts`
  (`actionPlansSheet` with `withGapNo`, Excel options, PDF), `nd-gap-analysis.component.ts` (passes the page's gap
  states and evidence reviews), `analyse-regul-full-v2.component.ts` (loads them for its own export).
- **Verified:** 11 tests for this change (14 in the file pass): gaps only, no actions; resolved from actions; saved
  risk and state; latest evidence review; automatically closed clause; edited gaps; Gap # column, its order and the
  dialog column choice; other pages unchanged. Real 3.8 result: two gaps with the run's risks (Low, Medium); web
  type check clean.
- **Status:** done; confirm on the next export.

---

## 3. Test runs

| Run | Settings | Result | What it showed | Tasks |
|---|---|---|---|---|
| f8a76442, 3.3 / 3.5 / 3.6 | v3, prompt v9, local | 3.3 compliant, 3.5 partial (4 gaps), 3.6 partial | Verdicts right; asset-scope gap false; 3.3 p.14 evidence missed; ~6 min retrieval | T12, T14, T16 |
| 3.5 | v5, prompt v10, local | **Compliant** (wrong) | v10 rules let typologies cover definitions and one amount quote cover three conditions | T18 |
| 3.5 | v5, prompt v11, local | Partial 72%, 2 gaps | Definitions gap right; actions on the wrong gap; risk Medium; whole paragraph quoted in a gap | T21, T22, T23, T26 |
| 3.5 (latest) | v5, prompt v11 | Partial 70%, 3 low gaps ("funds", "proceeds", timeframe), each with its own action, Low / 45 days | All covered points right against the PDFs; judgment 50 s, gap checks 4-8 s; timeframe note missing (T26) | T17, T18, T21-T23 |
| 3.5 (latest), downloaded report | same run | 120 passages selected (114 keyword, 22 meaning matches); p.22 selected; p.24 not selected by the main search but found by the gap check; gap check answered not_covered for all 3 gaps | Steps 1-6 33 s (Azure calls one by one, T29); timeframe classed not covered although p.22 / p.24 deal with it (T28); "GPML" and long-form "AML" acronyms in the search wording (T30) | T28-T30 |
| 10 Oct, 3.4 / 3.5 / 3.7 / 3.8 / 3.9 | v6, prompt v11, Azure, gap re-check on | 3.4 partial (bearer shares, High); 3.5 partial (funds, proceeds, timeframe, Low); 3.7 partial (first limb of the TF definition, Low); 3.8 partial (illegal organisations definition, Low; NPO attention, Medium); 3.9 compliant | Shahid's question: clauses that only use "funds" / "proceeds" (3.4, 3.7, 3.8, 3.9) raised no "funds / proceeds definition" gap; each gap is that clause's own requirement. 5 of 8 gap checks failed with OpenRouter 402 (credit / in-flight budget), gaps kept. Bank names included "SAR" (T35). Duplicate manual still selected. Excel "Identified Gaps" column shows the action text (export bug) | T33, T35 |

---

## 4. Plan status

**Plan V1:** tasks 1-6, 9, 10 done (= T13-T18 above); 7 and 8 not done by decision.

**Main plan (22 points):**

| # | Point | Status |
|---|---|---|
| 1-9 | P0 bugs and no-limit | Done (T4-T10); point 8 needed no change |
| 10 | Search passages with heading path | Done (T14) |
| 11 | Stronger embedding model | Done (T25) |
| 12 | BM25 stemming, stop words, heading field | Partly: heading path searched; stemming not done |
| 13 | Concept groups + workspace self-names | Partly: 27 pairs (T15); self-names not done |
| 14 | Clause profile | Not started |
| 15 | Evidence per requirement | Not started |
| 16 | Structured judgment, verdict in code | Partly: prompt v11 (T18); verdict still from the AI |
| 17 | Gap verification | Done (T17) |
| 18 | Eval labels + retrieval recall | Partly: retrieval check (T13) |
| 19-22 | Finalize loop | Not started |

Nothing in this work changed extraction, chunking, demo accounts or pipelines v1-v3. Tests: 322 pass; the same 21 old
failures as before this work.

---

## 5. Pending tasks

The next development tasks are planned in `REGUL-V5-PLAN-V2.md` (tasks 1-12, with your answers). Recorded decisions
(09 Oct): page references fix, keyword word forms and bank self-names approved; the duplicate document is not handled
in code (the same file was selected twice under two names; select it once); the regulation clause is never summarised
or reworded anywhere; tasks 4-6 and 8-11 wait for your OK after the explanations in Plan V2.

### A. Your tests (no code)

| # | Task | Detail |
|---|---|---|
| A1 | Run 3.5 on the latest code and send the report | Pull, rebuild and restart API and web; Admin: pipeline v5, prompt v11 on all three, embedding "Azure OpenAI (current)"; new analysis with only 3.5 (or open the last 3.5 run); **Download report** in the pipeline panel; send the file |
| A2 | Run 3.3 and 3.6 | Same settings; compare with Plan V1 section 4 (3.3 compliant with AML p.42 / p.14; 3.6 partial, predicate offence definition gap) |
| A3 | Old runs | Runs before T21-T23 keep their old actions and Medium risk; delete or run the clause again |
| A4 | Azure DI price | Set `AzureDocumentIntelligence:UsdPerPage` once the Layout rate on the invoice is confirmed (T9) |
| A5 | Optional re-extract | Removes repeated page headers from documents extracted before T6 (free, no re-parse) |
| A6 | Timeframe decision | Option 1 (partly addressed + low gap) is now built (T28); say if you want option 2 (counted as covered) |
| A7 | Dictionary clean-up | Deactivate "GPML = Money Laundering" and "AML = Anti-Money Laundering, Counter-Terrorist Financing and Sanctions Module" on the dictionary page (T30 already ignores them on v4 / v5) |

### B. After the A1 report

| # | Task | Detail |
|---|---|---|
| B1 | Timeframe "partly addressed" note | Built (T28): the gap check dropped it, not the search; confirm on the next run |
| B2 | Relevance gate | Re-tune the 2.5 standard-deviation gate if the report shows too many or too few passages |
| B3 | Embedding comparison | Selected passages with Azure vs the earlier local run; record here |

### C. Main plan, not started or partly done

| # | Point | What is left | Why |
|---|---|---|---|
| C1 | 12 | Word stemming (report / reported / reporting) | Keyword search misses inflected words |
| C2 | 13 | Each workspace's own name for itself (e.g. "DIFC" in its policies) treated as "the institution" | Prompt rule covers it today; data is more reliable |
| C3 | 14 | Clause profile: requirements listed once per regulation clause, cached, reused by every run | Same requirements and numbering every run, cheaper |
| C4 | 15 | Evidence searched per requirement | Right evidence next to each requirement, smaller context |
| C5 | 16 | Structured judgment: status computed in code; oversized context split into several calls | Status always matches the gaps; no context limit |
| C6 | 18 | Expected result labels and a retrieval score per run | Measures every change before it reaches you |
| C7 | 19 | Finalize writer gets the document's own section text and terms | Inserted text matches the policy |
| C8 | 20 | Note placed from the requirement's passage | Lands in the right section |
| C9 | 21 | Corrected copy as DOCX for PDF sources | Usable corrected document |
| C10 | 22 | Re-index the corrected copy and re-check | Fixed gaps disappear |

### D. Housekeeping

| # | Task | Detail |
|---|---|---|
| D1 | Prompt v11 as default | After A1 / A2 pass, make v11 current for everyone |
| D2 | 3 old prompt-version unit tests | Outdated assertions (failing before this work) |
| D3 | 18 other old test failures | Failing before this work, to fix separately |

### Decided not to do

- Duplicate-document detection (V1 Task 7): evidence from every file is shown.
- Per-document citation check (V1 Task 8): citations stay as they are.
- Any change to extraction, chunking (no semantic chunking), demo accounts or pipelines v1-v3.
