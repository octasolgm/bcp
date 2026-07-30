# Regul.ai vs BCP — Analysis Differences & Change Plan

**Date:** 2026-07-27  
**Scope:** Analysis Version **V2** (`/nd/analyse-v9`) only — original Analyse V8 stays on BCP V2 prompts.

---

## 1. High-level difference

| Area | **BCP (this repo)** | **Regul.ai** |
|------|---------------------|--------------|
| Stack | Angular + ASP.NET Core | Python FastAPI |
| Phase 1 | **Landing AI** extract/compare (structured JSON schema) | **Claude** clause extraction (`EXTRACTION_SYSTEM_PROMPT`) |
| Phase 2 | **Dual verify** second LLM (Gemini/Claude/etc. from settings) | **No second model** — one Claude judgment + **code** quote verify |
| Judgment | Compliance auditor prompt (V1/V2) | Claude `JUDGMENT_SYSTEM_PROMPT` (element-level, regulator-perspective, AML vendor DD) |
| Extra passes | — | Reverse coverage mapping + qualitative assessment |
| Human gate | Workflow (maker → checker → reviewer) | Clause review/confirm before analysis |
| Output | Landing message + Pass 2 block text → ND run points | Structured tool-call JSON → findings DB |

**Important:** Regul.ai does **not** use Landing AI or dual-verify. We **keep** BCP’s two-pass pipeline and **port Regul.ai judgment rules** into Landing AI (Pass 1) and Dual Verify (Pass 2) as **ComparePromptVersion.V3**.

---

## 2. What we change (V2 clone only)

| # | Change | Status |
|---|--------|--------|
| 1 | Clone UI `analyse-v8` → `analyse-v9` + Analysis Versions menu | Done |
| 2 | Add **V3** Landing AI prompt (Regul judgment rules → BCP JSON fields) | Done |
| 3 | Add **V3** Dual Verify Pass 2 rules (same judgment ideas) | Done |
| 4 | Persist `compare_prompt_version` on analysis run; analyse-v9 sends `v3` | Done |
| 5 | Leave analyse-v8 / gap-analysis default on **V2** | Done |
| 6 | Later: extraction / reverse coverage / qualitative (optional) | Not yet — wait for your go-ahead |

---

## 3. Prompt mapping

| Regul.ai | BCP V3 (analyse-v9) |
|----------|---------------------|
| `JUDGMENT_SYSTEM_PROMPT` | Landing AI `PromptTemplateV3` + Dual Verify `AppendPass2RulesV3` |
| Document-perspective (regulator-only ≠ gap) | Included in V3 |
| Vendor/list-provider due diligence | Included in V3 |
| Element-level checking | Included in V3 |
| Verbatim `policy_extract` | Mapped to evidence quote rules |
| `gap_description` / `suggested_action` | Mapped to `corrective_action_plan` / responsibility |
| `EXTRACTION_SYSTEM_PROMPT` | **Not** applied yet (BCP still uses library/reg points) |
| Reverse + qualitative | **Not** applied yet |

---

## 4. Files touched

**API**
- `Services/LandingAi/ComparePromptVersion.cs` — `V3` + parse/cache helpers  
- `Services/LandingAi/LandingAiComparePromptBuilder.cs` — `PromptTemplateV3`  
- `Services/GovPointsService.cs` — `DualVerifyPromptBuilder` Pass 2 V3 rules  
- `Services/NewDashboard/NdAnalysisProcessor.cs` — per-run prompt version  
- `Services/NewDashboard/NdAnalysisPromptDefaults.cs`  
- `Controllers/NewDashboard/AnalysisRunsController.cs` — accept `comparePromptVersion`  
- `Data/NewDashboard/NdEntities.cs` — `ComparePromptVersion` column  
- `Infrastructure/SupabaseSchemaBootstrap.cs` — `ALTER TABLE … compare_prompt_version`

**Web**
- `pages/analyse-v9/analyse-v9.component.ts` — create run with `comparePromptVersion: 'v3'`

**Docs**
- This file

---

## 5. How to verify

1. Restart API (required after backend prompt changes).  
2. Open **Analysis Version → V2** (`/nd/analyse-v9`).  
3. Start a new analysis — run should store `compare_prompt_version = v3`.  
4. Confirm Landing / Pass 2 wording reflects Regul rules (regulator-only, element-level, etc.).  
5. Open **V1** (`/nd/analyse-v8`) — still uses V2 prompts (unchanged).

---

## 6. Later (when you ask)

- Claude-style **extraction** instead of / in addition to current regulation-point load  
- Reverse coverage + qualitative assessment  
- Server-side quote verification like Regul.ai `verify_quotes()`  
- UI tweaks on the clone only
