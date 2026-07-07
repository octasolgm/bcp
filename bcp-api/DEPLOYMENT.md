# BCP API — Azure App Service Deployment

Deploy **bcp-api** as a **Linux .NET 8** App Service (separate from **bcp-web**).

## 1. Create App Service

Azure Portal → Create **Web App**:

| Setting | Value |
|---------|--------|
| Name | `bcp-api` (unique) |
| Runtime | .NET 8 (Linux) |
| Plan | B1 or higher |

## 2. Application settings

Configuration → Application settings:

| Name | Value |
|------|--------|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `WEBSITES_PORT` | `8080` |
| `Bcp__UsePostgres` | `true` |
| `ConnectionStrings__PostgreSQL` | **Same Supabase URI for all devs** |
| `Bcp__MigrateLocalDataToSupabase` | `true` (first deploy) |
| `Gemini__ApiKey` | your key |
| `Bcp__CorsOrigins` | `https://YOUR-bcp-web.azurewebsites.net` |
| `KAFKA_ENABLED` | `false` (or Kafka vars) |

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
curl https://bcp-api.azurewebsites.net/dual-verify-kafka/health
```

## Notes

- Do **not** commit `appsettings.Development.json` — use App Service Configuration or Key Vault in production.
- SQLite is not suitable on App Service; use PostgreSQL.
- Set `Bcp__CorsOrigins` (or `BCP_CORS_ORIGINS`) to your **bcp-web** URL before Angular can call the API.
