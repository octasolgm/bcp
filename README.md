# BCP - Bank Compliance Platform

## Overview
Bank Compliance Platform (BCP) is an AI-powered regulatory compliance and gap analysis tool built for banking institutions. It automates the comparison of regulatory requirement documents against internal process documents to identify compliance gaps.

## What Does This System Do?
1. **Upload** regulatory requirement files (PDF, HTML, JPEG, etc.)
2. **Upload** bank's internal process documents (Word, PDF, Excel, Email text)
3. **AI automatically compares** both sets of documents
4. **Generates** compliance report with 3 levels:
   - **Compliant** — bank process fully meets the requirement
   - **Partial Compliant** — bank process partially meets the requirement
   - **Non-Compliant** — bank process does not address the requirement
5. **Tracks** corrective actions with target dates and responsibilities
6. **Alerts** when deadlines are missed
7. **Dashboard** shows overall compliance health of the organization

## Project Structure (Monorepo)
```
bcp/
├── apps/
│   ├── api/              # NestJS API (Landing AI, compare, Supabase)
│   ├── web/              # Next.js web dashboard & compliance workbench
│   └── mobile/           # React Native mobile app
├── packages/
│   ├── shared-types/
│   └── shared-utils/
├── docs/
└── package.json
```

## Quick Start
```bash
npm install

# API (http://localhost:4000)
npm run dev:api

# Web (http://localhost:3000)
npm run dev:web

# BCP Web / Reguliq UI (http://localhost:3001)
npm run dev:bcpweb

# Mobile
npm run dev:mobile
```

Copy `.env.example` to `apps/api/.env` and set API keys (Landing AI, Gemini, Supabase).

## Documentation
See [docs/README.md](docs/README.md) for architecture, Supabase setup, Landing AI workflow, and developer guides.

## License
Proprietary — confidential banking software
