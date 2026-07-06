# BCP Web — Full Claude Prompt (Copy Everything Below)

**How to use:**
1. Upload the entire `web-interface/` folder (all PNGs + `BUILD_REQUIREMENTS.md`).
2. Copy the entire prompt inside the ` ``` ` block below.
3. Paste into Claude Code or claude.ai.
4. If Claude stops early, use the follow-up prompts at the bottom.

---

## COPY FROM HERE ↓

```
You are a senior full-stack engineer. Build a COMPLETE, WORKING banking compliance web application end-to-end. Do NOT stop at scaffold or mock UI only — deliver a runnable monorepo with all pages, APIs, database, mock AI pipeline, PDF modal, Excel export, and seed data.

The UI must match the attached `web-interface/` screenshot folder (Reguliq design). Read `BUILD_REQUIREMENTS.md` for full detail.

═══════════════════════════════════════════════════════════════
MONOREPO
═══════════════════════════════════════════════════════════════

Root folder name: **bcpweb**

bcpweb/
├── apps/
│   ├── web/                 # Next.js 14 App Router, TailwindCSS, port 3000
│   └── api/                 # NestJS 10, port 4000
├── packages/
│   ├── shared-types/        # All DTOs, enums, API response types
│   └── shared-utils/        # formatters, date helpers
├── docs/web-interface/      # UI reference (attached)
├── package.json             # npm workspaces
├── .env.example
├── README.md
└── CHANGELOG.md

Root scripts:
  dev:web, dev:api, build:web, build:api, db:migrate, db:seed, test

═══════════════════════════════════════════════════════════════
TECH STACK (MANDATORY)
═══════════════════════════════════════════════════════════════

Frontend:  Next.js 14, React 18, TypeScript strict, TailwindCSS, Lucide React, Recharts (donut chart)
Backend:   NestJS 10, class-validator, class-transformer, Swagger at /api/docs
Database:  Supabase PostgreSQL + pg migrations + seed script
Storage:   Supabase storage OR local uploads/ folder for PDFs
AI:        Gemini API (gap analysis) + Landing AI VISION_AGENT_API_KEY (PDF parse/extract)
           → MUST work with mock/fallback when keys missing
Excel:     ExcelJS — real .xlsx download
PDF:       pdf.js modal — page render + extracted text panel
Upload:    Multer — PDF, DOCX (max 50MB)
Realtime:  SSE or polling for analysis progress

═══════════════════════════════════════════════════════════════
DESIGN SYSTEM (match screenshots pixel-close)
═══════════════════════════════════════════════════════════════

- Dark mode ONLY: bg #0b111b / #0f1729, cards bg-white/5 border-white/10 rounded-xl
- Primary CTA: mint green #10b981 / #22d3a0
- Severity: Critical=#ef4444, High=#f97316, Medium=#eab308, Low=#22c55e, Compliant=#3b82f6
- Font: Inter
- Every page layout:
  TOP NAV: Logo "Reguliq" | Dashboard | Analyse | Reg Library | Documents | [Branch ▼] | [+ New Analysis]
  LEFT SIDEBAR: WORKSPACE (Overview, New Analysis, Documents w/ badge) + REGULATIONS (Library)
- Yellow field labels: "AI DRAFT — REVIEW & EDIT", "FILL IN", "SELECT", "SET DATE"
- Pill badges for severity/status
- AI draft banner on report page: "Columns C and J are extracted verbatim... AI-drafted and require your review"

═══════════════════════════════════════════════════════════════
ALL PAGES — BUILD EVERY ONE FULLY
═══════════════════════════════════════════════════════════════

1. /dashboard  (ref: 01-dashboard.png)
   - Header: "Compliance Dashboard", branch, "Last analysis: June 22, 2026"
   - 4 metric cards: Critical Gaps(2), High Risk(3), Total Findings(11), Compliant Items(3)
   - Donut chart: risk breakdown 27% critical/high
   - Remediation tracker table: Item, Severity, Target, Status
   - Recent analyses list with PDF icon, findings count, severity pills
   - "Run New Analysis →" button → /analyse

2. /reg-library  (ref: 02-regulation-library.png)
   - Filter tabs: All(12), CBUAE(4), FATF(2), UAE Gov(3), DIFC/DFSA(2), International(1)
   - Table: Regulation, Issuing Body, Type, Version, Last Updated, Status, [Use] button
   - Actions: "Sync now", "+ Add regulation"
   - Seed 12 regulations from screenshot (TFS, AML/CFT, FATF, Cabinet Decision 74, etc.)
   - "Use" button → pre-selects regulation on /analyse

3. /documents  (ref: 03-document-library.png)
   - Filter tabs: All documents, AML/CFT, Sanctions, KYC/CDD
   - 6 seeded documents with PDF/DOC/XLS icons, version, status badges (2 Gaps, Compliant, Review due, etc.)
   - "+ Upload" (real file upload to API)
   - "OneDrive" button (stub — toast "OneDrive sync coming soon")
   - "View analysis" link → report page

4. /analyse  (ref: 04–08)
   STEP 1: Select regulation — 4 cards with checkbox (TFS Guidelines CBUAE 48 clauses, AML/CFT 120, FATF 32, Cabinet Decision 74 21)
   STEP 2: Upload internal compliance doc (PDF/DOCX) — drag-drop, show filename + size + Ready + X remove
   STEP 3: Upload regulation PDF — drag-drop
   Button: "Run AI Gap Analysis" — disabled until regulation selected + both files uploaded
   RIGHT PANEL during analysis:
     - Progress checklist: Parsing → Loading clauses (48 found) → Cross-referencing → Identifying gaps → Generating actions
     - Progress bar 0-100%
     - Live briefing text stream
   RIGHT PANEL on complete:
     - "Analysis complete ✓" + green "View full report →"
     - Top findings list with severity tags
   → Creates session, runs job, redirects or links to /analyse/report/[id]

5. /analyse/report/[sessionId]  (ref: view-full-report/ all 19 screenshots)
   HEADER: "Gap Analysis — Working Document" | doc vs regulation | [Export XLSX] [Re-run]
   SUMMARY CARDS: Critical(2), High(3), Medium(2), Compliant(2)
   AI DRAFT BANNER (teal border)
   FILTER TABS: All(11), Critical, High, Medium, Low, Compliant
   EXPANDABLE ROWS (11 items) — click to expand full form:

   Per-item expanded form (ALL fields required):
   ┌─ REGULATORY REQUIREMENT (AI EXTRACTED)     │ POLICY EXTRACT (AI EXTRACTED) ─┐
   │  text + [View PDF · p.N]                   │  text + [View PDF · p.N]        │
   ├─ GAPS IDENTIFIED (AI DRAFT — REVIEW & EDIT) ─────────────────────────────────┤
   ├─ MANAGEMENT RESPONSE (FILL IN) ──────────────────────────────────────────────┤
   ├─ DESIGN EFFECTIVENESS [▼] │ OPERATING EFFECTIVENESS [▼] │ OVERALL EFFECTIVENESS [▼] ─┤
   │  Options: Compliant | Partial | Non-Compliant | N/A                          │
   ├─ DOCUMENT REFERENCE │ EVIDENCE OF IMPLEMENTATION (large textarea) ────────────┤
   ├─ EVIDENCE REFERENCE │ RESPONSIBLE DEPARTMENT [▼] ────────────────────────────┤
   │  Dept options: Compliance, Business/Compliance, Risk Management, Internal Audit, Legal, Technology
   ├─ COMPLIANCE STATUS [▼] │ TARGET DATE [date picker] ──────────────────────────┤
   │  Status: Compliant | Gap Identified | In Progress | Closed                   │
   ├─ CONCLUSION (AI DRAFT) │ OBSERVATION (AI DRAFT) │ ACTION PLAN (AI DRAFT) ────┤
   ├─ ASSIGNED TO [▼] │ [Sign off] button ─────────────────────────────────────────┤
   │  Assignee: Unassigned, Head Compliance, Senior Officer Policies, Officer AML, Head UAE Branch, etc.
   └─ Collapsed row shows: serial, §ref, title, severity badge, Signed off badge if signed ─┘

   PDF MODAL (ref: 06-view-pdf-modal.png, 19-view-pdf-modal-regulation-3-5.png):
   - Opens on "View PDF · p.N" click
   - Header: PDF icon, "Regulation — §X.X", "Regulation / Guideline", "Page N of M", X close
   - Left: PDF page render (pdf.js)
   - Right: "EXTRACTED TEXT" italic text + "Manually verify this matches what you see on the page"
   - Works with uploaded PDFs OR bundled sample PDF

   SIGN-OFF: PATCH item → signed_off=true, show "Signed off" blue badge on collapsed row
   AUTO-SAVE: debounced PATCH on every form field change

6. EXPORT XLSX  (ref: export-xlsx/ all 11 screenshots)
   - "Export XLSX" button on report page → GET /excel/export/:sessionId → downloads Gap_Analysis_Working.xlsx
   - Sheet 1 "Cover": audit header (INTERNAL AUDIT GROUP, Audit Title, Document Reviewed, Benchmark, Date, Prepared by, Version, column notes rows 10-11)
   - Sheet 2 "AML Guidelines": 21 columns A-U, 11 data rows matching seed items
   - Column C + J = verbatim regulatory/policy text
   - Column D, O, P, Q = AI gaps/conclusion/observation/action plans
   - Columns F-N, R-S = form field values from UI
   - Column T Signed Off: "Yes" or "Pending"
   - Evidence from form (e.g. "this is a test") appears in column K

═══════════════════════════════════════════════════════════════
SEED DATA — 11 COMPLIANCE ITEMS (use exactly)
═══════════════════════════════════════════════════════════════

| # | §Ref | Title | Severity | Signed Off |
|---|------|-------|----------|------------|
| 01 | §2.1 | Senior Management SCP Approval and Oversight | High | Pending |
| 02 | §2.3 | Sanctions Risk Appetite Written Documentation | Critical | Pending |
| 03 | §3.7 | Confirmed Match Freeze Without Delay 24 Hours | Critical | Pending |
| 04 | §4 | Notification to CBUAE and Executive Office Timing | High | Pending |
| 05 | §2.7 | Independent Audit Testing Processes and Systems | High | Pending |
| 06 | §2.3 | Sanctions Risk Appetite Documentation | Medium | Pending |
| 07 | §2.8 | Statutory Record Retention Period | Compliant | Yes |
| 08 | §3.3 | Customer Screening Lifecycle Triggers | Compliant | Yes |
| 09 | §4 | Notification Timelines to CBUAE and Executive Office | Medium | Pending |
| 10 | §3.5 | White List Management and False Positive Documentation | Low | Pending |
| 11 | §3.6 | Payments Screening Information Fields | Low | Pending |

Populate realistic AI text for each (regulatory requirement, policy extract, gaps, conclusion, observation, action plan) matching screenshot tone. Item 01 gaps example: "Manual Section 6.1 lacks documented annual review cycles..."

Default session: I M P T F S.pdf.pdf vs TFS Guidelines — 22 Jun 2026

═══════════════════════════════════════════════════════════════
BACKEND API — IMPLEMENT ALL ENDPOINTS
═══════════════════════════════════════════════════════════════

GET    /health
GET    /dashboard                          → metrics + recentAnalyses + remediationItems
GET    /regulations?category=              → list with filter counts
POST   /regulations                        → add regulation
POST   /regulations/sync                   → stub sync
GET    /documents?category=
POST   /documents/upload                   → multipart, returns document record
DELETE /documents/:id
POST   /analysis/sessions                  → { regulationId, internalFile, regulationFile } starts job
GET    /analysis/sessions/:id              → session + progress + status
GET    /analysis/sessions/:id/progress     → SSE stream OR poll endpoint
GET    /analysis/sessions/:id/items        → all compliance items
PATCH  /analysis/sessions/:id/items/:itemId → update any form field
POST   /analysis/sessions/:id/items/:itemId/sign-off
POST   /analysis/sessions/:id/rerun        → re-run analysis stub
GET    /pdf/page?documentId=&page=         → { imageUrl or base64, extractedText, totalPages }
GET    /excel/export/:sessionId            → application/vnd.openxmlformats... Gap_Analysis_Working.xlsx
GET    /branches                           → branch list for selector

Response format:
{ "success": true, "data": {}, "message": "" }
{ "success": false, "error": { "code": "", "message": "" } }

CORS: allow http://localhost:3000

═══════════════════════════════════════════════════════════════
DATABASE — SQL MIGRATION + SEED
═══════════════════════════════════════════════════════════════

Tables: branches, regulations, documents, analysis_sessions, compliance_items, audit_logs

Run: npm run db:migrate && npm run db:seed

Seed must create:
- 1 branch: SNB UAE / DIFC
- 12 regulations, 6 documents
- 1 completed analysis session (demo) with all 11 compliance items pre-filled
- Dashboard reads from this seed data

If Supabase unavailable: fallback to SQLite or in-memory JSON store with same interface — app MUST still run.

═══════════════════════════════════════════════════════════════
AI PIPELINE
═══════════════════════════════════════════════════════════════

When GEMINI_API_KEY + VISION_AGENT_API_KEY set → real AI
When missing → mock pipeline (2-3 second delays per step, return seed data above)

Steps:
1. parseDocument (Landing AI or pdf-parse)
2. extractRegulationPoints (48 clauses for TFS)
3. extractPolicyPoints
4. crossReference (match each clause to policy)
5. classifySeverity
6. generateGapsConclusionActionPlan
7. saveSession + items

Progress updates: 0% → 25% → 50% → 75% → 100%

═══════════════════════════════════════════════════════════════
SHARED TYPES (packages/shared-types)
═══════════════════════════════════════════════════════════════

Export: Severity, ComplianceStatus, EffectivenessRating, AnalysisStatus,
Regulation, Document, AnalysisSession, ComplianceItem, DashboardMetrics,
all request/response DTOs. Used by both apps/web and apps/api.

═══════════════════════════════════════════════════════════════
QUALITY & TESTS
═══════════════════════════════════════════════════════════════

- TypeScript strict, no `any`
- Zod or class-validator on all inputs
- JSDoc on public functions
- Error boundaries in React
- Loading skeletons on data fetch
- apps/api: Vitest + Supertest — health + dashboard + excel export tests
- apps/web: Vitest + RTL — SeverityBadge + GapItemRow tests
- CHANGELOG.md updated
- README: install, env setup, dev commands, page map

═══════════════════════════════════════════════════════════════
DEFINITION OF DONE — ALL MUST PASS
═══════════════════════════════════════════════════════════════

□ npm install works at root
□ npm run dev:api → http://localhost:4000/health returns 200
□ npm run dev:web → http://localhost:3000 loads dashboard
□ All 5 routes navigable via sidebar and top nav
□ /analyse: upload files → run analysis → see progress → complete → View full report works
□ /analyse/report/[id]: expand row → edit fields → auto-save → sign off → badge appears
□ View PDF modal opens with page + extracted text
□ Export XLSX downloads real file with Cover + AML Guidelines sheets, 11 rows, 21 columns
□ Seed data matches screenshot counts (2 Critical, 3 High, 2 Medium, 2 Compliant, 2 Low)
□ App runs WITHOUT API keys (mock mode)
□ Dark theme matches screenshots

═══════════════════════════════════════════════════════════════
BUILD ORDER — COMPLETE ALL IN THIS SESSION
═══════════════════════════════════════════════════════════════

Do NOT ask "should I continue?" — build all phases:
1. Monorepo scaffold + shared-types + env
2. NestJS all modules + migrations + seed
3. Next.js shell + all 5 pages with real API calls
4. Analysis wizard + mock AI job + SSE progress
5. Full report with all form fields + PDF modal + sign-off
6. Excel export
7. Tests + README + verify definition of done

Start now. Create every file. Output the full file tree when done.
```

## COPY TO HERE ↑

---

## Follow-up prompts (if Claude stops early)

**Continue building:**
```
Continue building bcpweb. Complete everything in the Definition of Done checklist that is not done yet. Do not restart — add missing files to the existing codebase.
```

**Fix and run:**
```
Run npm install, fix all TypeScript errors, start dev:api and dev:web, and fix any runtime errors until all 5 routes work.
```

**Report page only:**
```
Build /analyse/report/[sessionId] completely: all 11 expandable items, every form field and dropdown from view-full-report/ screenshots, PDF modal with pdf.js, sign-off, auto-save PATCH, Export XLSX button.
```

**Excel only:**
```
Implement GET /excel/export/:sessionId with ExcelJS. Must produce Gap_Analysis_Working.xlsx with Sheet1 Cover and Sheet2 AML Guidelines, columns A through U, 11 rows matching seed data. Match export-xlsx/ screenshots.
```

**Mock AI:**
```
Implement analysis job with mock AI fallback when API keys missing. Use the 11 seed compliance items. Simulate 5 progress steps over 8 seconds with SSE.
```

---

## What you attach to Claude

| File/Folder | Required |
|-------------|----------|
| `web-interface/*.png` (12 files) | Yes |
| `web-interface/view-full-report/` (19 files) | Yes |
| `web-interface/export-xlsx/` (11 files) | Yes |
| `web-interface/BUILD_REQUIREMENTS.md` | Yes |
| This `CLAUDE_PROMPT.md` | Optional (prompt is self-contained) |

**Total: 42 screenshots + 1 spec file**
