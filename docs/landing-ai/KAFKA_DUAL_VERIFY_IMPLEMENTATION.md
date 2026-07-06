# Kafka Dual Verify — Implementation Guide

**Status:** Implemented in BCP monorepo  
**UI:** `http://localhost:3000/landing-ai/kafka-dual-verify`  
**API:** `http://localhost:4000/dual-verify-kafka/*`

---

## 1. What was built

Professional async dual verify pipeline:

```text
UI (kafka-dual-verify page)
  → POST /dual-verify-kafka/jobs
  → API creates session + enqueues 1 job per gov point
  → Kafka (Azure Event Hubs) OR local in-process queue
  → Worker: Phase 1 Landing AI → Phase 2 Gemini → agreement
  → Save to dual_verify_point_jobs + compliance_sessions
  → UI polls GET /dual-verify-kafka/jobs/:sessionId
```

| Layer | Location |
|-------|----------|
| Kafka transport | `apps/api/src/modules/kafka/` |
| Dual verify jobs | `apps/api/src/modules/dual-verify-kafka/` |
| UI page | `apps/web/src/app/landing-ai/kafka-dual-verify/page.tsx` |
| DB migration | `docs/supabase/migrations/003_dual_verify_kafka.sql` |
| Azure setup | `docs/landing-ai/AZURE_KAFKA_SETUP_STEPS.md` |
| Architecture | `docs/landing-ai/KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md` |

---

## 2. File map (every code file)

### Kafka module

| File | Purpose |
|------|---------|
| `kafka/kafka.module.ts` | Nest module exports |
| `kafka/kafka.config.ts` | Env config (brokers, topics, transport mode) |
| `kafka/kafka-producer.service.ts` | Publish to Azure Event Hubs; optional consumer |
| `kafka/local-job-queue.service.ts` | In-process queue when `KAFKA_ENABLED=false` |

### Dual verify Kafka module

| File | Purpose |
|------|---------|
| `dual-verify-kafka/dual-verify-kafka.module.ts` | Wires Landing AI + AI + Kafka |
| `dual-verify-kafka/dual-verify-kafka.controller.ts` | REST API |
| `dual-verify-kafka/dual-verify-kafka.service.ts` | Create jobs, progress, retry |
| `dual-verify-kafka/dual-verify-kafka-worker.service.ts` | **Worker** — Phase 1 + Phase 2 |
| `dual-verify-kafka/dual-verify-kafka-store.service.ts` | Session/point persistence |
| `dual-verify-kafka/dual-verify-kafka.types.ts` | Message + session types |

### Prompts & agreement (ported from web)

| File | Purpose |
|------|---------|
| `utils/reference-map-prompt.ts` | Phase 2 base prompt (`REFERENCE_MAP_PROMPT`) |
| `utils/dual-verify-prompt.ts` | `buildDualVerifyPrompt()` — Pass 2 addendum |
| `utils/parse-reference-response.ts` | Parse plain-text gap record |
| `utils/dual-verify-agreement.ts` | `compareDualVerifyResults()` |

### Tests

| File | Purpose |
|------|---------|
| `utils/dual-verify-agreement.spec.ts` | Agreement logic unit tests |
| `utils/dual-verify-prompt.spec.ts` | Prompt builder tests |
| `scripts/kafka-smoke-test.mjs` | Azure Kafka connectivity test |

### Reused existing services (not duplicated)

| Service | File | Used for |
|---------|------|----------|
| `LandingAiService.comparePoint()` | `landing-ai/services/landing-ai.service.ts` | **Phase 1** |
| `BcpAnalyzeService.analyze()` | `ai/services/bcp-analyze.service.ts` | **Phase 2** |
| `LandingAiService.saveComplianceSession()` | same | Incremental Supabase save |
| `LandingAiSeedService.getStoredPoints()` | `landing-ai-seed.service.ts` | Load gov points |
| `filterComparableGovPoints()` | `utils/gov-point-filter.ts` | Section-level points |

---

## 3. API endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/dual-verify-kafka/health` | Transport mode + topic names |
| `POST` | `/dual-verify-kafka/jobs/json` | Start job (JSON body) |
| `POST` | `/dual-verify-kafka/jobs` | Start job (multipart + optional PDF) |
| `GET` | `/dual-verify-kafka/jobs/:sessionId` | Progress + all point statuses |
| `GET` | `/dual-verify-kafka/jobs/:sessionId/results` | Completed/failed results |
| `POST` | `/dual-verify-kafka/jobs/:sessionId/retry-failed` | Re-queue failed points |

### Create job (JSON)

```json
POST /dual-verify-kafka/jobs/json
{
  "pointIds": ["2.1", "2.3", "3.7"],
  "granularity": "section",
  "govDocId": "gov-tfs-guidelines",
  "internalDocId": "internal-imptfs",
  "phase2Model": "gemini-2.5-flash-lite"
}
```

### Kafka message (one per point)

```json
{
  "schemaVersion": "1.0",
  "messageId": "uuid",
  "jobId": "uuid",
  "sessionId": "uuid",
  "pointId": "2.1",
  "govText": "...",
  "granularity": "section",
  "govDocId": "gov-tfs-guidelines",
  "internalDocId": "internal-imptfs",
  "phase2Model": "gemini-2.5-flash-lite",
  "attempt": 1,
  "maxAttempts": 3
}
```

---

## 4. Workflow (step by step)

### Step A — Prerequisites

```bash
# 1. Env keys
GEMINI_API_KEY=...
VISION_AGENT_API_KEY=...   # Phase 1 Landing AI

# 2. Seed gov points (once)
curl -X POST http://localhost:4000/landing-ai/seed/builtin

# 3. Optional: Azure Kafka
KAFKA_ENABLED=true
KAFKA_BROKERS=your-namespace.servicebus.windows.net:9093
KAFKA_PRODUCER_CONNECTION_STRING=Endpoint=sb://...

# 4. Optional: internal PDF for Phase 2 on server
DUAL_VERIFY_INTERNAL_PDF_PATH=/path/to/IMPTFS.pdf
```

### Step B — User runs pipeline

1. Open `/landing-ai/kafka-dual-verify`
2. Load gov points (from Supabase cache)
3. Select points + attach internal PDF
4. Click **Run Kafka dual verify**
5. UI polls progress every 2.5s

### Step C — Worker per message

```text
1. Idempotency check — skip if point already completed
2. Mark point status = running
3. PHASE 1 — LandingAiService.comparePoint()
     Prompt: COMPARE_PROMPT_V2 (compliance-compare-prompts.ts)
     Input: gov point + IMPTFS markdown (Supabase parse cache)
4. PHASE 2 — BcpAnalyzeService.analyze()
     Prompt: buildDualVerifyPrompt() (REFERENCE_MAP_PROMPT + Pass 1 output)
     Input: internal PDF + prompt
5. Agreement — compareDualVerifyResults()
6. Save point job + incremental compliance session
7. Commit Kafka offset (after success)
```

### Step D — Failure handling

| Error type | Action |
|------------|--------|
| Transient (429, 503, timeout) | Retry up to 3× → `dual-verify-retry` topic |
| Permanent / max retries | Mark failed → `dual-verify-dlq` |
| Other points | Continue independently |

---

## 5. Prompts (exact sources)

### Phase 1 — Landing AI

**File:** `apps/api/src/modules/landing-ai/prompts/compliance-compare-prompts.ts`  
**Function:** `COMPARE_PROMPT_V2`  
**Schema:** `compliance-comparison-v2.schema.json`  
**Service:** `LandingAiClientService.compareRequirement()`

Key rules:
- Whole-point semantic compare (not keyword matching)
- Output JSON: status, confidence, fulfilled_clauses, corrective_action_plan

### Phase 2 — Gemini dual verify

**Base prompt:** `dual-verify-kafka/utils/reference-map-prompt.ts` → `REFERENCE_MAP_PROMPT`  
**Builder:** `dual-verify-kafka/utils/dual-verify-prompt.ts` → `buildDualVerifyPrompt()`

Structure:
```text
REFERENCE_MAP_PROMPT
+ DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)
+ LANDING AI PASS 1 (reference only): {phase1 message}
+ REQUIREMENT POINT TO CHECK: {gov point}
```

**Original web copy:** `apps/web/src/lib/landing-ai/dual-verify-prompt.ts` (kept in sync conceptually)

### Agreement check

**File:** `dual-verify-kafka/utils/dual-verify-agreement.ts`  
**Function:** `compareDualVerifyResults(landingMessage, llmMessage)`

Returns: `aligned` | `status_mismatch` | `confidence_gap` | `both_non_compliant`

---

## 6. Transport modes

| Mode | When | How |
|------|------|-----|
| **local** | Default dev (`KAFKA_ENABLED` unset/false) | `LocalJobQueueService` in same Nest process |
| **kafka** | Azure configured + `KAFKA_ENABLED=true` | `KafkaProducerService` → Event Hubs |

Same message contract — swap transport without UI changes.

---

## 7. Database tables

**Migration:** `docs/supabase/migrations/003_dual_verify_kafka.sql`

| Table | Purpose |
|-------|---------|
| `dual_verify_sessions` | Batch session progress |
| `dual_verify_point_jobs` | Per-point status + results |

Falls back to **in-memory** if tables not migrated.

Compliance results also saved to existing `landing_ai_compliance_sessions` (granularity `dual-section` / `dual-leaf`).

---

## 8. How to run

```bash
# Terminal 1 — API (includes worker)
npm run dev:api

# Terminal 2 — Web
npm run dev:web

# Tests
npm run test --workspace=apps/api

# Kafka smoke (after Azure setup)
npm run kafka:smoke --workspace=apps/api
```

Open: **http://localhost:3000/landing-ai/kafka-dual-verify**

---

## 9. .NET Core migration (later)

Keep unchanged:
- Kafka topics + message JSON (`dual-verify-kafka.types.ts` contract)
- REST API paths (or proxy through .NET gateway)
- DB tables

Replace:
- `DualVerifyKafkaWorkerService` → .NET worker using Confluent.Kafka

See: `KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md` §10

---

## 10. Troubleshooting

| Issue | Fix |
|-------|-----|
| Gov points empty | `POST /landing-ai/seed/builtin` |
| Phase 1 fails | Check `VISION_AGENT_API_KEY` |
| Phase 2 fails | Attach PDF in UI or set `DUAL_VERIFY_INTERNAL_PDF_PATH` |
| Transport shows `local` | Set Azure env vars + `KAFKA_ENABLED=true` |
| All points failed | Check API logs; use **Retry failed points** |
| Supabase save skipped | Run migration 003; check `SUPABASE_URL` |

---

## 11. Related docs

| Doc | Content |
|-----|---------|
| [AZURE_KAFKA_SETUP_STEPS.md](./AZURE_KAFKA_SETUP_STEPS.md) | Azure portal clicks |
| [KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md](./KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md) | Failure handling, .NET |
| [DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md](./DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md) | Business workflow |
| [BCP_PIPELINE_SIMPLE.md](./BCP_PIPELINE_SIMPLE.md) | Phase 1 + 2 prompts summary |
