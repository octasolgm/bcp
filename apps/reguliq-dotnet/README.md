# Reguliq — .NET Core + Angular Stack

Full Reguliq dashboard and **Kafka dual verify** pipeline implemented in **ASP.NET Core 8** and **Angular 19**.

## Architecture

| Layer | Tech | Port |
|-------|------|------|
| Frontend | Angular 19 | **3002** |
| API | ASP.NET Core 8 | **5100** |
| Phase 1 (Landing AI) | NestJS bridge → `localhost:4000` | 4000 |
| Database | PostgreSQL (Supabase) or SQLite fallback | — |
| Queue | In-process local queue (Kafka optional via env) | — |

## Features

- **Dashboard** — pipeline health, persistence, Kafka sessions, MIS metrics
- **Dual Verify** — gov point selection, PDF upload, async worker (Landing AI + Gemini)
- **EF Core** — `dual_verify_sessions`, `dual_verify_point_jobs`, `landing_ai_compliance_sessions`
- **Disk fallback** — `data/dual-verify-kafka/{sessionId}.json`
- **Node bridge** — Phase 1 uses existing NestJS `/landing-ai/compare-point` (cache + ADE)

## Quick start

```bash
# From repo root — starts NestJS (4000), .NET API (5100), Angular (3002)
npm run dev:reguliq-dotnet
```

Or manually:

```bash
# Terminal 1 — NestJS (required for Landing AI Phase 1)
npm run dev:api

# Terminal 2 — .NET API
cd apps/reguliq-dotnet/src/Reguliq.Api && dotnet run

# Terminal 3 — Angular
cd apps/reguliq-dotnet/client/reguliq-web && npm install && npm start
```

## URLs

- Dashboard: http://localhost:3002/dashboard
- Dual Verify: http://localhost:3002/dual-verify
- .NET API Swagger: http://localhost:5100/swagger
- Health: http://localhost:5100/dual-verify-kafka/health

## Environment

Loads `../../.env` from repo root automatically. Key variables:

| Variable | Purpose |
|----------|---------|
| `DIRECT_URL` / `DATABASE_URL` | PostgreSQL (Supabase) |
| `GEMINI_API_KEY` | Phase 2 Gemini |
| `NODE_API_URL` | NestJS bridge (default `http://localhost:4000`) |
| `REGULIQ_API_PORT` | .NET API port (default `5100`) |
| `KAFKA_ENABLED` | Enable Kafka transport flag |

Without PostgreSQL, uses SQLite at `src/Reguliq.Api/data/reguliq.db`.

## API endpoints (same contract as NestJS)

- `GET /bcpweb/dashboard`
- `GET /dual-verify-kafka/health`
- `GET /dual-verify-kafka/sessions`
- `POST /dual-verify-kafka/jobs` (multipart)
- `GET /dual-verify-kafka/jobs/{id}`
- `POST /dual-verify-kafka/jobs/{id}/retry-failed`
- `GET /landing-ai/stored-points`
- `GET /landing-ai/compliance-sessions`
