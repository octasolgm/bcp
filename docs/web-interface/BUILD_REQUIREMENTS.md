# BCP Web (bcpweb) — Build Requirements

> **Design reference:** UI screenshots in this folder replicate the **Reguliq** banking compliance platform.
> **Product name in code:** `bcpweb` (monorepo root folder name).
> **Stack:** Next.js 14 (App Router) + NestJS 10 + TypeScript strict + Supabase (PostgreSQL) + TailwindCSS.

---

## 1. Monorepo Structure

```
bcpweb/
├── apps/
│   ├── web/                    # Next.js 14 — port 3000
│   └── api/                    # NestJS 10 — port 4000
├── packages/
│   ├── shared-types/           # DTOs, enums, API contracts
│   └── shared-utils/           # Formatters, validators
├── docs/
│   └── web-interface/          # THIS FOLDER — UI reference screenshots
├── package.json                # npm workspaces
├── .env.example
├── README.md
└── CHANGELOG.md
```

### Root `package.json` scripts
```json
{
  "workspaces": ["apps/*", "packages/*"],
  "scripts": {
    "dev:web": "npm run dev --workspace=apps/web",
    "dev:api": "npm run start:dev --workspace=apps/api",
    "build:web": "npm run build --workspace=apps/web",
    "build:api": "npm run build --workspace=apps/api",
    "db:migrate": "npm run db:migrate --workspace=apps/api"
  }
}
```

---

## 2. Branding & Design System

### Theme
- **Dark mode only** — deep navy/charcoal background (`#0b111b` / `#0f1729` range)
- **Primary accent:** mint/teal green (`#22d3a0` / `#10b981`) for CTAs
- **Severity colors:**
  - Critical → red (`#ef4444`)
  - High → orange (`#f97316`)
  - Medium → yellow (`#eab308`)
  - Low → green (`#22c55e`)
  - Compliant → blue (`#3b82f6`)
- **Typography:** Inter or similar sans-serif
- **Cards:** rounded-xl, subtle border `border-white/10`, `bg-white/5`
- **Badges:** pill-shaped severity/status tags

### Layout (all pages)
- **Top nav:** Logo "Reguliq" (or configurable `BCP_WEB_APP_NAME`), tabs: Dashboard | Analyse | Reg Library | Documents
- **Branch selector:** dropdown e.g. "SNB UAE / DIFC"
- **CTA:** green `+ New Analysis` button (top right)
- **Left sidebar:**
  - WORKSPACE: Overview, New Analysis, Documents (badge count)
  - REGULATIONS: Library

---

## 3. Pages & Screenshot Map

### 3.1 Dashboard (`/dashboard`)
**Reference:** `01-dashboard.png`

| Component | Details |
|-----------|---------|
| Header | "Compliance Dashboard", branch name, "Last analysis: {date}" |
| CTA | "Run New Analysis →" |
| Metric cards | Critical Gaps, High Risk, Total Findings, Compliant Items |
| Risk breakdown | Donut chart — Critical/High/Medium/Low/Compliant |
| Remediation tracker | Table: Item, Severity, Target, Status |
| Recent analyses | List with PDF icon, date, findings count, severity badge |

### 3.2 Regulation Library (`/reg-library`)
**Reference:** `02-regulation-library.png`

| Component | Details |
|-----------|---------|
| Header | "Regulation Library" + subtitle "MENA region regulatory database" |
| Actions | "Sync now", "+ Add regulation" |
| Filter tabs | All, CBUAE, FATF, UAE Gov, DIFC/DFSA, International (with counts) |
| Table columns | Regulation, Issuing Body, Type, Version, Last Updated, Status, Action (Use) |
| Status pills | Active (green), Updated (yellow) |

### 3.3 Document Library (`/documents`)
**Reference:** `03-document-library.png`

| Component | Details |
|-----------|---------|
| Header | "Document Library" + "Version-controlled compliance documents — synced from OneDrive" |
| Actions | "OneDrive" sync button, "+ Upload" |
| Filter tabs | All documents, AML/CFT, Sanctions, KYC/CDD |
| Document rows | File type icon (PDF/DOC/XLS), title, metadata, version, status badges (Gaps/Reviewed/Compliant/Review due), "View analysis" / "History" links |

### 3.4 New Gap Analysis (`/analyse`)
**Reference:** `04`–`08` in root folder

**Step 1 — Select regulation** (`04-new-gap-analysis-empty.png`)
- 4 selectable cards with checkbox: TFS Guidelines, AML/CFT Guidelines, FATF Recommendations, Cabinet Decision 74/2020
- Each card: issuing body, date, clause count, type (Guidance/Standard/Law)

**Step 2 — Upload compliance document** (`05-new-gap-analysis-upload.png`)
- Dashed drop zone: "Internal compliance document" (PDF or DOCX)
- Step 3 drop zone: "Regulation / guideline PDF"
- Button: "Run AI Gap Analysis" (disabled until ready)

**Analysis in progress** (`06-analysis-in-progress.png`, `07-analysis-briefing.png`)
- Right panel: progress checklist (parsing, loading clauses, cross-referencing, identifying gaps, generating actions)
- Progress bar (0–100%)
- Live briefing text area with streaming AI output

**Analysis complete** (`08-analysis-complete.png`)
- Right panel: "Analysis complete ✓" + "View full report →"
- Findings list with severity tags (Critical/High) and section refs

### 3.5 View Full Report (`/analyse/report/[sessionId]`)
**Reference:** `view-full-report/` folder (19 screenshots)

| Screen | File | Features |
|--------|------|----------|
| Gap list | `02-gap-analysis-list.png` | Summary cards (Critical/High/Medium/Compliant), AI draft banner, filter tabs, expandable rows |
| Detail | `03-gap-analysis-detail.png` | Two columns: Regulatory Requirement vs Policy Extract (AI EXTRACTED), View PDF links |
| Form top | `04`–`05` | Gaps Identified (AI draft), Management Response |
| Dropdowns | `07`–`12`, `15` | Design/Operating/Overall Effectiveness, Responsible Dept, Compliance Status, Assignee |
| AI fields | `11`, `13` | Conclusion, Observation, Action Plan (AI DRAFT — REVIEW & EDIT) |
| Sign-off | `13`, `16` | Assignee dropdown, Sign off button, Signed off badges on rows |
| PDF modal | `06`, `19` | Split view: PDF page viewer (left) + Extracted Text (right), page nav, close X |
| Expanded item | `17`–`18` | Item §3.5 expanded, hover on View PDF link |

#### Gap item form fields (per compliance row)
| Field | Type | Options / Notes |
|-------|------|---------------|
| Regulatory Requirement | Read-only AI text | + View PDF · p.{n} |
| Policy Extract | Read-only AI text | + View PDF · p.{n} |
| Gaps Identified | Textarea (AI draft) | Editable, yellow label |
| Management Response | Textarea | User fills in |
| Design Effectiveness | Select | Compliant, Partial, Non-Compliant, N/A |
| Operating Effectiveness | Select | Same |
| Overall Effectiveness | Select | Same |
| Document Reference | Text input | e.g. TFS Manual §7, page 19 |
| Evidence of Implementation | Textarea | |
| Evidence Reference | Text input | e.g. ref.#2.5, 4.3 |
| Responsible Department | Select | Compliance, Business/Compliance, Risk Management, Internal Audit, Legal, Technology |
| Compliance Status | Select | Compliant, Gap Identified, In Progress, Closed |
| Target Date | Date picker | dd/mm/yyyy |
| Conclusion | Textarea (AI draft) | |
| Observation | Textarea (AI draft) | |
| Action Plan | Textarea (AI draft) | |
| Assigned To | Select | Head Compliance, Senior Officer, etc. |
| Sign off | Button | Marks row as signed off |

### 3.6 Export XLSX (`export-xlsx/` folder)
**Reference:** `export-xlsx/` folder (11 screenshots)

**Flow:** Fill form → Export XLSX button → download `Gap_Analysis_Working.xlsx`

#### Excel structure

**Sheet 1 — Cover**
| Row | Content |
|-----|---------|
| A1 | INTERNAL AUDIT GROUP |
| A3/B3 | Audit Title: Gap Analysis — {doc name} |
| A4/B4 | Document Reviewed: {internal doc} |
| A5/B5 | Benchmark Regulation: {regulation doc} |
| A6/B6 | Analysis Date: {date} |
| A7/B7 | Prepared by: Reguliq Platform — AI-assisted, compliance officer reviewed |
| A8/B8 | Version: v1.0 |
| Row 10 | NOTE: Columns D, O, P, Q are AI-drafted… |
| Row 11 | NOTE: Columns F, G, H, I, K, L, M, N, R must be completed by officer |

**Sheet 2 — AML Guidelines** (single wide table, 11 data rows)

| Col | Header |
|-----|--------|
| A | Serial # |
| B | Clause No. |
| C | Rules by CB UAE |
| D | Interpretation and expected action to comply |
| E | Actions Taken by Management |
| F | Design Effectiveness |
| G | Operate Effectiveness |
| H | Both (D & OE) |
| I | Document Reference |
| J | Policy Extract |
| K | Actions Taken to Implement the Regulatory Requirement |
| L | Evidence Reference |
| M | Responsible Dept. |
| N | Compliance Status |
| O | Conclusion |
| P | Observation |
| Q | Action Plans |
| R | Target Date |
| S | Assigned To |
| T | Signed Off |
| U | IA Comments, if any |

- Columns C and J = verbatim AI extraction from source PDFs
- Columns D, O, P, Q = AI-drafted (gaps, conclusion, observation, action plans)
- Columns F–N, R = officer-completed from web form
- Signed Off: "Yes" or "Pending"

---

## 4. Backend API (NestJS)

### Modules
```
api/src/
├── modules/
│   ├── health/
│   ├── dashboard/          # metrics, recent analyses
│   ├── regulations/        # regulation library CRUD
│   ├── documents/          # document library, upload
│   ├── analysis/           # gap analysis sessions
│   ├── compliance-items/   # per-row gap items + sign-off
│   ├── ai/                 # Gemini / Azure OpenAI integration
│   ├── landing-ai/         # Landing AI parse + extract
│   ├── pdf/                # PDF page render for modal viewer
│   └── excel/              # Gap_Analysis_Working.xlsx export
├── common/
│   ├── supabase/
│   └── database/
```

### Key endpoints
| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Health check |
| GET | `/dashboard` | Metrics + recent analyses |
| GET | `/regulations` | List with filters |
| POST | `/regulations` | Add regulation |
| GET | `/documents` | Document library |
| POST | `/documents/upload` | Upload file (multer) |
| POST | `/analysis/sessions` | Create + run gap analysis |
| GET | `/analysis/sessions/:id` | Session status + results |
| GET | `/analysis/sessions/:id/items` | All compliance items |
| PATCH | `/analysis/sessions/:id/items/:itemId` | Update form fields |
| POST | `/analysis/sessions/:id/items/:itemId/sign-off` | Sign off item |
| GET | `/pdf/page` | PDF page image + extracted text for modal |
| GET | `/excel/export/:sessionId` | Download XLSX |

### AI pipeline (gap analysis job)
1. Parse internal doc + regulation doc (Landing AI or pdf-parse)
2. Extract gov requirement points + internal policy points
3. Cross-reference each requirement → policy match
4. Classify severity: Critical / High / Medium / Low / Compliant
5. Generate gaps, conclusion, observation, action plan per item
6. Persist session + items to Supabase
7. Stream progress via SSE or WebSocket to frontend

### Environment variables
```
SUPABASE_URL=
SUPABASE_ANON_KEY=
SUPABASE_SERVICE_KEY=
DATABASE_URL=
GEMINI_API_KEY=
VISION_AGENT_API_KEY=          # Landing AI
LANDING_AI_API_BASE=
AZURE_OPENAI_API_KEY=          # optional
AZURE_OPENAI_ENDPOINT=
PORT=4000
JWT_SECRET=
NEXT_PUBLIC_API_URL=http://localhost:4000
NEXT_PUBLIC_SUPABASE_URL=
NEXT_PUBLIC_SUPABASE_ANON_KEY=
```

---

## 5. Database Schema (Supabase / PostgreSQL)

```sql
-- branches
branches (id, name, code, created_at)

-- regulations library
regulations (id, title, issuing_body, type, version, last_updated, status, clause_count, category, file_path)

-- documents
documents (id, branch_id, title, category, format, file_path, file_size, page_count, version, status, uploaded_at)

-- analysis sessions
analysis_sessions (
  id, branch_id, regulation_id,
  internal_doc_id, regulation_doc_id,
  status,           -- pending | processing | completed | failed
  progress_pct,
  total_items, critical_count, high_count, medium_count, low_count, compliant_count,
  created_at, completed_at
)

-- compliance items (one per gap/requirement row)
compliance_items (
  id, session_id, serial_no, clause_no, section_ref,
  severity,           -- critical | high | medium | low | compliant
  regulatory_text, regulatory_pdf_page,
  policy_text, policy_pdf_page,
  gaps_identified, management_response,
  design_effectiveness, operating_effectiveness, overall_effectiveness,
  document_reference, evidence_implementation, evidence_reference,
  responsible_department, compliance_status, target_date,
  conclusion, observation, action_plan,
  assigned_to, signed_off, signed_off_at,
  created_at, updated_at
)
```

---

## 6. Frontend (Next.js) Page Routes

```
app/
├── layout.tsx              # Shell: top nav + sidebar
├── page.tsx                # redirect → /dashboard
├── dashboard/page.tsx
├── reg-library/page.tsx
├── documents/page.tsx
├── analyse/
│   ├── page.tsx            # New gap analysis wizard
│   └── report/
│       └── [sessionId]/
│           └── page.tsx    # Full report workbench
├── components/
│   ├── layout/             # TopNav, Sidebar, AppShell
│   ├── dashboard/          # MetricCard, DonutChart, RemediationTable
│   ├── analyse/            # RegulationCard, UploadZone, AnalysisProgress
│   ├── report/             # GapItemRow, GapItemForm, SeverityBadge
│   ├── pdf/                # PdfViewerModal
│   └── ui/                 # Button, Badge, Select, Card
└── lib/
    ├── api.ts              # Axios/fetch client → NestJS
    └── supabase-browser.ts
```

---

## 7. Implementation Phases

### Phase 1 — Scaffold (Day 1)
- Monorepo setup, shared-types, env config
- App shell (layout, nav, sidebar, dark theme)
- Health endpoint + dashboard page (static mock data matching screenshots)

### Phase 2 — Libraries (Day 2)
- Regulation library page + API
- Document library page + upload
- Supabase migrations

### Phase 3 — Analysis wizard (Day 3–4)
- Regulation selection cards
- File upload (compliance + regulation PDFs)
- AI analysis job + progress panel
- Analysis complete panel

### Phase 4 — Full report (Day 5–6)
- Gap list with filters + expandable rows
- Per-item form with all fields + dropdowns
- PDF viewer modal (pdf.js)
- Sign-off flow

### Phase 5 — Export (Day 7)
- ExcelJS export matching `Gap_Analysis_Working.xlsx` template
- Export XLSX button on report page

### Phase 6 — Polish
- OneDrive sync stub
- Branch selector
- Tests (Vitest + RTL for components, Supertest for API)

---

## 8. Quality Requirements

- TypeScript strict mode, no `any`
- Zod validation on all API inputs
- Responsive layout (min 1280px desktop-first)
- Loading skeletons, error boundaries
- Audit log for sign-off and export actions
- Match screenshot UI pixel-close (spacing, colors, labels)
- All public functions have JSDoc
- Update CHANGELOG.md per phase

---

## 9. Screenshot Index (quick lookup)

### Root folder (analysis flow)
| File | Screen |
|------|--------|
| 01-dashboard.png | Dashboard |
| 02-regulation-library.png | Reg Library |
| 03-document-library.png | Documents |
| 04-new-gap-analysis-empty.png | Analyse — empty |
| 05-new-gap-analysis-upload.png | Analyse — upload zones |
| 06-analysis-in-progress.png | Analyse — progress |
| 07-analysis-briefing.png | Analyse — briefing stream |
| 08-analysis-complete.png | Analyse — complete |
| 09–12 | Early report views (also in view-full-report/) |

### view-full-report/ (19 files)
Full report workbench: list → detail → form → dropdowns → PDF modal → sign-off

### export-xlsx/ (11 files)
Form fill → Export XLSX → download modal → Excel Cover + AML Guidelines sheets (cols A–U)
