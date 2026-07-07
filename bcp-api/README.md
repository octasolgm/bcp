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

2. Copy the appsettings template and set **the same** Supabase credentials for every developer:

```bash
cd bcp-api
copy appsettings.Development.example.json appsettings.Development.json
```

Edit `appsettings.Development.json`:

```json
{
  "Bcp": { "UsePostgres": true },
  "ConnectionStrings": {
    "PostgreSQL": "postgresql://postgres.YOUR_REF:PASSWORD@aws-0-REGION.pooler.supabase.com:5432/postgres"
  },
  "Supabase": {
    "DbHost": "aws-0-REGION.pooler.supabase.com",
    "DbUser": "postgres.YOUR_REF",
    "DbPassword": "PASSWORD"
  },
  "Gemini": { "ApiKey": "your-key" }
}
```

Prefer `Supabase:DbPassword` when the password contains `@` (no URL encoding needed).

3. On first startup, existing **local** data (`data/reguliq.db`, `data/dual-verify-kafka/*.json`) is imported into Supabase automatically (`Bcp:MigrateLocalDataToSupabase=true`).

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

All developers with the same Supabase connection see the same sessions.

## Configuration (`appsettings`)

| Key | Required | Purpose |
|-----|----------|---------|
| `ConnectionStrings:PostgreSQL` | **Yes** | Supabase Session pooler URI |
| `Supabase:DbPassword` | Recommended | Plain password (use when password contains `@`) |
| `Bcp:UsePostgres` | Yes (`true`) | Use Supabase instead of SQLite |
| `Gemini:ApiKey` | For live runs | Gemini Phase 2 |
| `Bcp:MigrateLocalDataToSupabase` | No (`true`) | Import local SQLite/JSON on startup |
| `Bcp:AllowSqlite` | No | `true` = offline local-only (not shared) |
| `Bcp:CorsOrigins` | No | Angular origins |

## Deploy

See [DEPLOYMENT.md](./DEPLOYMENT.md) for Azure App Service.
