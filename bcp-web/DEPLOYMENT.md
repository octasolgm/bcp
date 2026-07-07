# BCP Web — Azure App Service Deployment

Deploy **bcp-web** as a **separate** App Service from **bcp-api** (e.g. `bcp-web-dev`).

Angular builds to static files — you deploy the **`dist`** output, not the whole repo.

## 1. Create App Service (if not created)

| Setting | Value |
|---------|--------|
| Name | `bcp-web-dev` |
| Runtime | .NET 8 or Node 20 (Linux) — static files only |
| OS | Windows (IIS) or Linux |

## 2. Set production API URL (before build)

Edit `src/environments/environment.production.ts`:

```typescript
export const environment = {
  production: true,
  apiUrl: 'https://bcp-api-dev.azurewebsites.net',
  nestjsApiUrl: '',
};
```

This is baked into the build — change it **before** `npm run build:prod`.

## 3. Build + zip (one command)

```powershell
cd bcp-web/scripts
.\deploy-prep.ps1
```

Or manually:

```powershell
cd bcp-web
npm ci
npm run build:prod
```

**Deploy this folder** (contents inside, not the parent):

```
dist/reguliq-web/browser/
```

Contains `index.html`, `web.config` (SPA routing), and hashed JS/CSS.

## 4. Deploy to Azure

### Option A — ZIP deploy (easiest)

1. `deploy-prep.ps1` creates `bcp-web/bcp-web-dist.zip`
2. Azure Portal → **bcp-web-dev** → **Deployment Center** → ZIP deploy  
   Or Advanced Tools (Kudu) → drag zip to `site/wwwroot`

### Option B — Visual Studio / FTP

Upload everything inside `dist/reguliq-web/browser/` to `wwwroot`.

### Option C — Azure CLI

```bash
az webapp deploy --resource-group YOUR_RG --name bcp-web-dev --src-path bcp-web-dist.zip --type zip
```

## 5. SPA routing

**Windows App Service (IIS):** `public/web.config` is copied into dist — routes like `/dashboard` work.

**Linux App Service:** Configuration → General settings → Startup Command:

```bash
npx serve -s /home/site/wwwroot -l 8080
```

Set `WEBSITES_PORT=8080`.

## 6. CORS on API

On **bcp-api**, add your web URL to `appsettings.Production.json`:

```json
"CorsOrigins": "http://localhost:3002,https://bcp-web-dev.azurewebsites.net"
```

Republish **bcp-api** after changing CORS.

## Verify

1. Open `https://bcp-web-dev.azurewebsites.net`
2. Dashboard loads
3. Browser devtools → Network — API calls go to `bcp-api-dev.azurewebsites.net` (no CORS errors)

## Common mistakes

| Mistake | Fix |
|---------|-----|
| Deploying whole `dist/` folder | Deploy **`dist/reguliq-web/browser/`** contents only |
| Wrong API URL | Rebuild after editing `environment.production.ts` |
| CORS error | Add web URL to API `CorsOrigins` and republish API |
| 404 on refresh | Ensure `web.config` (Windows) or `serve -s` (Linux) is set |
