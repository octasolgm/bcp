# Changelog

## [Unreleased]

### Added
- **Reguliq .NET + Angular stack** (`apps/reguliq-dotnet/`) — ASP.NET Core 8 API + Angular 19 UI
  - Dashboard + Kafka dual verify workbench (ports 5100 API, 3002 UI)
  - EF Core persistence (SQLite default, optional PostgreSQL via `REGULIQ_USE_POSTGRES=true`)
  - Local job queue worker, Landing AI Phase 1 via NestJS bridge, Gemini Phase 2
  - Script: `npm run dev:reguliq-dotnet`
  - Reuses shared `KafkaDualVerifyWorkbench` from `apps/web` (combined report, PDF/Excel export, load saved sessions)
  - Dashboard: live pipeline health, persistence status, recent Kafka sessions, quick actions
  - Nav: **Dual Verify** in AppShell + sidebar
  - API: `/dual-verify-kafka/*` (jobs, progress, retry-failed, sessions list)
  - UI: `/landing-ai/kafka-dual-verify`
  - Local in-process queue (dev) + Azure Event Hubs Kafka (prod)
  - Docs: `KAFKA_DUAL_VERIFY_IMPLEMENTATION.md`, `BCP_KAFKA_DEV_CONFIGURED.md`, migration `003_dual_verify_kafka.sql`
- **apps/bcpweb** — Reguliq-style compliance workbench (Next.js, port 3001)
  - Dashboard, Regulation Library, Document Library, Gap Analysis wizard, Full Report workbench
  - PDF viewer modal, sign-off flow, Export XLSX (`Gap_Analysis_Working.xlsx`)
- **apps/api** — `BcpwebModule` at `/bcpweb/*` (isolated from existing API routes)
  - Real Gemini gap analysis pipeline (`BcpwebAnalysisService`): extract regulation points + per-point compare with uploaded PDFs
  - Multipart upload endpoint `POST /bcpweb/analysis/sessions/upload`
- Root script `npm run dev:bcpweb`
- Dashboard link **Open BCP Web (Reguliq)** on `apps/web` MIS Dashboard

### Changed
- **Kafka dual verify UI** — workbench-style combined report: full Pass 1/Pass 2 cards (no truncation), summary stats, load saved sessions before run, accumulate results (6 + 50 = 56), Summary PDF / Detail PDF / Excel export
- **Kafka dual verify** — cost/quality optimizations: Phase 1 Supabase cache by default (`forceRefresh` off), internal markdown supplement for Phase 2, UI defaults (3 points, `gemini-2.5-flash-lite`), force-refresh checkbox
- **Docs** — `KAFKA_DUAL_VERIFY_PROCESS.md` (full run process + cost vs quality); updated `KAFKA_DUAL_VERIFY_SIMPLE.md` to reflect implemented pipeline

### Fixed
- **Kafka dual verify persistence** — disk backup (`data/dual-verify-kafka/`) when Supabase tables missing; sessions survive API restart; block paid runs if nothing durable; manual migration guide `docs/supabase/APPLY_MIGRATIONS_MANUAL.md`
