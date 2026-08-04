# Regul workflow V3 — Code map (BCP)

Companion to [`REGUL-AI-WORKFLOW.md`](REGUL-AI-WORKFLOW.md). Same steps, but **where the code lives** and **how data flows**.

**Stack:** Angular (`bcp-web`) + ASP.NET Core (`bcp-api`) + Supabase Postgres.

**UI route:** `/nd/analyse-regul` · **Workflow engine:** `workflow_engine = regul_pipeline`

---

## 1. Architecture (one picture)

```
Browser (analyse-regul.component.ts)
    │  POST create / confirm-clauses / start
    │  GET status / results (poll)
    ▼
AnalysisRunsController.cs
    │  Task.Run → NdRegulAnalysisProcessor.ProcessRunAsync()
    ▼
┌─────────────────────────────────────────────────────────────┐
│ forward:  RegulWorkflowLlmService.CallJudgmentAsync()       │
│           → NdRegulJudgmentPostProcessor (quotes, retry)      │
│           → NdRegulAnalysisPointSync → analysis_points        │
├─────────────────────────────────────────────────────────────┤
│ reverse:  NdInternalDocumentSectionService (library sections) │
│           → regul_internal_sections (per run)               │
│           → RegulWorkflowLlmService (reverse map per section) │
│           → NdRegulReverseIntRows → INT findings + points   │
├─────────────────────────────────────────────────────────────┤
│ qualitative: RegulWorkflowLlmService.AnalyzeTextAsync()     │
│              → regul_qualitative_assessments                │
└─────────────────────────────────────────────────────────────┘
    ▲
Landing AI (parse/extract only — not forward/reverse LLM)
    GovPointsService / LandingAiGovExtractService
    LandingAiPolicyClauseExtractService
```

**DI registration:** `bcp-api/Program.cs` — `NdRegulAnalysisProcessor`, `RegulWorkflowLlmService`, `RegulWorkflowLlmSettingsService`, `NdInternalDocumentSectionService`, `LandingAiPolicyClauseExtractService`.

---

## 2. Library prep (before analyse-regul)

### 2A — Regulation document: upload + extract points

| Layer | File | What it does |
|-------|------|----------------|
| **UI** | `bcp-web/src/app/pages/nd/regulation-documents/nd-regulation-documents.component.ts` | Upload, list docs, **Run extraction** button |
| **UI** | `nd-regulation-points-panel.component.ts` | Viewpoints tree from extracted points |
| **API** | `Controllers/NewDashboard/RegulationDocumentsController.cs` | `POST /nd/regulation-documents/{id}/extract` (~line 1051) |
| **Service** | `Services/LandingAi/LandingAiGovExtractService.cs` | Landing AI ADE extract with gov schema |
| **Service** | `Services/GovPointsService.cs` | Persist points, cache keys |
| **Schema** | `bcp-api/Schemas/gov-requirement-points.schema.json` | Structured extract output |
| **DB** | `regulation_documents`, `regulation_points`, `landing_ai_extract_cache` | Points + cache by `file_hash` + schema key |
| **Upload** | `Services/NewDashboard/NdRegulationUploadService.cs` | Upload path, `extractionStatus = pending` until manual extract |

**Flow:** Upload → `stored_documents` / regulation row → user clicks extract → `LandingAiGovExtractService` → points in `regulation_points` → UI reads via regulation-documents APIs.

**Not automatic on upload** — extraction stays `pending` until Run extraction (see `NdRegulationUploadService` / list `extractionStatus` in controller).

---

### 2B — Internal document: parse + extract sections

| Layer | File | What it does |
|-------|------|----------------|
| **UI** | `nd-internal-documents.component.ts` | Upload, **Run parse**, **Extract sections** |
| **UI** | `nd-internal-document-sections-panel.component.ts` | Sections side panel |
| **API parse** | `InternalDocumentsController.cs` | `POST /nd/internal-documents/{id}/parse` |
| **API sections** | `InternalDocumentsController.cs` | `POST /nd/internal-documents/{id}/extract-sections` (~482), `GET …/sections` |
| **Parse** | `Services/NewDashboard/NdInternalParseService.cs` | Landing parse → markdown in `landing_ai_parse_cache` |
| **Sections** | `Services/NewDashboard/NdInternalDocumentSectionService.cs` | `ExtractAndSaveSectionsAsync`, `EnsureSectionsForWorkflowAsync` |
| **Landing extract** | `Services/LandingAi/LandingAiPolicyClauseExtractService.cs` | 15-page chunks, cache key `policy_clauses_v1` |
| **Schema** | `bcp-api/Schemas/policy-clauses.schema.json` | `clause_no`, `clause_text`, `source_page` |
| **DB** | `stored_documents` (`parse_status`, `section_extract_status`, …) | |
| **DB** | `nd_internal_document_sections` | Library sections (`stored_document_id`) |

**Flow:** Parse stores markdown → optional extract-sections writes `nd_internal_document_sections` → Sections panel lists via GET sections.

If library sections are empty at analysis time, **reverse phase** calls `EnsureSectionsForWorkflowAsync()` (same Landing extract, saves to library + run).

---

## 3. Create Regul analysis run

| Layer | File | What it does |
|-------|------|----------------|
| **UI** | `analyse-regul.component.ts` | Extends `analyse-base.ts`; sets `ndWorkflowEngine = 'regul_pipeline'` |
| **UI** | `analyse-regul.component.ts` | `createRun()` → `workflowEngine: 'regul_pipeline'`, `enableQualitative`, selected points + internal doc IDs |
| **API client** | `nd-api.service.ts` | `POST /nd/analysis-runs` |
| **API** | `AnalysisRunsController.Create()` (~213) | Creates `NdAnalysisRun` + `NdAnalysisPoint` rows from snapshot |
| **Entity** | `Data/NewDashboard/NdEntities.cs` | `NdAnalysisRun.WorkflowEngine`, `EnableQualitative`, `RegulPipelinePhase`, … |
| **Constant** | `AnalysisWorkflowEngine.cs` | `RegulPipeline = "regul_pipeline"` |

**DB after create:**
- `analysis_runs.status = draft`
- `analysis_points` — one row per selected regulation point (`regulation_point_id` + `point_snapshot` JSON)
- `total_points_count` = number of selected clauses

**Routing:** `app.routes.ts` → path `analyse-regul` loads `AnalyseRegulComponent`.

---

## 4. Clause review + confirm (human gate)

| Layer | File | What it does |
|-------|------|----------------|
| **UI** | `analyse-regul.component.html` + `.ts` | Inline clause panel; `regulClauseRows`; `confirmRegulClauses()` |
| **API client** | `nd-api.service.ts` | `POST /nd/analysis-runs/{id}/confirm-clauses` |
| **API** | `AnalysisRunsController.ConfirmClauses()` (~486) | Sets `regul_clauses_confirmed_at`; optional snapshot edits |
| **Processor gate** | `NdRegulAnalysisProcessor.ProcessRunAsync()` (~51) | Throws if `RegulClausesConfirmedAt == null` |

**UI rule:** Run button disabled until `regulClausesConfirmed` (from `regulClausesConfirmedAt` on resume).

---

## 5. Start analysis (background job)

| Layer | File | What it does |
|-------|------|----------------|
| **UI** | `analyse-base.ts` | `startNdRun()` → POST start; polls `GET …/status` |
| **API** | `AnalysisRunsController.Start()` (~435) | Validates confirm gate; `Task.Run` → processor |
| **Branch** | `Start()` (~460–468) | `regul_pipeline` → `NdRegulAnalysisProcessor`; else `NdAnalysisProcessor` (V8 dual-verify) |
| **Cancel** | `NdAnalysisRunCancellationTracker` | Stop button sets cancel flag |

**Important:** `NdAnalysisProcessor.cs` explicitly rejects `regul_pipeline` runs — Regul must use `NdRegulAnalysisProcessor` only.

---

## 6. Pipeline orchestration

**File:** `Services/NewDashboard/NdRegulAnalysisProcessor.cs`  
**Entry:** `ProcessRunAsync(Guid runId)`

| Order | Method | Sets `regul_pipeline_phase` | Purpose |
|-------|--------|-------------------------------|---------|
| 1 | `EnsureInternalSectionsForRunAsync` | (prep) | Copy library sections → `regul_internal_sections` or extract |
| 2 | `EnsureForwardFindingsAsync` | | Create `regul_forward_findings` rows (`status=pending`) |
| 3 | `RunForwardPhaseAsync` | `forward` | LLM per clause |
| 4 | `RunReversePhaseAsync` | `reverse` | LLM per internal section + INT rows |
| 5 | `RunQualitativePhaseAsync` | `qualitative` | One LLM call if `enable_qualitative` |
| 6 | `FinalizePointCountsAsync` | `done` | `status=completed`, recount failed clauses |

**On failure:** `status=failed`, `regul_pipeline_error=ex.Message`  
**On cancel:** `MarkCancelledAsync`

**LLM config loaded once:** `RegulWorkflowLlmSettingsService.GetConfigAsync()` → stored on run as `regul_llm_provider` / `regul_llm_model`.

---

## 7. Forward phase (per regulatory clause)

| Step | Code |
|------|------|
| Load policy | `LoadPolicyBundleAsync()` → `NdInternalParseService` + `SupabaseStorageService` |
| Policy context | `NdRegulPolicyContextService.BuildContextForClause(clauseText)` — full manual if ≤50 pages, else keyword retrieval (top 8 chunks) |
| Prompts | `NdRegulPromptDefaults.JudgmentSystemPrompt`, `BuildJudgmentContextText`, `BuildJudgmentQueryText` |
| LLM call | `RegulWorkflowLlmService.CallJudgmentAsync(context, query, cacheContext)` |
| Parse JSON | `NdRegulLlmJsonHelper.ParseJsonObject<RegulJudgmentResult>` |
| Post-process | `NdRegulJudgmentPostProcessor.ApplyQuoteVerification`, gap_description retry loop |
| Format UI message | `NdRegulJudgmentFormatter.FormatLandingMessage` |
| Persist finding | `regul_forward_findings` (`result_json`, `status`) |
| Sync gap UI | `NdRegulAnalysisPointSync.ApplyForwardJudgment` → `analysis_points.landing_ai_*`, `final_status` |

**Models:** `NdRegulJudgmentModels.cs` (`RegulJudgmentResult`)  
**Prompt source:** `NdRegulPromptDefaults.cs` (ported from Regul.ai `prompts.py`)

**Progress counters during forward:** `run.ProcessedPointsCount`, `LandingAiCompletedCount` updated each clause (failed clauses set `landing_ai_status=failed`, `landing_ai_error`).

---

## 8. Reverse phase (per internal section)

| Step | Code |
|------|------|
| Sections for run | `EnsureInternalSectionsForRunAsync` → `NdInternalDocumentSectionService.EnsureSectionsForWorkflowAsync` |
| Per-run table | `regul_internal_sections` (`NdRegulInternalSection` in `RegulWorkflowEntities.cs`) |
| Regulatory context | `BuildSelectedRegulatoryClauses(run)` — **only selected run points**, not whole regulation doc |
| Clear old INT | `ClearIntReverseArtifactsAsync` — removes prior INT findings/mappings on re-run |
| Per section | `CallReverseMappingAsync` → `RegulWorkflowLlmService.AnalyzeTextAsync` |
| Prompts | `NdRegulPromptDefaults.ReverseMappingSystemPrompt`, `BuildReverseMappingContextText`, `BuildReverseMappingQueryText` |
| Parse | `RegulReverseMappingResult` in `NdRegulJudgmentModels.cs` |
| Save mapping | `regul_reverse_mappings` (`mapping`, `mapped_clause_nos`, `result_json`) |
| INT gap rows | `NdRegulReverseIntRows.ShouldCreateIntRow` → `BuildIntFinding` → new `analysis_points` (no `regulation_point_id`) |
| INT sync | `NdRegulAnalysisPointSync.ApplyIntReverseFinding` |

**Cost:** One LLM call **per section** in `regul_internal_sections` (can be hundreds). Not per selected clause.

**Poll API:** `AnalysisRunsController.Status` loads section list + mapping status → `regulReverseSectionTotal`, `regulReverseSections[]`.

---

## 9. Qualitative phase (optional)

| Layer | Code |
|-------|------|
| Gate | `run.EnableQualitative` on `NdAnalysisRun` |
| Processor | `RunQualitativePhaseAsync` |
| Prompts | `NdRegulPromptDefaults.QualitativeAssessmentSystemPrompt`, `BuildQualitativeAssessmentPrompt` |
| LLM | `RegulWorkflowLlmService.AnalyzeTextAsync` |
| Parse | `RegulQualitativeResult` |
| DB | `regul_qualitative_assessments` (`NdRegulQualitativeAssessment`) |
| Results API | `ResultsController.Get` (~91) → `regulQualitativeAssessment` |
| UI | `analyse-regul.component.html` — Qualitative Document Assessment card |

---

## 10. API responses & frontend field mapping

**Projection layer:** `NdRegulApiProjection.cs`

| API output | DB source | Used by UI |
|----------|-----------|------------|
| `regulPipelinePhase` | `analysis_runs.regul_pipeline_phase` | Phase badge, progress |
| `regulClauseTotal/Completed/Failed` | `total_points_count`, `landing_ai_completed_count`, `dual_verify_failed_count` | Clause progress |
| `regulReverseSection*` | Computed from `regul_internal_sections` + `regul_reverse_mappings` | Reverse progress panel |
| `regulForwardStatus/Error/Result` | `analysis_points.landing_ai_*` | Per-clause row (Regul runs) |
| Legacy aliases | Same columns | V8 compatibility |

**Frontend helpers:**
- `bcp-web/src/lib/nd/regul-fields.ts` — `regulForwardError`, `regulClauseFailedCount`, …
- `analysis-point-mapper.ts` — maps API point to UI model
- `analysis-run-status.ts` — phase-aware status labels for `regul_pipeline`

**Poll:** `analyse-regul.component.ts` `pollNdRunStatus` logs phase + counts; reads `regulReverseSections`.

---

## 11. UI behaviour (analyse-regul specifics)

| Behaviour | Code |
|-----------|------|
| Clause list excludes INT | `analyse-regul.component.ts` — filters `INT` prefix from analysing list (~742, ~756) |
| INT in gap export | `analysis_points` includes INT rows; export uses shared gap helpers |
| Confirm panel | `regulClauseRows`, `confirmRegulClauses()` (~1643) |
| Demo / credit guard | `analyse-base.ts` — type `start` for demo runs |
| Shared analyse logic | `analyse-base.ts` — polling, gap panel, export PDF/Excel |
| `isRegulPipeline` | `regul-fields.ts` `isRegulWorkflowEngine()` |

**Templates / styles:** `analyse-regul.component.html`, `analyse-regul.component.scss`

---

## 12. LLM settings (admin)

| Layer | File |
|-------|------|
| Settings service | `Services/Llm/RegulWorkflowLlmSettingsService.cs` |
| LLM router | `Services/Llm/RegulWorkflowLlmService.cs` — switches Google/OpenAI/Anthropic/xAI |
| Admin API | `SystemSettingsController` — `GET/PUT /nd/admin/settings/regul-workflow-llm` |
| Read-only API | `NdSettingsController` — `GET /nd/settings/regul-workflow-llm` |
| DB | `nd_system_settings` key `regul_workflow_llm` (JSON provider + model) |
| API keys | `appsettings.Secrets.json` — `Anthropic:ApiKey`, `Gemini:ApiKey`, … |

**Landing AI keys** (`LandingAi:ApiKey`) are separate — used only for parse/extract, not forward/reverse/qualitative.

---

## 13. Database bootstrap

| Script | Purpose |
|--------|---------|
| `scripts/supabase/009_regul_workflow.sql` | `regul_forward_findings`, `regul_internal_sections`, `regul_reverse_mappings`, `regul_qualitative_assessments`, run columns |
| `scripts/supabase/010_internal_document_sections.sql` | `nd_internal_document_sections` |
| `scripts/supabase/011_landing_ai_extract_cache_schema_key.sql` | Cache schema key column |
| `Infrastructure/SupabaseSchemaBootstrap.cs` | Auto ALTER on API startup |
| `Infrastructure/NdSchemaBootstrap.cs` | ND tables |
| EF entities | `RegulWorkflowEntities.cs`, `NdEntities.cs` (`NdInternalDocumentSection`) |
| DbContext | `Data/AppDbContext.cs` — `DbSet` for all Regul tables |

---

## 14. Tests

| File | Covers |
|------|--------|
| `tests/Reguliq.Api.Tests/NdRegulPolicyContextServiceTests.cs` | Full manual vs retrieval |
| `tests/Reguliq.Api.Tests/NdRegulApiProjectionTests.cs` | `regulForward*` and poll field names |

Run: `dotnet test --filter FullyQualifiedName~NdRegul` (from `bcp-api/tests/Reguliq.Api.Tests`).

---

## 15. Step → file quick index

| Workflow step (see REGUL-AI-WORKFLOW.md) | Primary code |
|------------------------------------------|--------------|
| Regulation extract | `LandingAiGovExtractService`, `RegulationDocumentsController` POST extract |
| Internal parse | `NdInternalParseService`, `InternalDocumentsController` POST parse |
| Internal sections | `LandingAiPolicyClauseExtractService`, `NdInternalDocumentSectionService` |
| Create run | `AnalysisRunsController.Create`, `analyse-regul.component.ts` |
| Confirm clauses | `AnalysisRunsController.ConfirmClauses` |
| Start / orchestrate | `AnalysisRunsController.Start`, `NdRegulAnalysisProcessor.ProcessRunAsync` |
| Forward judgment | `RunForwardPhaseAsync`, `CallForwardJudgmentAsync`, `NdRegulJudgmentPostProcessor` |
| Reverse mapping | `RunReversePhaseAsync`, `CallReverseMappingAsync`, `NdRegulReverseIntRows` |
| Qualitative | `RunQualitativePhaseAsync`, `ResultsController` |
| Gap UI sync | `NdRegulAnalysisPointSync`, `NdRegulJudgmentFormatter` |
| API → UI names | `NdRegulApiProjection`, `regul-fields.ts` |
| Prompts (analysis LLM) | `NdRegulPromptDefaults.cs` |
| Extraction schemas (Landing) | `Schemas/gov-requirement-points.schema.json`, `Schemas/policy-clauses.schema.json` |

---

## 16. Debugging tips

| Symptom | Check |
|---------|--------|
| All clauses `failed`, credit error | `analysis_points.landing_ai_error` / `regulForwardError`; Anthropic balance; `RegulWorkflowLlmService` logs |
| High cost, few clauses | Reverse section count — `regul_reverse_section_total`; use smaller internal doc |
| Run won't start | `regul_clauses_confirmed_at` null; `POST confirm-clauses` |
| INT in wrong list | UI filter in `analyse-regul.component.ts` (INT excluded from analysing list only) |
| Wrong API fields in UI | `NdRegulApiProjection.MapPoint` / `MapRunPoll`; `regul-fields.ts` |
| V8 processor error on Regul run | `NdAnalysisProcessor` guard — must use `NdRegulAnalysisProcessor` |

**Logs:** API console — search `Regul pipeline`, `Regul forward judgment`, `Regul reverse phase`.
