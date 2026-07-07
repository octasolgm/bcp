# BCP API (.NET 8)

Standalone ASP.NET Core API for Reguliq compliance dashboard and Kafka dual-verify pipeline.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Setup

```bash
cd bcp-api
copy .env.example .env    # Windows
# cp .env.example .env    # macOS/Linux
```

Edit `.env` with your keys (`GEMINI_API_KEY`, `DATABASE_URL`, etc.).

## Run

```bash
cd bcp-api
dotnet run
```

- API: http://localhost:5100
- Swagger: http://localhost:5100/swagger
- Health: http://localhost:5100/dual-verify-kafka/health

## Build & test

```bash
dotnet build Bcp.Api.sln
dotnet test Bcp.Api.sln
```

## Environment

Loads **`bcp-api/.env`** automatically on startup.

| Variable | Default | Purpose |
|----------|---------|---------|
| `BCP_API_PORT` | `5100` | Local API port |
| `GEMINI_API_KEY` | — | Required for live dual-verify |
| `REGULIQ_USE_POSTGRES` | `false` | `true` + `DATABASE_URL` for PostgreSQL |
| `BCP_CORS_ORIGINS` | `http://localhost:3002` | Angular origins (comma-separated) |
| `KAFKA_ENABLED` | `false` | Azure Event Hubs when `true` |

## Deploy

See [DEPLOYMENT.md](./DEPLOYMENT.md) for Azure App Service.
