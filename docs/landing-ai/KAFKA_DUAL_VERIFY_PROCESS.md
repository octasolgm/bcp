# Kafka Dual Verify — Full Process Guide

**Goal:** Best compliance results at lowest cost  
**UI:** http://localhost:3000/landing-ai/kafka-dual-verify  
**API:** http://localhost:4000/dual-verify-kafka  

---

## 1. One-page overview

```text
┌─────────────┐     ┌──────────────┐     ┌─────────────────┐
│  Azure      │     │  Nest API    │     │  Worker         │
│  Event Hubs │ ◄── │  (producer)  │ ──► │  (consumer)     │
│  Kafka      │     │  + UI        │     │  Phase1→Phase2  │
└─────────────┘     └──────────────┘     └────────┬────────┘
                                                   │
                     ┌──────────────┐              │
                     │  Supabase    │ ◄────────────┘
                     │  cache +     │   save results
                     │  sessions    │
                     └──────────────┘
```

**Per gov point:** 1 Kafka message → Landing AI (Phase 1) → Gemini (Phase 2) → agreement check → save.

---

## 2. Cost vs quality (choose your mode)

### Low cost (recommended for dev / daily use)

| Setting | Value | Why |
|---------|--------|-----|
| Azure throughput units | **1** | Minimum billable |
| Capture | **OFF** | No Blob storage fees |
| Message retention | **1 day** | Enough for jobs |
| Phase 1 force refresh | **OFF** | Uses Supabase compare cache = **0 Landing AI credits** |
| Phase 2 model | **gemini-2.5-flash-lite** | Cheapest Gemini |
| Points per run | **3–5** | Fewer API calls |
| Worker concurrency | **2** | Limits parallel Gemini (`DUAL_VERIFY_WORKER_CONCURRENCY=2`) |
| Gov points source | **Supabase seed** | Free — `POST /landing-ai/seed/builtin` |

### Best quality (sign-off / audit packs)

| Setting | Value | Why |
|---------|--------|-----|
| Phase 1 force refresh | **ON** | Fresh Landing AI compare every point |
| Phase 2 model | **gemini-3.5-flash** | Stronger independent verify |
| Points | **Section or leaf** | Leaf = more precise, more cost |
| Internal PDF | **Always attach** | Gemini reads real PDF pages |
| Granularity | **leaf** | Sub-point level (2.1.1, 2.1.2…) |

---

## 3. Setup process (one time)

### A. Azure (Steps 1–10)

See [AZURE_KAFKA_SETUP_STEPS.md](./AZURE_KAFKA_SETUP_STEPS.md)

Checklist:
- [ ] Namespace `bcp-kafka-dev` Standard tier, **1 TU**
- [ ] Capture **OFF** on all hubs
- [ ] Local authentication **ON**
- [ ] Hubs: `dual-verify-jobs`, `dual-verify-retry`, `dual-verify-dlq`
- [ ] Policies: `bcp-api-send`, `bcp-worker-listen`, `bcp-worker-send`

### B. Nest `.env` (Step 11)

See [BCP_KAFKA_DEV_CONFIGURED.md](./BCP_KAFKA_DEV_CONFIGURED.md)

```env
KAFKA_ENABLED=true
KAFKA_BROKERS=bcp-kafka-dev.servicebus.windows.net:9093
# + producer, consumer, worker-send connection strings
DUAL_VERIFY_WORKER_CONCURRENCY=2
GEMINI_DEFAULT_MODEL=gemini-2.5-flash-lite
```

### C. Verify

```bash
npm run dev:api
curl http://localhost:4000/dual-verify-kafka/health   # transport: kafka
npm run kafka:smoke --workspace=apps/api              # Sent + Received OK
```

### D. Seed Supabase (once per environment)

```bash
curl -X POST http://localhost:4000/landing-ai/seed/builtin
```

Loads gov + internal extract points into cache (**free reloads**).

---

## 4. Run process (every analysis)

### Step 1 — Open UI

http://localhost:3000/landing-ai/kafka-dual-verify

Confirm banner shows **Transport: kafka**.

### Step 2 — Load gov points

- Auto-loads from Supabase (`gov-tfs-guidelines`)
- If empty → click **Seed builtin docs (free)**

### Step 3 — Select points

- Default: **3 section points** (low cost)
- Select more only when needed

### Step 4 — Configure

| Field | Low cost | Best quality |
|-------|----------|--------------|
| Phase 2 model | gemini-2.5-flash-lite | gemini-3.5-flash |
| Force fresh Phase 1 | ☐ unchecked | ☑ checked |
| Internal PDF | Attach IMPTFS | Attach IMPTFS |

### Step 5 — Run

Click **Run Kafka dual verify (N points)**

API:
1. Creates session in DB
2. Publishes **N messages** to `dual-verify-jobs`
3. Returns `sessionId` immediately

### Step 6 — Worker (automatic)

For each message:

```text
1. Check idempotency (skip if already completed)
2. PHASE 1 — LandingAiService.comparePoint()
     • Uses Supabase cache if force refresh OFF
     • Prompt: COMPARE_PROMPT_V2 (landing-ai/prompts/)
3. PHASE 2 — BcpAnalyzeService.analyze() + PDF
     • Prompt: buildDualVerifyPrompt() (dual-verify-kafka/utils/)
4. Agreement — compareDualVerifyResults()
5. Save point job + compliance session (Supabase)
6. Commit Kafka offset
```

On failure:
- Transient → retry (up to 3×) → `dual-verify-retry`
- Permanent → `dual-verify-dlq` + mark failed
- **Other points keep running**

### Step 7 — Monitor progress

UI polls every 2.5s. Shows:
- Completed / failed / running / queued
- Agreement badge per point (click to expand)

Or API:

```bash
curl http://localhost:4000/dual-verify-kafka/jobs/{sessionId}
```

### Step 8 — Review results

- **Aligned** — both passes agree → low review effort
- **Status mismatch** — manual review required
- **Retry failed** button re-queues failed points only

Results also saved to `landing_ai_compliance_sessions` (granularity `dual-section`).

---

## 5. What each component costs money

| Component | Costs when | Free when |
|-----------|------------|-----------|
| **Azure Event Hubs** | Always (1 TU ~ fixed/month) | — |
| **Landing AI Phase 1** | force refresh ON or no cache | Cache hit in Supabase |
| **Gemini Phase 2** | Every point analyzed | — |
| **Supabase** | Storage (minimal) | Seed + cache reads |
| **Kafka messages** | Included in Event Hubs TU | — |

**Biggest savings:** Phase 1 cache + flash-lite + few points + capture OFF.

---

## 6. Files & prompts reference

| Step | Code | Prompt / data |
|------|------|----------------|
| UI | `apps/web/.../kafka-dual-verify/page.tsx` | — |
| API jobs | `dual-verify-kafka.controller.ts` | — |
| Producer | `kafka/kafka-producer.service.ts` | — |
| Worker | `dual-verify-kafka-worker.service.ts` | — |
| Phase 1 | `landing-ai.service.ts` → `comparePoint()` | `compliance-compare-prompts.ts` |
| Phase 2 | `bcp-analyze.service.ts` | `dual-verify-prompt.ts` + `reference-map-prompt.ts` |
| Agreement | `dual-verify-agreement.ts` | — |
| Azure setup | `AZURE_KAFKA_SETUP_STEPS.md` | — |
| Implementation | `KAFKA_DUAL_VERIFY_IMPLEMENTATION.md` | — |

---

## 7. Troubleshooting

| Problem | Fix |
|---------|-----|
| Transport `local` | `KAFKA_ENABLED=true`, restart API |
| No gov points | Seed builtin |
| Phase 1 quota | Use cache (force refresh off) or wait |
| Phase 2 empty | Attach internal PDF in UI |
| All points failed | Check `VISION_AGENT_API_KEY`, `GEMINI_API_KEY` |
| High Azure bill | TU=1, capture OFF, 1-day retention |
| Stuck queued | Restart API (consumer), check worker-listen policy |

---

## 8. Daily commands

```bash
# Start
npm run dev:api
npm run dev:web

# Health
curl http://localhost:4000/dual-verify-kafka/health

# Seed (once)
curl -X POST http://localhost:4000/landing-ai/seed/builtin

# Smoke test
npm run kafka:smoke --workspace=apps/api
```

---

## 9. Related docs

| Doc | Use |
|-----|-----|
| [KAFKA_DUAL_VERIFY_PROCESS.md](./KAFKA_DUAL_VERIFY_PROCESS.md) | This file |
| [BCP_KAFKA_DEV_CONFIGURED.md](./BCP_KAFKA_DEV_CONFIGURED.md) | Your Azure namespace |
| [AZURE_KAFKA_SETUP_STEPS.md](./AZURE_KAFKA_SETUP_STEPS.md) | Portal setup |
| [KAFKA_DUAL_VERIFY_IMPLEMENTATION.md](./KAFKA_DUAL_VERIFY_IMPLEMENTATION.md) | Code map |
| [DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md](./DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md) | Business workflow |
