# BCP — Bank Compliance Platform

Two **fully separate** apps in this repo:

| Folder | Stack | Port (local) |
|--------|--------|----------------|
| **[bcp-api](./bcp-api)** | ASP.NET Core 8 | http://localhost:5100 |
| **[bcp-web](./bcp-web)** | Angular 19 | http://localhost:3002 |

No monorepo workspaces — each app has its own dependencies and README.

Remote: [github.com/octasolgm/bcp](https://github.com/octasolgm/bcp.git)

## Quick start (local)

### Terminal 1 — API

```bash
cd bcp-api
copy appsettings.Development.example.json appsettings.Development.json   # first time only; edit keys
dotnet run
```

### Terminal 2 — Web

```bash
cd bcp-web
npm install
npm start
```

Open **http://localhost:3002/dashboard**

## Environment

- **API:** `bcp-api/appsettings.Development.json` (copy from `appsettings.Development.example.json` — never commit)
- **Web:** `bcp-web/src/environments/environment.ts` (local) and `environment.production.ts` (Azure)

## Deploy to Azure

Each app has its own guide:

- [bcp-api/DEPLOYMENT.md](./bcp-api/DEPLOYMENT.md) — .NET App Service
- [bcp-web/DEPLOYMENT.md](./bcp-web/DEPLOYMENT.md) — Angular static App Service

Deploy **two** App Services: `bcp-api` + `bcp-web`.

## Health check

```bash
curl http://localhost:5100/dual-verify-kafka/health
```
