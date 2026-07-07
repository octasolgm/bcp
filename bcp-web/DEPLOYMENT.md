# BCP Web — Azure App Service Deployment

Deploy **bcp-web** as a **separate** App Service from **bcp-api**.

## 1. Create App Service

| Setting | Value |
|---------|--------|
| Name | `bcp-web` |
| Runtime | Node 20 LTS (Linux) |

## 2. Set production API URL

Before building, edit `src/environments/environment.production.ts`:

```typescript
export const environment = {
  production: true,
  apiUrl: 'https://bcp-api.azurewebsites.net',
  nestjsApiUrl: '',
};
```

## 3. Build

```bash
cd bcp-web
npm ci
npm run build:prod
```

## 4. Deploy static files

Zip contents of `dist/bcp-web/browser/` and deploy to **bcp-web** App Service (ZIP deploy or GitHub Actions).

```bash
cd dist/bcp-web/browser
Compress-Archive -Path * -DestinationPath ..\..\..\bcp-web.zip -Force
az webapp deploy --resource-group YOUR_RG --name bcp-web --src-path ..\..\..\bcp-web.zip --type zip
```

## 5. Startup command (SPA routing)

Configuration → General settings → **Startup Command**:

```bash
npx serve -s /home/site/wwwroot -l 8080
```

Set `WEBSITES_PORT=8080`.

## 6. CORS on API

On **bcp-api**, set:

```env
BCP_CORS_ORIGINS=https://bcp-web.azurewebsites.net
```

## Verify

Open `https://bcp-web.azurewebsites.net` — dashboard should load and call **bcp-api** without CORS errors.
