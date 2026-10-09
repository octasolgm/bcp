# Regul V5 - Plan V2 Implementation Guide

A self-contained guide for implementing `REGUL-V5-PLAN-V2.md` in a new Claude Code session that has **no memory of
the earlier work**. Give the new session this file first (section 0 prompt), then one task prompt at a time
(section 4). Every task prompt is copy-paste ready.

Read with: `REGUL-V5-PLAN-V2.md` (what and why), `REGUL-V5-WORK-LOG.md` (what is already built, tasks T1-T30),
`CLAUDE.md` (project rules), `REGUL-V5-FIX-PLAN-V1.md` section 4 (expected results for 3.3 / 3.5 / 3.6).

---

## 0. Start prompt (paste this first in the new session)

```
You are continuing work on the V5 regulatory analysis (/nd/analyse-regul-full-v2) in this repo, branch
feature/regul-clause-context-and-evals. Before doing anything:

1. Read CLAUDE.md, docs/pipeline/REGUL-V5-PLAN-V2.md, docs/pipeline/REGUL-V5-PLAN-V2-IMPLEMENTATION.md (this
   guide) and docs/pipeline/REGUL-V5-WORK-LOG.md (sections 1, 2 for T14-T30, 5). Do not read the whole repo.
2. Follow the ground rules in section 1 of the implementation guide for every change.
3. Set up and record the test baseline as in section 2 of the guide. Tell me the build and test results.
4. Do NOT start any task until I paste its task prompt. Do one task at a time. After each task: build, run the
   tests (compare with the baseline), update the work log and the weekly report, commit and push, then stop and
   report to me in plain words: what changed, where, how you checked it, what I should test.
```

---

## 1. Ground rules (every task)

1. **The regulation clause is never summarised or reworded.** The AI always receives the full clause text. Any list of
   a clause's points uses the clause's own exact words (verbatim substrings, checked in code).
2. **Extraction and chunking do not change.** `LocalSectionSplitter` (sections) and `LocalPassageSplitter` (search
   passages) stay as they are.
3. **Demo accounts are not touched** (CLAUDE.md): no change to demo seed data, `NdDemoInterceptionService`,
   `NdDemoWorkspaceService`, `AnalysisBundleSeedService`, `DemoAnalysisSeedService`, or how demo results render.
   Front-end changes in shared code must be no-ops for demo data.
4. **Workspaces (CLAUDE.md):** every new table holding client data implements `ITenantScoped` and is added to
   `bcp-api/Infrastructure/NdWorkspaceSchemaBootstrap.cs` (column, backfill, insert trigger); new tables are created in
   `NdIncrementalSchemaBootstrap.PatchSql`. Raw SQL and in-memory caches must filter by tenant themselves. Hosted
   workers run unscoped: set `TenantId` explicitly from the parent row.
5. **Switchable, never replacing:** retrieval/judgment changes go into a new **pipeline v6**
   (`NdRegulPipelineVersions`, add `V6RequirementJudgment = 6`, label and description, `IsKnown`). Pipelines v1-v5 and
   prompts v9-v11 keep working unchanged. Exception: task 1 is a display bug fix and applies to all versions.
6. **Check before the user pays:** each judgment change is scored with the answer keys (task 4) for 3.3, 3.5, 3.6. Each
   prompt rule is checked against the expected results in `REGUL-V5-FIX-PLAN-V1.md` section 4 and the answer keys,
   rule by rule, and the check is written in the work log. (Prompt v10 failed because this was skipped.)
7. **No count limits** (user decision): relevance decides how many passages are used; never add "top N" caps.
8. **Docs after every task:** add the task to `docs/pipeline/REGUL-V5-WORK-LOG.md` (overview table row + a section
   with Problem / Cause / Fix / Where / Verified / Status / Commit, same format as T1-T30) and update section 5
   (pending). Update the current week's `docs/weekly-reports/<week>/summary.md` and `detail.md` (CLAUDE.md rules: no
   em dashes, no smart quotes, detail.md client-facing with no file names).
9. **Commits:** small, one per task, clear message; push with `git push -u origin feature/regul-clause-context-and-evals`.
10. **Tests:** add unit tests for every pure function (xUnit, `bcp-api/tests/Reguliq.Api.Tests`). Test fixtures that use
    the EF in-memory provider must ignore pgvector columns (see `AiCostingTests.InMemoryAppDbContext`).

---

## 2. Environment, build and test commands

### Local machine (Windows / Mac with .NET 8 and Node installed)

```bash
git fetch origin feature/regul-clause-context-and-evals
git checkout feature/regul-clause-context-and-evals
git pull origin feature/regul-clause-context-and-evals

# API
cd bcp-api
dotnet build
dotnet test tests/Reguliq.Api.Tests 2>&1 | tail -3

# Web
cd ../bcp-web
npm ci
npx ng build --configuration development
```

### Cloud Claude Code session (Linux container)

```bash
# .NET 8 SDK if missing (the dot.net install script may be blocked by the proxy; apt works)
dotnet --version || (sudo apt-get update && sudo apt-get install -y dotnet-sdk-8.0)
```

If `dotnet build` fails while the SmartComponents.LocalEmbeddings package downloads the bge-micro model from
HuggingFace (HTTP 403 behind the proxy), create empty placeholder files at the exact paths named in the error (under
`~/.nuget/packages/smartcomponents.localembeddings/<version>/.cache/`) and build again. The local embedding model then
cannot run in that container, which is fine for building and unit tests.

Karma tests in `bcp-web` do not run in this repo (vitest imports); check front-end logic with a small esbuild script
in the scratchpad if needed, and always run `npx ng build --configuration development`.

### Test baseline (record before task 1)

```bash
cd bcp-api
dotnet test tests/Reguliq.Api.Tests -nologo -v q 2>&1 | grep "\[FAIL\]" | sed 's/.*Reguliq.Api.Tests\.//; s/ \[FAIL\]//' | sort > /tmp/baseline-fail.txt
dotnet test tests/Reguliq.Api.Tests -nologo -v q 2>&1 | tail -1
```

Expected on 09 Oct 2026: **343 tests, 322 pass, 21 fail**. The 21 failures existed before this work:

```
AppDbContextTests.JsonColumns_AreMappedToJsonb
DualVerifyAgreementServiceTests.Compare_LargeConfidenceGap_ReturnsConfidenceGap
GovPointExtractNormalizerTests.DedupeAndFilter_keeps_longest_text_per_point_number
GovPointExtractNormalizerTests.DedupeAndFilter_nests_distinct_siblings_with_same_number
GovPointExtractNormalizerTests.PlanRepair_soft_deletes_duplicates_and_junk
GovPointsServiceTests.ResolveSelectedPoints_FindsExpandedLeafIds
LocalSectionSplitterTests.A_whole_contents_page_is_skipped_including_its_last_entry_and_what_follows_it
LocalSectionSplitterTests.Annex_heading_still_namespaces_its_own_numbering
LocalSectionSplitterTests.Nested_list_under_multi_level_clause_stays_with_parent_TFS_3_4_case
NavCountsQueryTranslationTests.CapturedQueryableInProjection_IsNotComposedIntoSql
NavCountsQueryTranslationTests.RunStatusTally_IncludingDeletedBin_TranslatesToSql
NavCountsQueryTranslationTests.StoredDocumentBins_TranslateToOneGroupedStatement
NdAnalysisPromptVersionServiceTests.BuildJudgmentContextAsync_uses_current_db_version_not_hardcoded_default
NdAnalysisPromptVersionServiceTests.GetCurrentTextAsync_throws_when_no_current_judgment_version
NdAnalysisPromptVersionServiceTests.SetCurrentAsync_switches_text_used_on_next_build
NdCbuaeSection5LandingAiPatchTests.ApplyMissing_inserts_section_5_rows_when_absent
NdRegulJudgmentFormatterTests.Clause_3_2_gap_analysis_matches_seed_interpretation
NdRegulPolicyContextServiceTests.BuildContextForClause_retrieves_keyword_chunks_when_pages_gt_50
PdfGroundedMarkdownBuilderTests.TryGround_injects_real_pdf_page_markers_into_landing_text
PolicyClauseExtractNormalizerTests.DedupeClauseNumbers_avoids_1_dot_dash_1_ids
PolicyPageResolverTests.Resolve_PrefersQuoteLastMatch_AndIgnoresUuidSection
```

After each task: no test outside this list may fail; new tests must pass.

### How the user tests a run

1. Pull, rebuild and restart API and web.
2. `/nd/admin/prompts`: Retrieval pipeline version, judgment prompts, Search embedding model (Azure OpenAI).
3. `/nd/analyse-regul-full-v2`: new analysis, regulation CBUAE guidance, clause 3.5 (and 3.3, 3.6), the internal
   documents selected **once each** (Document.pdf is the same file as the Implementation Manual: select only one).
4. In the pipeline panel: **Download report** (and the score from task 4 once built). The report is the main input
   for checking a run.

---

## 3. Key code map (where things are)

| Area | Files |
|---|---|
| Pipeline versions | `bcp-api/Services/NewDashboard/NdRegulPipelineVersions.cs`; admin setting in `RegulWorkflowLlmSettingsService.GetPipelineVersionAsync` |
| Steps 1-6 (expansion, split, BM25, embedding, fusion, selection) | `RegulEmbeddingRetrievalService.cs` (`RunRetrievalAsync`, `BuildPreviewAsync`, `ScoreQueryAsync`, `ExpandAsync`, `SearchTexts`, `PrewarmQueryVectorsAsync`, `SearchRelevantInMemoryAsync`, `SelectFused`, `CreateEvidenceSessionAsync` / `SearchEvidenceAsync`), `SubObligationSplitter.cs`, `Bm25Scorer.cs`, `HybridFusionSelector.cs`, `DictionaryExpansionService.cs` (`bcp-api/Services/LocalDocs`) |
| Search passages | `LocalPassageSplitter.cs`, `NdPassageIndexService.cs`, `PassageEmbeddingService.cs`, table `nd_local_document_passages`, worker `Workers/PassageBackfillHosted.cs` |
| Step 7 (context) and Step 8 (judgment) | `NdRegulAnalysisProcessor.cs` (`PrepareForwardJudgmentAsync`, `ExecuteForwardJudgmentAsync`, `ExecuteForwardJudgmentCoreAsync`, `RecordFinal`), `NdRegulPolicyContextService.cs` (`PolicyChunk`), `RegulWorkflowLlmService.cs` |
| Step 9 (post-processing, saving) | `NdRegulJudgmentPostProcessor.cs` (`ApplyQuoteVerification*`, `ApplyGroundedDocumentReference`, `ApplyStatusConsistency`, `VerifyQuote`), `NdRegulJudgmentFormatter.cs` (`FormatLandingMessage`), `NdRegulJudgmentModels.cs` (`RegulJudgmentResult`), `NdRegulLlmSchemas.cs` |
| Gap check (v5) and gap numbering | `NdRegulGapVerifier.cs` (`BuildPrompt`, `Decide`, `Apply`, `NormalizeGapNumbering`), `NdRegulAnalysisProcessor.VerifyGapsAsync` |
| Prompts | `NdRegulPromptDefaults.cs` (v9-v11 texts), `NdAnalysisPromptVersionService.cs` (seeding, `EnsureJudgmentSemanticV11Async`, keys `regul_judgment_system`, `regul_judgment_user_context`, `regul_judgment_user_query`) |
| Traces (per clause, for the report) | `Data/NewDashboard/RegulClauseTraceEntities.cs` (`RegulClauseTraceSteps`), endpoint `GET nd/analysis-runs/{id}/clause-traces`, retrieval preview in `GET nd/analysis-runs/{id}/status` (`AnalysisRunsController`) |
| Evals and retrieval check | `Controllers/NewDashboard/EvalsController.cs`, `Services/NewDashboard/NdAnalysisEvalService.cs`, `Data/NewDashboard/NdAnalysisEvalEntities.cs` (`NdRetrievalExpectation`), front end `nd-retrieval-check-drawer.component` |
| Dictionary | `DictionaryExpansionService.cs`, entities `NdSynonymEntry.cs`, `NdDictionaryEntry.cs`, seed `SeedData/synonym-seed.json`, dictionary page in `bcp-web/src/app/pages/nd/` |
| Front end, V5 page and panel | `analyse-regul-full-v2.component.ts`, `nd-pipeline-progress-panel.component.*`, `nd-pipeline-panel.service.ts`, `src/lib/nd/pipeline-console-log.ts` (report: `buildClauseReport`, `downloadClauseReport`, `reportSourceFromServer`) |
| Gap report | `nd-gap-point-detail.component.*`, `src/lib/nd/policy-doc-resolve.ts` (`buildPolicyExtractBlocks` pairs document reference line i with quote i), `src/lib/nd/cap-gap-count.ts`, `src/lib/nd/action-plan-seed.ts` |
| Finalize (corrected copy) | `Services/NewDashboard/NdCorrectedDocumentService.cs`, `Services/NewDashboard/CorrectedDocs/NdFinalizeEmbedContentService.cs`, `CorrectedDocs/NdActionPlanEmbedResolver.cs`, `Services/Llm/FinalizeEmbedLlmService.cs` |
| Schema / tenancy | `Infrastructure/NdIncrementalSchemaBootstrap.cs`, `Infrastructure/NdWorkspaceSchemaBootstrap.cs`, `Data/AppDbContext.cs` |

---

## 4. Tasks (one prompt each, in this order)

Status of the user's approval (09 Oct 2026): tasks 1, 2, 3 approved; tasks 5, 6, 7 approved ("do that, explain what
you change"); task 4 explained, confirm with the user before building; tasks 8-11 need the user's OK (task 8 must
never touch the regulation text).

### Task 1 - Page reference next to the right quote (approved)

**Problem:** on the 3.5 run, "Purchase of valuable commodities" shows AML Manual p.62 (it is p.58-59), the crypto
typology shows "Implementation Manual section 32, p.3", the last quote has no page. The front end pairs reference line
i with quote i (`buildPolicyExtractBlocks`). The back end builds the reference lines with `refs.Distinct(...)` and skips
quotes it cannot ground (`NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference`), so the lists drift apart.
Also `NdRegulGapVerifier.Apply` splits `DocumentReference` on `;` and joins with `"; "`, while the post-processor joins
with `\n`: mixed separators; and it adds references and quotes with separate de-duplication, so it can drift too.

**Prompt:**
```
Task 1 of Plan V2: page reference next to the right quote. Read the Task 1 section of
docs/pipeline/REGUL-V5-PLAN-V2-IMPLEMENTATION.md.

1. In NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference: produce exactly one reference line per
   policy_extract quote, in the same order, no Distinct. A quote that cannot be grounded gets the line
   "<document name if known> - page not found" (or "page not found"). Join with "\n".
2. In NdRegulGapVerifier.Apply: treat DocumentReference as newline-separated lines aligned with PolicyExtract;
   when adding a covered/partial quote, append the quote and its reference line together (skip both only when the
   same quote is already present). Never split on ';'.
3. Check every other place that writes DocumentReference for V5 results (grep DocumentReference in
   Services/NewDashboard) and keep them aligned the same way. Do not change the demo seed data or how demo
   results render.
4. Unit tests: quotes [A on p.59, B on p.59, C not found, D on p.62] give 4 lines in order; Apply keeps alignment.
5. Build, test against the baseline, work log task T31, weekly report, commit, push, report to me.
```

**Done when:** each quote in a new run's Policy extract shows its own page; tests pass.

### Task 2 - Keyword search understands word forms (approved)

**Problem:** `Bm25Scorer.Tokenize` compares exact lower-case words; "report" does not match "reported" / "reporting",
"suspicion" does not match "suspicious".

**Prompt:**
```
Task 2 of Plan V2: keyword search with word roots, pipeline v6 only. Read the Task 2 section of the
implementation guide.

1. Add pipeline v6 to NdRegulPipelineVersions (V6RequirementJudgment = 6, label "v6 - requirement-based
   judgment", description listing what v6 adds; IsKnown; the admin pipeline selector lists it). Until tasks 5-7
   land, v6 behaves like v5 plus the task 2 / 3 changes.
2. Add a small, conservative English stemmer (static class, e.g. Services/NewDashboard/EnglishStemmer.cs,
   Porter-style suffix rules) with unit tests: report/reported/reporting -> same root; suspicion/suspicious ->
   same root; transaction/transactions -> same root; short words (<= 3 letters) and all-caps acronyms (STR, AML)
   unchanged.
3. Bm25Scorer: an optional "stem" mode used for both BuildCorpus and Score; add a small domain stop list for that
   mode only ("shall", "must", "bank", "policy", "procedure" are NOT removed if they carry meaning: only remove
   words that appear in more than ~60% of passages, computed from the corpus, not a fixed list).
4. RegulEmbeddingRetrievalService: use stem mode only when pipelineVersion >= 6 (corpus built per version; v1-v5
   unchanged).
5. Tests, build, baseline, work log T32, weekly report, commit, push, report.
```

**Done when:** on v6 a passage that says "shall be reported" scores for a clause that says "report"; v1-v5 identical.

### Task 3 - The bank's own name for itself (approved)

**Problem:** clauses say "financial institution(s)", "the institution", "FIs", "licensed financial institution";
policies say "DIFC is protected ...", "UAE is obliged to report ...", "the Bank". Only a prompt rule bridges this.

**Prompt:**
```
Task 3 of Plan V2: each workspace's own name for itself. Read the Task 3 section of the implementation guide and
CLAUDE.md (workspaces).

1. New tenant-scoped table nd_workspace_self_names (Id, TenantId, Name, Source "auto"|"manual", IsActive,
   Occurrences, CreatedAt, UpdatedAt). Add it to NdIncrementalSchemaBootstrap.PatchSql, NdWorkspaceSchemaBootstrap
   (parent: none, backfill Default workspace) and AppDbContext with the ITenantScoped filter.
2. Detection (after indexing, in IndexingWorkerHosted or a small service it calls): count capitalised names used
   as the subject of duties in the document's sections (regex like
   \b([A-Z][A-Za-z&.]{1,30}(?:\s[A-Z][A-Za-z&.]{1,30}){0,3})\s+(?:shall|must|is obliged to|is required to|will|is protected)\b),
   exclude generic words (The, This, Each, Staff, Employees, Customer, Management, ...). Names seen >= 5 times
   across the workspace's documents become "auto" candidates with IsActive = false. Set TenantId from the
   extraction row (the worker is unscoped).
3. Dictionary page: a small "Institution names" card: list, confirm (IsActive = true), delete, add manually.
   Platform admins and workspace admins only.
4. Expansion (pipeline v6 only, in RegulEmbeddingRetrievalService.ExpandAsync): when a clause part contains an
   institution term (financial institution(s), the institution, FI(s), licensed financial institution(s),
   the bank, banks), add reworded variants with each active self-name of the run's workspace. Read the names
   once per job; filter by the run's TenantId explicitly (cache must be tenant-safe).
5. Tests (detection regex on sample text; variants built), build, baseline, work log T33, weekly report, commit,
   push, report. Tell me which names were detected on my documents after I re-index or run the detection.
```

**Done when:** on v6, clause 3.3's search includes wording with "DIFC" and selects AML Manual p.42 / p.14.

### Task 4 - Answer keys and automatic scoring (confirm with the user first)

**What it is (for the user):** the correct result of a test clause written once; after every run the system compares
the run with it and shows a score (status right / wrong, expected gaps found, extra gaps, expected pages sent to the AI).

**Answer keys to load (from the PDFs, agreed with the user):**

| Clause | Expected status | Expected covered (page) | Expected gaps (match words) | Must not be a gap |
|---|---|---|---|---|
| 3.3 | compliant | good-faith protection (AML Manual p.42, p.14) | none | reporting deadline, escalation path |
| 3.5 | partial | ML acts (AML p.6); independent offence (AML p.6, CandNM p.2); no proof of predicate (AML p.6, CandNM p.2); any asset (typologies AML p.58-62 / Implementation p.45-50); amount irrelevant (Implementation p.4) | "funds" definition [funds]; "proceeds" definition [proceeds]; timeframe [timeframe, period] (partly addressed: Implementation p.22 / p.24) | crypto / virtual assets, amount / threshold, independent offence |
| 3.6 | partial | NRA findings considered (AML p.30-32); categories of risk (AML 7.5 p.29) | predicate offence definition [predicate, felony, misdemeanour] (partly: CandNM p.2 inside or outside the UAE) | - |

**Prompt:**
```
Task 4 of Plan V2: answer keys and automatic scoring. First explain to me in 5 lines what you will build and wait
for my OK. Read the Task 4 section of the implementation guide, EvalsController, NdAnalysisEvalService and
NdRetrievalExpectation (the retrieval check already stores expected snippets per clause; extend that area).

1. New tenant-scoped table nd_eval_answer_keys (Id, TenantId, ClauseNo, ClauseTextHash, ExpectedStatus,
   ExpectedCoveredJson [{label, pages[]}], ExpectedGapsJson [{label, matchWords[], partialPages[]}],
   ForbiddenGapsJson [{label, matchWords[]}], UpdatedAt, UpdatedBy), keyed by clause number + hash of the clause
   text (whitespace-normalised).
2. Scoring (pure function, unit-tested): input = saved finding (status, gap lines, covered lines, document
   references) + retrieval record (selected passages with document and page) + answer key. Output: status right;
   each expected gap found (any match word in a gap line, case-insensitive); extra gaps (gap lines matching no
   expected gap); forbidden gaps raised; expected pages sent to the AI (doc name + page in the selected list);
   one overall score.
3. Endpoints (platform admins): GET/PUT answer key by clause; GET score for a run (all clauses with a key).
4. Front end: answer key editor in the retrieval check drawer (simple form per clause); a "Score" block at the
   top of the downloaded pipeline report and in the pipeline panel when the run has keys.
5. Seed the three answer keys in the guide table for the Default workspace only if absent (clause numbers 3.3,
   3.5, 3.6; match by clause number when the hash differs, and log it).
6. Tests, build, baseline, work log T34, weekly report, commit, push, report. Then score my last 3.5 run.
```

**Done when:** the last 3.5 run shows a score; the user can edit keys without code.

### Task 5 - Requirement list per clause, saved and reused (approved)

**Design:**
- One AI call per clause (pipeline v6) lists its requirements. Each requirement:
  `{ "id": "R1", "clause_words": "<verbatim substring of the clause>", "type": "duty|prohibition|defined_term|scope|protection|penalty|context", "note": "<optional, max 15 words, never a rewording of the requirement>" }`.
- **Code validation (mandatory):** every `clause_words` must be a verbatim substring of the clause text after
  whitespace normalisation; otherwise retry once with the failing items named; if it still fails, fall back to
  `SubObligationSplitter` parts as requirements (type "duty") and log it. Requirements must together cover every
  sentence of the clause except context-only sentences (check: each clause sentence overlaps at least one
  clause_words or is typed context).
- Saved per workspace in a new tenant-scoped table `nd_regul_clause_requirement_sets` (Id, TenantId, ClauseNo,
  ClauseTextHash, RequirementsJson, Source "ai"|"admin", PromptKey, PromptVersion, CreatedAt, UpdatedAt, UpdatedBy).
  Reused by every v6 run of the same clause text; regenerated only if the clause text changes or an admin clears it.
- New prompt key `regul_requirement_list_system` (version 1, seeded current, editable in Admin > Analysis prompts).
- Shown in the pipeline panel (new step "Step 2b - Requirements") and in the report; platform admins can edit the
  list (JSON editor is enough) and the edit is validated the same way.

**Prompt text to seed (`regul_requirement_list_system` v1):**
```
You list the requirements of ONE regulatory clause. Output only JSON: {"requirements":[{"id":"R1","clause_words":"...","type":"...","note":""}]}.
Rules:
- clause_words is copied VERBATIM from the clause: the shortest continuous part of the clause that states the
  requirement fully. Never reword, shorten inside, summarise or merge separate sentences.
- One requirement per distinct thing the clause requires, defines or states. A list introduced by "such as",
  "including", "for example" or "not limited to" is ONE requirement together with its lead-in.
- Each term the clause formally defines ("X means", "defined as") is its own requirement, type defined_term.
- A sentence listing several distinct conditions (for example size, timeframe and nature) is one requirement per
  condition when each needs its own evidence; use the clause words of that condition with enough context.
- type: duty, prohibition, defined_term, scope (what a rule extends to or what is irrelevant), protection
  (a right or protection granted by law), penalty, context (background, statistics, disclaimers; nothing is
  expected in the institution's documents).
- Every sentence of the clause belongs to at least one requirement or is context.
- Do not add requirements from other clauses, laws or practice.
```

**Expected list for 3.5 (use as a unit-test fixture and in the answer key check):** ML acts with knowledge (duty /
scope); "funds" definition (defined_term); "proceeds" definition (defined_term); not only money, any tangible or
intangible asset incl. the illustrative list (scope); size or monetary value irrelevant (scope); timeframe irrelevant
(scope); nature of funds irrelevant (scope); ML a criminal offence, prosecution independent (scope); suspicion without
proof of predicate, inferred from indicators (scope); NRA 2018 sentence (context).

**Prompt:**
```
Task 5 of Plan V2: saved requirement list per clause (pipeline v6). Read the Task 5 section of the implementation
guide, ground rule 1 (never reword the clause) and CLAUDE.md (workspaces).

1. Table nd_regul_clause_requirement_sets as described (tenant-scoped, schema bootstrap, AppDbContext).
2. Service NdRegulClauseRequirementService: GetOrCreateAsync(tenant, clauseNo, clauseText) -> requirement list;
   one AI call with prompt key regul_requirement_list_system (seed v1 with the prompt text in the guide; validate
   placeholders like the other prompt keys); strict JSON parse; verbatim validation; one retry; fallback to
   SubObligationSplitter; trace step "requirements" (RegulClauseTraceSteps) with request, response and the
   validation result.
3. Call it in RunRetrievalAsync / RunRetrievalForFindingAsync when pipelineVersion >= 6, before Steps 3-6. Store the
   requirement ids and clause words in the retrieval record (RetrievalPreview gains Requirements).
4. Front end: pipeline panel step "Step 2b - Requirements" (list per clause), the report prints the list,
   platform admins can edit the list for a clause (validated server-side with the same verbatim check).
5. Unit tests: verbatim validation (accepts copied text with different whitespace, rejects reworded text),
   coverage check, fallback; the 3.5 fixture list above passes validation against the real 3.5 clause text
   (copy the clause text from REGUL-V5-WORK-LOG.md or the run report).
6. Build, baseline, work log T35, weekly report, commit, push, report. Show me the list generated for 3.5.
```

**Done when:** a v6 run of 3.5 shows ~10 requirements, all verbatim, the same list on the next run.

### Task 6 - Evidence searched per requirement (approved)

**Design:**
- v6 searches each requirement's `clause_words` (with Step 1 expansion, equivalent terms, word roots, self-names) as its
  own query, through the same BM25 + embedding + relevance gate (no count limit). `context` requirements are not
  searched.
- The retrieval record keeps the passages per requirement: `RequirementEvidence: [{requirementId, passages:[{id,
  doc, page, score}]}]`, plus the union for the existing panel views.
- Step 7 (v6) builds the context grouped by requirement. Each passage gets a stable evidence id (E1, E2, ...) once;
  a passage relevant to several requirements is printed once (under its first requirement) and referenced by id under
  the others. Each evidence id maps to document, section and page (stored in the Step 7 trace `chunksJson`).
- Context layout:
```
REQUIREMENT R6 (scope): "the timeframe during which it took place ... are irrelevant to the suspicion and reporting"
Evidence: E14, E15, E3 (see R2)
[E14] internal -Implementation of AML CFTPF Manual (1) - 17.2 p.22
Heading: ... > 17.2 Best Practices for Drafting an STR or SAR
If the activity takes place over a period of time, ...
```

**Prompt:**
```
Task 6 of Plan V2: evidence per requirement (pipeline v6). Requires task 5. Read the Task 6 section of the
implementation guide.

1. RegulEmbeddingRetrievalService: for v6, search each non-context requirement's clause_words as its own query
   (reuse ExpandAsync, SearchTexts, PrewarmQueryVectorsAsync, SearchRelevantInMemoryAsync; same relevance gate,
   no count limit); keep hits per requirement; save RequirementEvidence in the retrieval record; keep the union
   in FusedMatches for the existing panel.
2. NdRegulAnalysisProcessor.PrepareForwardJudgmentAsync: for v6 build the grouped context exactly as in the
   guide (evidence ids, each passage printed once); store the evidence id -> document/section/page map in the
   Step 7 trace chunksJson and on the ForwardJudgmentPrep for task 7.
3. Report and panel: list evidence per requirement.
4. Unit tests for the context builder (ids stable, passage printed once, references for shared passages).
5. Build, baseline, work log T36, weekly report, commit, push, report with the context size before (v5) and after
   (v6) for 3.5.
```

**Done when:** the 3.5 v6 report shows evidence per requirement, the timeframe requirement lists Implementation p.22 /
p.24, and the context is smaller than v5's ~156k characters (the duplicate manual selected once).

### Task 7 - AI judges each requirement, code does the counting (approved)

**Design:**
- New prompt keys (version 1, seeded current, editable): `regul_requirement_judgment_system`,
  `regul_requirement_judgment_user` (placeholders `{clause_no}`, `{clause_text}`, `{requirements}`, `{evidence}`).
  Pipeline v6 uses these; v1-v5 keep using `regul_judgment_*` (v9-v11).
- AI output (strict JSON):
```
{"clause_type":"...","requirements":[{"id":"R6","status":"covered|partial|not_covered",
  "evidence_ids":["E14"],"quote":"<verbatim from that evidence>","reason":"<one sentence>",
  "missing":"<what the documents lack, if partial/not_covered>",
  "draft_wording":"<exact policy sentence to add, if partial/not_covered>","target_section":"<section from the evidence or Policy>"}],
 "interpretation":"<short>"}
```
- The prompt carries the v11 rules that proved right (clause types; defined term covered only by a stated or adopted
  definition; scope covered by practice; illustrative lists and named parties are one requirement; each condition needs
  its own evidence; partial = same subject, part of the way) and the instruction to answer **every requirement id
  exactly once** and never add requirements.
- **Code (bookkeeping only, no judgment of meaning):**
  - verify each quote is verbatim in the named evidence (`VerifyQuote`); covered with an unverified quote becomes
    `not_covered` with reason "quote not verified" (the gap check then re-checks it); partial with an unverified quote
    keeps partial without quote;
  - a requirement missing from the answer: one retry naming the missing ids; still missing -> not_covered "not answered";
  - status: all non-context covered -> compliant; none covered or partial -> non_compliant; else partial;
  - covered_elements: `[n] <clause words, cut at 25 words> - Covered: [<evidence label>]`;
  - gaps: one per partial / not_covered requirement, numbered 1..n:
    `[n] <clause words, cut> (clause: "<clause words, cut>") - Missing: <missing> - Materiality: <m>`;
    partial adds `Partly addressed: [<label>] "<quote>"`;
  - materiality from type: duty / prohibition / protection / penalty = high when not covered, medium when partial;
    defined_term / scope = low; context never a gap;
  - suggested_action: `[n] Amend <target_section> to include: "<draft_wording>"` aligned with the gap numbers;
  - policy_extract and document_reference: one line each per verified quote, aligned (task 1), labels from the
    evidence id map;
  - the result is saved as the existing `RegulJudgmentResult`, so the gap report, actions and demo stay unchanged.
- Gap check (v6): per not_covered / partial requirement, search its clause words + missing text over all passages
  (existing `SearchEvidenceAsync`) and ask the existing gap check question; apply as today.
- Oversized context: if the grouped context exceeds the warning size, split the requirements into groups (each with its
  own evidence) and make one call per group; merge per requirement id.

**Prompt:**
```
Task 7 of Plan V2: per-requirement judgment (pipeline v6). Requires tasks 5 and 6. Read the Task 7 section of the
implementation guide, NdRegulPromptDefaults (v11 rules), NdRegulGapVerifier and NdRegulJudgmentPostProcessor.

1. Seed prompt keys regul_requirement_judgment_system / regul_requirement_judgment_user (v1, current) with text
   built from the v11 rules listed in the guide plus the per-requirement output contract. Before writing code,
   check each rule against the answer keys for 3.3, 3.5, 3.6 and write that check in the work log.
2. Pure class NdRegulRequirementJudgment: parse the JSON answer, verify quotes against the evidence map, compute
   status / gaps / actions / covered elements / extract + references exactly as in the guide, produce a
   RegulJudgmentResult. Unit tests with fixtures: all covered -> compliant; a covered answer with an invented quote
   -> not_covered; a missing id -> retry list; numbering 1..n; materiality per type; references aligned.
3. NdRegulAnalysisProcessor: v6 path uses the new prompt keys and NdRegulRequirementJudgment; gap check per
   requirement; oversized-context split by requirement groups; traces for every call (report shows them).
4. Admin > Analysis prompts lists the new keys; the pipeline v6 description says it uses them.
5. Build, baseline, work log T37, weekly report, commit, push, report. Then I run 3.3, 3.5, 3.6 on v6 and you score
   them with the answer keys (task 4) and compare with v5 / v11.
```

**Done when:** v6 scores at least as well as v5 / v11 on all three answer keys, and status, gaps, actions and pages
always agree.

### Task 8 - Finalize writer reads the bank's own policy section (needs the user's OK)

**Clarification for the user:** this never touches or summarises the regulation clause; it only concerns the sentence
inserted into the bank's policy.

**Prompt:**
```
Task 8 of Plan V2 (only after my OK): the finalize writer gets the target policy section. Read
NdCorrectedDocumentService, NdFinalizeEmbedContentService, NdActionPlanEmbedResolver, FinalizeEmbedLlmService.
First tell me how the writer is called today and what it receives, and propose the change in 5 lines; wait for my
OK. The change: pass the target section of the policy (verbatim, from the evidence passage or the section named in
the action) and the bank's own terms (self-names from task 3, terms used in that section) to the writer prompt so
the inserted wording fits the manual. Never send a summary of the regulation clause. Tests, build, baseline, work log,
weekly report, commit, push, report.
```

### Task 9 - Inserted text placed where the evidence is (needs the user's OK)

```
Task 9 of Plan V2 (only after my OK): placement from the evidence passage. Requires task 6. Read the finalize
services listed in task 8. First describe how placement is chosen today and propose the change; wait for my OK.
The change: when a gap's requirement has evidence (or partial evidence) in the target document, place the inserted
text after that passage (its section and page are known from the evidence id); otherwise keep today's logic. Tests,
build, baseline, docs, commit, push, report.
```

### Task 10 - Corrected copy as DOCX for PDF policies (needs the user's OK)

```
Task 10 of Plan V2 (only after my OK): DOCX corrected copy for PDF sources. Read NdCorrectedDocumentService and how
DOCX sources are corrected today. Propose how to build a DOCX from the PDF's parsed text (headings and paragraphs
from the stored sections) with the inserted wording marked (highlight or tracked insertion); wait for my OK.
Tests, build, baseline, docs, commit, push, report.
```

### Task 11 - Re-check after finalizing (needs the user's OK)

```
Task 11 of Plan V2 (only after my OK): re-index the corrected copy and re-check finalized clauses. Read the finalize
services, IndexingWorkerHosted and the clause rerun path in NdRegulAnalysisProcessor. Propose: store the corrected
copy as a new version of the internal document, extract and index it (sections + passages), then run the clause
rerun (Steps 1-9) for the finalized clauses against that version; a gap whose requirement is now covered closes.
Wait for my OK. Tests, build, baseline, docs, commit, push, report.
```

### Task 12 - Housekeeping (any time)

```
Housekeeping from Plan V2 task 12:
1. Fix the 21 baseline unit test failures one by one (each is old; find whether the test or the code is outdated;
   never weaken a test that checks real behaviour; ask me when unsure). Commit per group.
2. Remind me to set AzureDocumentIntelligence:UsdPerPage once the Layout rate is confirmed (log-only cost).
3. After task 6, check in the reports whether evidence counts per requirement look right; re-tune the relevance
   gate only if the answer-key scores say so.
Work log and weekly report as usual.
```

---

## 5. Order and checkpoints

| Order | Task | Checkpoint (user) |
|---|---|---|
| 1 | Task 1 | Run 3.5 on v5: every quote has its own page |
| 2 | Task 4 (after OK) | Score of the last 3.5 run shown |
| 3 | Tasks 2, 3 | Run 3.3 on v6: DIFC passages selected; scores not lower |
| 4 | Task 5 | 3.5 requirement list (verbatim, ~10 items, stable) |
| 5 | Task 6 | 3.5 evidence per requirement; smaller context |
| 6 | Task 7 | 3.3 / 3.5 / 3.6 on v6 scored >= v5 / v11; then make v6 the recommended version |
| 7 | Tasks 8-11 (after OK) | Finalize one 3.5 gap and check the corrected copy |
| any | Task 12 | - |

Effort: about 12-15 working days for tasks 1-11.

## 6. If something goes wrong

- A run gives a strange result: download the report (pipeline panel) and compare with the answer key before changing
  any prompt.
- A prompt change: check every rule against the three answer keys first and write the check in the work log.
- A schema change: run the API once against a local database and confirm the table, the `tenant_id` column and the
  insert trigger exist (`\d nd_<table>` in psql).
- Never push a change that alters demo accounts, extraction or chunking, or pipelines v1-v5 behaviour (other than the
  task 1 display fix).
