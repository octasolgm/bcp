# BCP API — Azure App Service Deployment

Deploy **bcp-api** as a **Linux .NET 8** App Service (separate from **bcp-web**).

## 1. Create App Service

Azure Portal → Create **Web App**:

| Setting | Value |
|---------|--------|
| Name | `bcp-api` (unique) |
| Runtime | .NET 8 (Linux) |
| Plan | B1 or higher |

## 2. Application settings (required — missing settings cause **HTTP 500.30**)

Configuration → Application settings → **New application setting**:

| Name | Value |
|------|--------|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `WEBSITES_PORT` | `8080` |
| `Bcp__UsePostgres` | `true` |
| `ConnectionStrings__PostgreSQL` | **Supabase transaction pooler URI (port 6543)** — see below |
| `Bcp__PostgresMaxPoolSize` | `5` (cap Npgsql connections per API instance) |

**Or** use separate Supabase keys (easier if password contains `@`):

| `Supabase__DbHost` | `aws-0-ap-northeast-1.pooler.supabase.com` |
| `Supabase__DbPort` | `6543` |
| `Supabase__DbUser` | `postgres.YOUR_PROJECT_REF` |
| `Supabase__DbPassword` | your DB password |
| `Supabase__DbName` | `postgres` |

Run locally to print values from your dev config:

```powershell
cd bcp-api/scripts
.\print-azure-settings.ps1
```

| Name | Value |
|------|--------|
| `Gemini__ApiKey` | your key |
| `LandingAi__ApiKey` | your Vision Agent key |
| `Bcp__CorsOrigins` | `https://YOUR-bcp-web.azurewebsites.net,http://localhost:3002` |
| `KAFKA_ENABLED` | `true` (same as monorepo `reguliq-dotnet`; requires Event Hubs connection strings in secrets) |

**Kafka on Azure:** `sync-secrets.ps1` copies `KAFKA_*` connection strings from `appsettings.Development.json` into `appsettings.Secrets.json` on publish — same as monorepo `.env`. Health should show `"transport":"kafka"`.

**Important:** `appsettings.json` in the published package has an **empty** `ConnectionStrings:PostgreSQL`. Either:

1. **Automatic (recommended for dev):** Run `.\scripts\sync-secrets.ps1` (or publish from VS — runs automatically) to create `appsettings.Secrets.json` from your local `appsettings.Development.json`. That file is **gitignored** but **included in the publish zip** so Azure gets DB + API keys without portal setup.

2. **Manual:** Set Azure Application settings (see table below).

Example **transaction** pooler URI (replace password; prefer **6543** for App Service):

`postgresql://postgres.prxmkrmwqxlltwjnazay:YOUR_PASSWORD@aws-0-ap-northeast-1.pooler.supabase.com:6543/postgres`

Do **not** use session pooler port **5432** for production API unless you have very low concurrency — Supabase caps session mode at ~15 clients **shared** by Azure API, local dev, workers, and SQL tools.

### Supabase connection pool exhaustion (live)

Symptoms: HTTP **500**, `EMAXCONNSESSION`, empty ND lists, wrong sidebar counts.

| Cause | Mitigation |
|--------|------------|
| Port **5432** (session pool) | Switch to **6543** (transaction pooler) in Azure settings + republish secrets |
| **Local dev + Azure** same DB | Both consume the same pool — use 6543 or pause local API when testing live |
| Old **bcp-web** bundle | Redeploy web so sidebar uses **`GET /nd/workspace/nav-counts`** (one call) not 8+ parallel list APIs |
| **Polling** (analysis status every 3.5s, dual-verify sessions every 15s per open tab) | Normal if status endpoints stay lightweight; many users × many tabs adds load — pooler 6543 handles this better |
| Npgsql default pool (100) | Set `Bcp__PostgresMaxPoolSize` = `5` per API instance |
| Kafka worker + HTTP on same App Service | Each job uses DB; keep concurrency at 2 or scale Supabase plan |

After changing DB port, **restart** the App Service and redeploy **both** API and web.

Optional first deploy:

| `Bcp__MigrateLocalDataToSupabase` | `false` on Azure (no local SQLite to migrate) |

Azure maps `Section__Key` to nested appsettings. Flat names like `DATABASE_URL` still work as overrides.

Use Key Vault for secrets in production.

## 3. Build + deploy (one command)

```powershell
cd bcp-api/scripts
az login
.\deploy-api.ps1
```

This will:
1. Sync `appsettings.Secrets.json` from `appsettings.Development.json`
2. Build `bcp-api.zip`
3. Push **all** Supabase/DB/API keys to Azure App Settings (from Development.json)
4. ZIP-deploy to `bcp-api-dev`

Manual build only:

```powershell
.\deploy-prep.ps1
```

## 4. Deploy (ZIP) — manual portal

```bash
cd bcp-api
dotnet publish Bcp.Api.csproj -c Release -o ./publish
```

## 4. Deploy (ZIP)

```bash
cd publish
# Windows PowerShell
Compress-Archive -Path * -DestinationPath ..\bcp-api.zip -Force

az webapp deploy --resource-group YOUR_RG --name bcp-api --src-path ..\bcp-api.zip --type zip
```

Or use **Deployment Center** → GitHub Actions / ZIP deploy in Azure Portal.

## 5. Verify

```bash
curl https://bcp-api-dev.azurewebsites.net/dual-verify-kafka/health
```

## Troubleshooting HTTP 500.30

1. **Enable logs:** App Service → **Monitoring** → **App Service logs** → Application logging **On** → save → **Log stream**.
2. **Missing database:** Log shows `PostgreSQL connection is required` → add `ConnectionStrings__PostgreSQL` in Configuration.
3. **Wrong port:** App must listen on `8080` on Azure (`WEBSITES_PORT=8080`). Recent builds use `AzureHosting` for this automatically.
4. **Runtime:** App Service → Configuration → General settings → **Stack** = `.NET 8`.

## Notes

- Do **not** commit `appsettings.Development.json` — use App Service Configuration or Key Vault in production.
- SQLite is not suitable on App Service; use PostgreSQL.
- Set `Bcp__CorsOrigins` (or `BCP_CORS_ORIGINS`) to your **bcp-web** URL before Angular can call the API.
