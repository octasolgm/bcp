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
| `ConnectionStrings__PostgreSQL` | **Supabase Session pooler URI** (same as local dev) |

**Or** use separate Supabase keys (easier if password contains `@`):

| `Supabase__DbHost` | `aws-1-ap-northeast-2.pooler.supabase.com` |
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
| `KAFKA_ENABLED` | `false` |

**Important:** `appsettings.json` in the published package has an **empty** `ConnectionStrings:PostgreSQL`. Either:

1. **Automatic (recommended for dev):** Run `.\scripts\sync-secrets.ps1` (or publish from VS — runs automatically) to create `appsettings.Secrets.json` from your local `appsettings.Development.json`. That file is **gitignored** but **included in the publish zip** so Azure gets DB + API keys without portal setup.

2. **Manual:** Set Azure Application settings (see table below).

Example pooler URI (replace password):

`postgresql://postgres.YOUR_PROJECT_REF:YOUR_PASSWORD@aws-1-ap-northeast-2.pooler.supabase.com:5432/postgres`

Optional first deploy:

| `Bcp__MigrateLocalDataToSupabase` | `false` on Azure (no local SQLite to migrate) |

Azure maps `Section__Key` to nested appsettings. Flat names like `DATABASE_URL` still work as overrides.

Use Key Vault for secrets in production.

## 3. Build & publish locally

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
