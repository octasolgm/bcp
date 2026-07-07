# BCP Web (Angular 19)

Standalone Angular UI for Reguliq dashboard and dual-verify workbench.

Talks to **bcp-api** on port **5100** (not NestJS 4000 by default).

## Prerequisites

- Node.js 18+
- **bcp-api** running on http://localhost:5100

## Setup

```bash
cd bcp-web
npm install
```

API URL for local dev is in `src/environments/environment.ts`:

```typescript
apiUrl: 'http://localhost:5100',
```

## Run

```bash
cd bcp-web
npm start
```

Open http://localhost:3002/dashboard

## Production build (Azure)

1. Edit `src/environments/environment.production.ts`:

```typescript
apiUrl: 'https://bcp-api.azurewebsites.net',
```

2. Build:

```bash
npm run build:prod
```

Output: `dist/bcp-web/browser/`

## Deploy

See [DEPLOYMENT.md](./DEPLOYMENT.md) for Azure App Service.

## Optional NestJS (port 4000)

Only for legacy Kafka sessions. Set in `environment.ts`:

```typescript
nestjsApiUrl: 'http://localhost:4000',
```

Leave empty for .NET-only setup.
