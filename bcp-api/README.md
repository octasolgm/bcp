# BCP API (.NET 8)

Standalone ASP.NET Core API for Reguliq compliance dashboard and Kafka dual-verify pipeline.

**Persistence: Supabase PostgreSQL (shared across all developers).**

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Supabase project** with PostgreSQL connection string

## Setup (team — shared data)

1. Run Supabase SQL migrations (once per project) from `scripts/supabase/` or the main BCP repo `docs/supabase/migrations/`:
   - `002_compliance_sessions.sql`
   - `003_dual_verify_kafka.sql`
   - `004_bcp_api_extra_columns.sql`

2. Copy env file and set **the same** `DATABASE_URL` for every developer:

```bash
cd bcp-api
copy .env.example .env
```

```env
REGULIQ_USE_POSTGRES=true
DATABASE_URL=postgresql://postgres:PASSWORD@db.xxx.supabase.co:5432/postgres
GEMINI_API_KEY=your-key
```

3. On first startup, existing **local** data (`data/reguliq.db`, `data/dual-verify-kafka/*.json`) is imported into Supabase automatically (`MIGRATE_LOCAL_DATA_TO_SUPABASE=true`).

## Run

```bash
cd bcp-api
dotnet run
```

- API: http://localhost:5100
- Health: http://localhost:5100/dual-verify-kafka/health → `persistence.mode` should be **`supabase`**

## Verify shared storage

```bash
curl http://localhost:5100/dual-verify-kafka/health
curl http://localhost:5100/dual-verify-kafka/sessions
```

All developers with the same `DATABASE_URL` see the same sessions.

## Environment

| Variable | Required | Purpose |
|----------|----------|---------|
| `DATABASE_URL` | **Yes** | Supabase PostgreSQL URI |
| `REGULIQ_USE_POSTGRES` | Yes (`true`) | Use Supabase instead of SQLite |
| `GEMINI_API_KEY` | For live runs | Gemini Phase 2 |
| `MIGRATE_LOCAL_DATA_TO_SUPABASE` | No (`true`) | Import local SQLite/JSON on startup |
| `BCP_ALLOW_SQLITE` | No | `true` = offline local-only (not shared) |
| `BCP_CORS_ORIGINS` | No | Angular origins |

## Deploy

See [DEPLOYMENT.md](./DEPLOYMENT.md) for Azure App Service.
