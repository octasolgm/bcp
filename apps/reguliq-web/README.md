# Reguliq Web (Angular)

Standalone Angular 19 UI for the Reguliq compliance dashboard and Kafka dual-verify workbench.

**API target:** Reguliq .NET API (`apps/reguliq-dotnet`) — **not** NestJS on port 4000 by default.

## Ports

| Service | Local URL | Purpose |
|---------|-----------|---------|
| **Angular UI** | http://localhost:3002 | This app |
| **.NET API** | http://localhost:5100 | Primary API (`environment.apiUrl`) |
| NestJS (optional) | http://localhost:4000 | Legacy sessions only — leave `nestjsApiUrl` empty |

## Why port 4000 appeared before

The dashboard used to call **NestJS** (`nestjsApiUrl: http://localhost:4000`) to merge old Kafka sessions started from Next.js. All main features (dashboard, dual verify, gov points) use the **.NET API on 5100**. Nest is now **optional** — set `nestjsApiUrl` only if you need legacy sessions.

## Run locally

```bash
# From repo root — terminal 1: .NET API
npm run dev:reguliq-api

# Terminal 2: Angular UI
npm run dev:reguliq-web
```

Or start everything (Nest + .NET + Angular):

```bash
npm run dev:reguliq-dotnet
```

Open http://localhost:3002/dashboard

## Environment files

| File | When used |
|------|-----------|
| `src/environments/environment.ts` | `ng serve` — local dev (`apiUrl: http://localhost:5100`) |
| `src/environments/environment.production.ts` | `ng build --configuration production` — Azure/hosted |

### Local (`environment.ts`)

```typescript
apiUrl: 'http://localhost:5100',
nestjsApiUrl: '',  // no :4000 calls
```

### Azure (`environment.production.ts`)

Update `apiUrl` to your .NET App Service URL before building:

```typescript
apiUrl: 'https://reguliq-api.azurewebsites.net',
```

Build:

```bash
npm run build:prod --workspace=apps/reguliq-web
```

Deploy `dist/reguliq-web/browser/` to Azure App Service (separate from .NET API).

## Optional: enable NestJS legacy sessions

In `environment.ts`, set:

```typescript
nestjsApiUrl: 'http://localhost:4000',
```

Only then will the UI call port 4000 (for old Kafka sessions).

## CORS on .NET API

Add your Angular origin to repo root `.env`:

```env
REGULIQ_CORS_ORIGINS=http://localhost:3002,https://reguliq-web.azurewebsites.net
```
