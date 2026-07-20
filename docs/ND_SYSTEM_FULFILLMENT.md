# BCP New Dashboard — System Fulfillment Matrix

**Document type:** Requirements vs implementation status  
**Companion doc:** [ND_WORKFLOW.md](./ND_WORKFLOW.md)  
**Last updated:** 2026-07-20

This document answers: **What does the system already fulfill?** and **What still needs work?**

Legend: ✅ Implemented · 🟡 Partial · ❌ Not implemented · 🔒 Legacy-only

---

## 1. Executive summary

| Area | Fulfillment | Notes |
|------|-------------|-------|
| ND shell & routing | ✅ ~95% | `/nd/*` live; `/old/*` preserved |
| Auth & roles | 🟡 ~80% | Supabase login, 4 roles, guards — verify email/forgot-password in prod |
| Document upload & storage | ✅ ~90% | Supabase bucket + DB; extraction cached |
| Regulation extraction UX | 🟡 ~70% | Upload + extract works; live "extracting" polling weak |
| Libraries | ✅ ~90% | 3-column builder, DB-backed, used in analyse-v8 |
| Analysis execution | 🟡 ~60% | **Gap:** analyse-v8 "Run" still uses legacy dual-verify jobs, not full ND processor |
| Resume failed/draft runs | 🟡 ~65% | `?run=` attach exists; not all resume/start paths wired in UI |
| Results / gap analysis | 🟡 ~75% | ND gap page exists; point detail good; list view & filters need polish |
| Action plan edit + history | ✅ ~85% | API + UI exist; item-wise history panel needs UX refinement |
| Checker / reviewer workflow | ✅ ~85% | Queues, approve, pull-back, comments, status history |
| Soft delete runs | ✅ ~90% | Delete + super-admin restore page |
| Departments | 🟡 ~70% | Admin CRUD exists; doc tagging/filtering may need UX |
| Export Excel/PDF | ✅ ~85% | Working doc Excel + ND export libs; parity testing needed |

**Overall ND fulfillment vs your full requirement set: ~75%**

The largest gap is **unifying analysis execution** so every new run goes through the ND database pipeline (create → start → poll → gap analysis) instead of the legacy Kafka dual-verify session path.

---

## 2. Requirement-by-requirement fulfillment

### 2.1 Platform & dual dashboard

| Requirement | Status | Evidence |
|-------------|--------|----------|
| New dashboard at `/nd/*` | ✅ | `app.routes.ts` — ND shell + pages |
| Old dashboard unchanged at `/old/*` | ✅ | Legacy routes under `ShellComponent` |
| Same AI pipeline (Landing + Google) | ✅ | `NdAnalysisProcessor` reuses `LandingAiCompareService` + `GeminiService` |
| Save extractions in Supabase to save credits | ✅ | `landing_ai_parse_cache`, `landing_ai_extract_cache`, `regulation_documents.extraction_result` |
| Store docs in container (Supabase storage) | ✅ | `SupabaseStorageService`, bucket `doc` |

---

### 2.2 Authentication & user management

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Login / register with email | ✅ | `/nd/auth/login`, Supabase auth |
| Super admin creates users by role | ✅ | `/nd/admin/users`, `UsersController` |
| Roles: maker, checker, reviewer | ✅ | `NdProfile.role`, `ndRoleGuard` |
| Forgot password via email | 🟡 | `/nd/auth/forgot-password`, reset pages — **verify SMTP/Supabase email config** |
| No hardcoded credentials in prod | 🟡 | ND uses Supabase; confirm no dev shortcuts in deployed env |

---

### 2.3 Regulation & internal documents

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Upload regulation PDFs | ✅ | `RegulationDocumentsController`, ND upload UI |
| Upload internal PDFs | ✅ | `InternalDocumentsController` |
| Extract on upload / Extract now button | ✅ | Auto-extract + manual `POST .../extract` |
| Show extracted vs not extracted | 🟡 | Shows `pending` / `extracted` / `manual` — **no live in-progress spinner/poll** |
| Ref doc ↔ extraction result (no re-credit) | ✅ | Cached by file hash + stored JSON on doc row |
| Department categorization | 🟡 | `DepartmentsController`, admin page — **confirm doc↔dept linking in UI** |
| Soft-hide regulation doc | ✅ | `DELETE` sets status -1 |
| Restore hidden regulation doc | ❌ | Hide works; no admin restore UI found |

---

### 2.4 Libraries

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Build library from multiple reg docs | ✅ | `nd-library-builder`, `LibrariesController` |
| Select specific points with source ref | ✅ | `library_points` snapshots with doc+point ids |
| 3-column UI (docs → points → selection) | ✅ | `nd-library-points-picker` |
| Edit / delete library | 🟡 | Edit ✅; delete is **hard delete** (not soft) |
| Load points from DB (no AI cost) | ✅ | Reads extracted points only |
| Use library in analysis | ✅ | Analyse-v8 library mode |

---

### 2.5 Analysis execution (Maker)

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Select internal + regulation docs or libraries | ✅ | `analyse-v8` + `AnalyseBase` |
| Dual verify: Phase 1 Landing → Phase 2 Google | ✅ | `NdAnalysisProcessor`, legacy `DualVerifyJobProcessor` |
| Progress N/M (e.g. 32/34) | ✅ | `processedPointsCount/totalPointsCount`, UI labels |
| Per-point Phase 1 / Phase 2 status chips | ✅ | Analyse-v8 progress column |
| Rerun failed point (full) | ✅ | `POST .../rerun-point/{pointId}` |
| Rerun dual verify only | ✅ | `POST .../rerun-dual-verify/{pointId}` + `/all` |
| Dual verify failed banner on results | 🟡 | Status `dual_verify_failed` exists; banner UX may need emphasis |
| Save all results in DB | 🟡 | ND runs ✅; **legacy Run button saves to compliance sessions, not always ND tables** |
| Create ND run from "Run analysis" button | ❌ | `AnalyseBase.runAnalysis()` → legacy job API, not `createAnalysisRun`+`start` |
| Original AI output preserved | ✅ | `originalAiActionPlan` on first completion |
| Maker sees 3 statuses: compliant / partial / non-compliant | ✅ | `finalStatus` normalization |

---

### 2.6 Analysis runs list (`/nd/analysis-runs`) — Task 1 & delete

| Requirement | Status | Evidence |
|-------------|--------|----------|
| List all runs with status & N/M points | ✅ | `nd-analysis-runs.component` |
| Click draft/running/failed → analyse-v8 resume | 🟡 | `run-links.ts` routes correctly; **full pre-fill + auto-start processor incomplete** |
| Click completed/DV failed/submit pending → gap analysis | ✅ | `ndAnalysisRunLink` → `/nd/gap-analysis?run=` |
| Soft delete (status deleted, not DB remove) | ✅ | `softDeleteAnalysisRun`, `status = deleted` |
| Super admin deleted-runs page + restore | ✅ | `/nd/admin/deleted-runs` |
| Legacy runs merged in list | ✅ | `AnalysisRunsController.List` merges ND + legacy |

---

### 2.7 Results & gap analysis — Task 2 & 3

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Open like `/old/gap-analysis?saved=compliance:uuid` | ✅ | ND: `?run={id}`; legacy: `?saved=compliance:` still supported |
| Point detail: regulation point | ✅ | `NdGapPointDetailComponent` |
| Point detail: policy extract | ✅ | Parsed citation display |
| Point detail: Phase 1 full detail | ✅ | Collapsible Landing AI message |
| Point detail: Phase 2 full detail | ✅ | Collapsible Google message |
| "What this reference fulfills" | ✅ | Bullet list in point detail |
| Corrective action plan (not part of phase 1/2) | ✅ | Separate CAP section |
| CAP editable | ✅ | Maker edit when not locked |
| CAP item-wise editable | 🟡 | Plan text editable; **per-item row edit UX may need refinement** |
| View history → right panel popup | 🟡 | History exists; **confirm right-panel popup vs inline** |
| Use previous history as current | ✅ | `revertToVersion` via API |
| History: who + when | ✅ | `ActionPlanHistoryEntry` with user + timestamp |
| Filters (severity, status, etc.) | 🟡 | Severity filters exist; **full filter set TBD** |
| All cards view | ✅ | Cards mode in gap analysis |
| List view: left list + right detail | 🟡 | Partial — **needs perfect match to your mockup** |
| Export Excel / PDF | ✅ | `exportXlsx`, ND export libs, legacy dual-verify exports |
| Embedded results on analyse-v8 (cards + list) | 🟡 | Embed mode exists; **toggle polish needed** |
| Submit for review from gap / v8 | ✅ | `submit-for-review`, `resubmit-for-review` |

---

### 2.8 Checker & reviewer workflow — Task 3

| Requirement | Status | Evidence |
|-------------|--------|----------|
| Checker sees queue of submitted runs | ✅ | `/nd/checker`, `GET /nd/checker/queue` |
| Checker approve (pass) | ✅ | Approve endpoint → `checker_approved` |
| Checker pull back to maker with comments | ✅ | Pull-back + required comment → `pulled_back` |
| Comment per regulation point | ✅ | `analysis_point_comments` |
| Comment on action plan (whole + items) | 🟡 | Point comments exist; **granularity for each CAP item — verify UI** |
| Reviewer queue after checker pass | ✅ | `/nd/reviewer` |
| Reviewer finalize | ✅ | → `reviewer_approved` |
| Reviewer pull back to checker | ✅ | Pull-back endpoints |
| Status phases visible on runs list | ✅ | `workflowStatusLabel`, status badges |
| Full status + review history | ✅ | `analysis_status_history`, reviews returned in API |

---

## 3. What each role can do today

### Super Admin ✅
- Manage users (create, role assign, deactivate)
- Manage departments
- View/restore deleted analysis runs
- All maker/checker/reviewer capabilities

### Maker 🟡
- ✅ Upload docs, build libraries, open analyse-v8
- ✅ View analysis runs, soft-delete own runs
- ✅ Edit action plans (when allowed)
- ✅ Submit/resubmit for review
- 🟡 **Run analysis** — works via legacy path; ND DB run creation from main button incomplete
- 🟡 Resume draft — partial via `?run=`

### Checker ✅
- ✅ Queue of `submitted_for_review` runs
- ✅ Approve or pull back with comments
- ✅ Review UI with point detail + history read-only

### Reviewer ✅
- ✅ Queue of `checker_approved` runs
- ✅ Finalize or pull back

---

## 4. API surface fulfillment

| API group | Base path | Status |
|-----------|-----------|--------|
| Auth | `/nd/auth` | ✅ |
| Users & departments | `/nd/users`, `/nd/departments` | ✅ |
| Regulation docs | `/nd/regulation-documents` | ✅ |
| Internal docs | `/nd/internal-documents` | ✅ |
| Libraries | `/nd/libraries` | ✅ |
| Analysis runs | `/nd/analysis-runs` | ✅ |
| Results & action plans | `/nd/results` | ✅ |
| Checker | `/nd/checker` | ✅ |
| Reviewer | `/nd/reviewer` | ✅ |
| Legacy dual-verify (still used) | `/dual-verify-kafka`, `/landing-ai` | 🔒 Active for analyse-v8 Run |

---

## 5. Database fulfillment

| Table / entity | Purpose | Status |
|----------------|---------|--------|
| `nd_analysis_runs` | Run lifecycle | ✅ |
| `nd_analysis_points` | Per-point AI results | ✅ |
| `nd_action_plan_history` | CAP versions | ✅ |
| `nd_analysis_reviews` | Checker/reviewer actions | ✅ |
| `nd_analysis_point_comments` | Review comments | ✅ |
| `nd_analysis_status_history` | Audit trail | ✅ |
| `regulation_documents` | Reg PDFs + extraction | ✅ |
| `internal_documents` | Internal PDFs | ✅ |
| `libraries` / `library_points` | Point libraries | ✅ |
| `departments` | Org units | ✅ |
| `hidden_legacy_runs` | Hide legacy from ND list | ✅ |
| `landing_ai_*_cache` | AI credit savings | ✅ |
| `landing_ai_compliance_sessions` | Legacy saved sessions | 🔒 Still used |

---

## 6. Gap priority matrix (for development)

| Priority | Gap | User impact | Effort |
|----------|-----|-------------|--------|
| **P0** | Wire analyse-v8 Run → `createAnalysisRun` + `startAnalysisRun` | Runs not fully in ND DB; resume/export/workflow breaks | Medium |
| **P1** | Full resume: pre-fill docs, points, selections on `?run=` | Task 1 incomplete | Medium |
| **P1** | Auto-start processor for draft/failed ND runs | User must manually figure out resume | Low |
| **P2** | Gap analysis list view (left/right split) match mockup | Task 3 UX | Medium |
| **P2** | CAP item-wise edit + right-panel history | Task 2 polish | Medium |
| **P2** | Dual verify failed banner + bulk rerun prominence | Maker confusion on DV failures | Low |
| **P3** | Regulation doc "extracting…" live status | Admin UX | Low |
| **P3** | Department filters on reg doc library | Org scoping | Medium |
| **P4** | Library soft-delete | Data retention preference | Low |
| **P4** | Regulation doc restore after hide | Admin recovery | Low |
| **P5** | Forgot-password email verification in production | Go-live blocker if email broken | Ops |

---

## 7. Testing fulfillment checklist

| Test scenario | Expected | Current |
|---------------|----------|---------|
| Upload reg doc → extract → points in library | ✅ | Pass |
| Build library from 2 docs, run analysis | 🟡 | Works if legacy/demo path; ND path partial |
| Fail 1 point → rerun point | ✅ | Pass on ND-attached runs |
| Phase 1 ok, Phase 2 fail → rerun DV only | ✅ | API + UI exist |
| Edit CAP → history → revert to original | ✅ | Pass |
| Submit → checker pull back → maker edit → resubmit | ✅ | Pass |
| Checker approve → reviewer finalize | ✅ | Pass |
| Delete run → hidden → super admin restore | ✅ | Pass |
| Export Excel after reviewer approved | 🟡 | Needs regression test |
| `/old/analyse-v8` still works | ✅ | Unchanged |

---

## 8. What the system fulfills (plain language)

**You can already:**

1. Log into the new dashboard with role-based navigation.
2. Upload regulation and internal documents to Supabase-backed storage.
3. Extract regulation points via Landing AI once and reuse them (libraries, analysis) without re-paying extraction costs.
4. Build named libraries by picking points from multiple regulation documents.
5. Run compliance analysis with dual verify (Landing AI + Google) and see per-point progress.
6. View analysis runs in a central list with point counts and workflow status.
7. Open finished runs in gap analysis with rich point detail (regulation text, policy extract, both AI phases, action plan).
8. Edit corrective action plans with version history and ability to revert.
9. Submit runs through maker → checker → reviewer with comments and pull-back.
10. Soft-delete runs and restore them as super admin.
11. Keep using the entire old dashboard at `/old/*` without changes.

**You cannot yet fully rely on:**

1. Every new analysis from analyse-v8 creating a proper ND database run (some paths still use legacy sessions).
2. One-click resume of draft/failed runs with 100% identical selections and automatic continuation.
3. Gap analysis list view and CAP history UI exactly matching your PDF/mockup spec.
4. Production-confirmed forgot-password email without environment verification.

---

## 9. Suggested next Cursor task prompt (implementation kickoff)

When ready to implement (after approving these docs):

```
Read docs/ND_WORKFLOW.md and docs/ND_SYSTEM_FULFILLMENT.md.

Phase P0 only — do NOT change /old/* routes or legacy controllers.

Goal: When maker clicks "Run analysis" on /nd/analyse-v8, call:
  POST /nd/analysis-runs (create draft with selected docs, libraries, points)
  POST /nd/analysis-runs/{id}/start
  Poll GET /nd/analysis-runs/{id}/status
instead of legacy POST /dual-verify-kafka/jobs.

Keep attachToNdAnalysisRun(?run=) working. Add tests for create+start+poll.
List every file you touch and confirm legacy /old/analyse-v8 still uses old path if needed.
```

---

*For process details and diagrams, see [ND_WORKFLOW.md](./ND_WORKFLOW.md).*
