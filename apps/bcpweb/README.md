# BCP Web (Reguliq UI)

Next.js compliance workbench matching the Reguliq design in `docs/web-interface/`.

## Run

```bash
# From repo root
npm install
npm run dev:api    # port 4000
npm run dev:bcpweb # port 3001
```

Open http://localhost:3001

From the main BCP web dashboard (http://localhost:3000/dashboard), use **Open BCP Web (Reguliq)**.

## Pages

| Route | Description |
|-------|-------------|
| `/dashboard` | Compliance metrics, charts, recent analyses |
| `/reg-library` | Regulation library |
| `/documents` | Document library |
| `/analyse` | New gap analysis wizard |
| `/analyse/report/[sessionId]` | Full report workbench + Export XLSX |

## API

All endpoints are under `/bcpweb/*` on the NestJS API (isolated module, does not affect existing routes).

Demo session: `/analyse/report/demo-session-001`

## Env

Copy `.env.local.example` to `.env.local`:

```
NEXT_PUBLIC_API_URL=http://localhost:4000
```

Optional on main web app:

```
NEXT_PUBLIC_BCPWEB_URL=http://localhost:3001
```
