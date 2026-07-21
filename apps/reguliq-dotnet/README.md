# Reguliq — .NET Core + Angular Stack

Full Reguliq dashboard and **Kafka dual verify** pipeline in **ASP.NET Core 8** and **Angular 19**, matching the NestJS `dual-verify-kafka` API contract and bcpweb workbench UX.

## Architecture

| Layer | Tech | Port |
|-------|------|------|
| Frontend | Angular 19 | **3002** |
| API | ASP.NET Core 8 | **5100** |
| Phase 1 (Landing AI) | NestJS bridge → `localhost:4000` | 4000 |
| Database | PostgreSQL (Supabase) or SQLite fallback | — |
| Queue | Confluent.Kafka (Azure Event Hubs) or in-process local queue | — |

## Features

- **App shell** — top nav + sidebar (Dashboard, Dual Verify), Reguliq dark theme (`#0b111b`, emerald accents)
- **Dashboard** — pipeline cards, Kafka/compliance sessions, MIS metrics, remediation, quick actions
- **Dual Verify workbench** — gov point chapter grouping, combined report bag, saved session load, live progress, retry failed, PDF/Excel export
- **Kafka transport** — jobs/retry/dlq/results topics when `KAFKA_ENABLED=true`; local queue fallback
- **EF Core** — `dual_verify_sessions`, `dual_verify_point_jobs`, `landing_ai_compliance_sessions`
- **Worker** — Phase 1 (NestJS bridge), Phase 2 (Gemini), agreement, transient retry, incremental compliance save

## Quick start

```bash
# From repo root — NestJS (4000), .NET API (5100), Angular (3002)
npm run dev:reguliq-dotnet
```

Or manually:

```bash
# Terminal 1 — NestJS (required for Landing AI Phase 1 + Supabase cache)
npm run dev:api

# Terminal 2 — .NET API
cd apps/reguliq-dotnet/src/Reguliq.Api && dotnet run

# Terminal 3 — Angular (standalone app)
cd apps/reguliq-web && npm install && npm start
```

### Verify

```bash
curl http://localhost:5100/dual-verify-kafka/health
dotnet build apps/reguliq-dotnet/src/Reguliq.Api
cd apps/reguliq-web && npm run build
dotnet test apps/reguliq-dotnet/tests/Reguliq.Api.Tests
```

## URLs

- Dashboard: http://localhost:3002/dashboard
- Dual Verify: http://localhost:3002/dual-verify
- .NET API Swagger: http://localhost:5100/swagger
- Health: http://localhost:5100/dual-verify-kafka/health

## Environment

Copy `apps/reguliq-dotnet/.env.example` values into the **repo root** `.env` (loaded automatically by `Program.cs`).

| Variable | Purpose |
|----------|---------|
| `REGULIQ_USE_POSTGRES` | `true` to use PostgreSQL instead of SQLite |
| `DIRECT_URL` / `REGULIQ_DATABASE_URL` / `DATABASE_URL` | PostgreSQL URI (password `@` handled via Npgsql builder) |
| `GEMINI_API_KEY` | Phase 2 Gemini |
| `NODE_API_URL` | NestJS bridge (default `http://localhost:4000`) |
| `REGULIQ_API_PORT` | .NET API port (default `5100`) |
| `KAFKA_ENABLED` | `true` + brokers/password → Kafka transport |
| `KAFKA_BROKERS`, `KAFKA_*_CONNECTION_STRING` | Azure Event Hubs |
| `DUAL_VERIFY_INTERNAL_PDF_PATH` | Default IMPTFS PDF for Phase 2 |

Angular API URL: `apps/reguliq-web/src/environments/environment.ts` → `apiUrl: 'http://localhost:5100'` (local) or `environment.production.ts` for Azure.

## API endpoints (NestJS parity)

- `GET /bcpweb/dashboard`
- `GET /dual-verify-kafka/health`
- `GET /dual-verify-kafka/sessions`
- `POST /dual-verify-kafka/jobs` (multipart)
- `POST /dual-verify-kafka/jobs/json`
- `GET /dual-verify-kafka/jobs/{id}`
- `GET /dual-verify-kafka/jobs/{id}/results`
- `POST /dual-verify-kafka/jobs/{id}/retry-failed`
- `GET /landing-ai/stored-points`
- `POST /landing-ai/seed/builtin`
- `GET|POST /landing-ai/compliance-sessions`

## Still delegated to NestJS (`apps/api`)

- Landing AI ADE parse/extract/compare with Supabase cache (Phase 1 via `NODE_API_URL`)
- Full `landing-ai` module (parse, extract, cache-status, builtin seed to Supabase)
- bcpweb sync analyse, reg library, documents, PDF viewer
- Supabase migration apply / `set_updated_at` triggers

## Tests

```bash
dotnet test apps/reguliq-dotnet/tests/Reguliq.Api.Tests
```

Unit tests cover `DualVerifyAgreementService` (aligned, mismatch, confidence gap).
