# Azure Kafka (Event Hubs) — Full Guide for BCP Dual Verify

**Audience:** Developers new to Kafka, building BCP compliance dual verify  
**Stack today:** NestJS (`apps/api`)  
**Stack later:** .NET Core backend (same Kafka topics and message contract)  
**Azure service:** **Event Hubs** with **Kafka endpoint** (not self-hosted Kafka)

**Related docs**

| Doc | Use for |
|-----|---------|
| [KAFKA_DUAL_VERIFY_SIMPLE.md](./KAFKA_DUAL_VERIFY_SIMPLE.md) | 1-page overview |
| [KAFKA_DUAL_VERIFY_SETUP_GUIDE.md](./KAFKA_DUAL_VERIFY_SETUP_GUIDE.md) | First NestJS steps |
| [DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md](./DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md) | Phase 1 + Phase 2 business logic |

---

## 1. Executive summary

Dual verify compares each **gov requirement point** twice:

1. **Phase 1** — Landing AI semantic gap analysis  
2. **Phase 2** — Gemini independent verification  

Today the UI loops these calls in the browser. That is slow, fragile, and hard to scale.

**Kafka solves this** by turning each gov point into an **async job**:

```text
API publishes job → Kafka holds it → Worker processes → DB stores result → UI polls progress
```

**Professional rule for BCP:**  
**One Kafka message = one gov point.**  
If point `2.7` fails, points `2.1`–`2.6` and `2.8`–`3.5` still complete.

---

## 2. Why Azure Event Hubs (Kafka protocol)

Azure does not offer “Managed Kafka” like AWS MSK. The standard enterprise choice is:

| Option | When to use |
|--------|-------------|
| **Azure Event Hubs (Kafka endpoint)** | ✅ Recommended — managed, Azure-native, works with NestJS and .NET |
| Confluent Cloud on Azure | Full Kafka features, higher cost/complexity |
| Self-hosted Kafka on AKS/VMs | Only if you need full Kafka admin control |

Event Hubs speaks the **Kafka protocol** on port **9093** with **SASL_SSL**.  
Your NestJS app uses **KafkaJS**. Your future .NET app uses **Confluent.Kafka** or **Azure.Messaging.EventHubs** — **same topics, same JSON messages**.

---

## 3. How Kafka works (professional mental model)

```text
┌─────────────┐     publish      ┌──────────────────┐     consume      ┌─────────────┐
│  Producer   │ ───────────────► │  Topic (Hub)     │ ───────────────► │  Consumer   │
│  (Nest API) │                  │  dual-verify-jobs│                  │  (Worker)   │
└─────────────┘                  └──────────────────┘                  └─────────────┘
                                        │
                                        │ messages persisted
                                        │ (durable log)
                                        ▼
                                 Partition 0 │ 1 │ 2 │ 3
```

| Term | Meaning for BCP |
|------|-----------------|
| **Topic** | Named queue, e.g. `dual-verify-jobs` |
| **Partition** | Parallel lanes inside a topic; same `sessionId` can share a key for ordering per session |
| **Producer** | API that enqueues work after user clicks Run |
| **Consumer** | Worker that runs Landing AI + Gemini |
| **Consumer group** | Team of workers sharing load; each message goes to **one** worker in the group |
| **Offset** | Bookmark — “this message is done” |
| **Retention** | How long Kafka keeps messages (days) — allows replay |

**Delivery guarantee (default):** **at-least-once**  
→ A message may be processed **more than once** if a worker crashes before committing offset.  
→ Your worker **must be idempotent** (safe to retry).

---

## 4. Target architecture (NestJS now → .NET later)

```text
                    ┌─────────────────────────────────────────┐
                    │           Azure Event Hubs               │
                    │  ┌─────────────────┐ ┌────────────────┐ │
                    │  │ dual-verify-jobs│ │dual-verify-dlq │ │
                    │  └────────┬────────┘ └───────▲────────┘ │
                    │           │                  │          │
                    │  ┌────────┴────────┐         │          │
                    │  │dual-verify-retry│─────────┘          │
                    │  └────────┬────────┘                    │
                    └───────────┼─────────────────────────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
        ▼                       ▼                       ▼
┌───────────────┐      ┌───────────────┐      ┌───────────────┐
│ NestJS API    │      │ NestJS Worker │      │ .NET Worker   │
│ (Producer)    │      │ (Consumer)    │      │ (Consumer)    │
│ apps/api      │      │ Phase A       │      │ Phase B       │
└───────┬───────┘      └───────┬───────┘      └───────┬───────┘
        │                      │                      │
        └──────────────────────┼──────────────────────┘
                               ▼
                    ┌─────────────────────┐
                    │ Supabase / PostgreSQL│
                    │ sessions + results   │
                    └─────────────────────┘
                               ▲
                               │
                    ┌──────────┴──────────┐
                    │ React / Next UI      │
                    │ poll job status      │
                    └─────────────────────┘
```

**Migration strategy**

| Phase | Producer | Consumer |
|-------|----------|----------|
| **Now** | NestJS API | NestJS worker process |
| **Later** | NestJS or .NET API | .NET worker (replace Nest consumer) |
| **Contract** | Same JSON message schema | Same topics — **no UI change** |

Only the **consumer implementation** changes. Topics and DB schema stay stable.

---

## 5. Azure setup — step by step

### 5.1 Prerequisites

- Azure subscription
- Resource group (e.g. `rg-bcp-dev`)
- Decision: **Standard** tier Event Hubs namespace (Basic does **not** support Kafka)

### 5.2 Create Event Hubs namespace

1. [Azure Portal](https://portal.azure.com) → **Create a resource**
2. Search **Event Hubs** → **Namespace**
3. Settings:
   - **Namespace name:** `bcp-kafka-dev` (globally unique)
   - **Pricing tier:** Standard
   - **Throughput units:** 1 (dev); scale in prod
   - **Zone redundancy:** optional (prod)
4. **Review + create**

### 5.3 Enable Kafka endpoint

1. Open namespace → **Settings** → **Kafka**
2. Enable Kafka (if toggle exists)
3. Copy **Bootstrap server**:

```text
bcp-kafka-dev.servicebus.windows.net:9093
```

### 5.4 Create Event Hubs (topics)

Create **four** hubs for production-grade dual verify:

| Event Hub name | Purpose |
|----------------|---------|
| `dual-verify-jobs` | New work — one message per gov point |
| `dual-verify-retry` | Delayed retry after transient failure |
| `dual-verify-dlq` | Dead letter — permanent failure after max retries |
| `dual-verify-results` | Optional events for UI/webhooks (point completed) |

For each hub:

1. Namespace → **Event Hubs** → **+ Event Hub**
2. **Partitions:** 4 (dev) / 8–16 (prod)
3. **Message retention:** 1 day (jobs), 7 days (dlq) — adjust per compliance policy
4. **Cleanup policy:** Delete (not compact — jobs are one-time)

### 5.5 Access policies (security)

**Do not use RootManageSharedAccessKey in production.**

1. Namespace → **Shared access policies** → **+ Add**
2. Create policies:

| Policy name | Send | Listen | Use on |
|-------------|------|--------|--------|
| `bcp-api-send` | ✅ | ❌ | NestJS / .NET API (producer) |
| `bcp-worker-listen` | ❌ | ✅ | Workers (consumer) |
| `bcp-worker-send` | ✅ | ❌ | Worker publishing to retry/dlq/results |

For each policy, copy **Connection string** into Azure Key Vault or `.env` (never git).

### 5.6 Environment variables (shared Nest + .NET)

```env
# Azure Event Hubs (Kafka)
KAFKA_ENABLED=true
KAFKA_BROKERS=bcp-kafka-dev.servicebus.windows.net:9093
KAFKA_SECURITY_PROTOCOL=SASL_SSL
KAFKA_SASL_MECHANISM=PLAIN
KAFKA_SASL_USERNAME=$ConnectionString

# Producer (API) — use bcp-api-send connection string
KAFKA_PRODUCER_CONNECTION_STRING=Endpoint=sb://...

# Consumer (Worker) — use bcp-worker-listen (+ send for retry/dlq)
KAFKA_CONSUMER_CONNECTION_STRING=Endpoint=sb://...

KAFKA_CLIENT_ID=bcp-dual-verify
KAFKA_CONSUMER_GROUP=dual-verify-workers-v1

KAFKA_TOPIC_JOBS=dual-verify-jobs
KAFKA_TOPIC_RETRY=dual-verify-retry
KAFKA_TOPIC_DLQ=dual-verify-dlq
KAFKA_TOPIC_RESULTS=dual-verify-results

# Worker tuning
DUAL_VERIFY_MAX_RETRIES=3
DUAL_VERIFY_RETRY_DELAY_MS=30000
DUAL_VERIFY_WORKER_CONCURRENCY=3
```

**Azure Kafka auth pattern**

| Field | Value |
|-------|-------|
| `sasl.mechanism` | `PLAIN` |
| `sasl.username` | `$ConnectionString` (literal string) |
| `sasl.password` | Full Event Hubs connection string |
| `ssl` | `true` |
| `broker` | `*.servicebus.windows.net:9093` |

### 5.7 Verify connectivity (before coding)

Use [Kafka CLI with Event Hubs](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-for-kafka-quickstart) or a small script:

```bash
# NestJS smoke test (after kafkajs installed)
node apps/api/scripts/kafka-smoke-test.mjs
```

Expected: producer sends test message → consumer reads it → log success.

### 5.8 Production Azure checklist

- [ ] Standard tier namespace
- [ ] Separate send/listen policies (least privilege)
- [ ] Connection strings in **Key Vault**
- [ ] Private endpoint / VNet (bank network requirement)
- [ ] Diagnostic logs → Log Analytics
- [ ] Alerts on throttling and DLQ depth
- [ ] Retention aligned with audit policy

---

## 6. Topic and message design (language-agnostic contract)

### 6.1 One message per gov point

**50 points selected → 50 messages** on `dual-verify-jobs`, same `sessionId`.

```json
{
  "schemaVersion": "1.0",
  "messageId": "550e8400-e29b-41d4-a716-446655440000",
  "jobId": "550e8400-e29b-41d4-a716-446655440001",
  "sessionId": "session-2026-07-02-001",
  "pointId": "2.1",
  "pointTitle": "Senior Management SCP Approval",
  "govText": "Senior management commitment to the LFI SCP must be demonstrated...",
  "granularity": "section",
  "internalDocId": "imptfs-001",
  "govDocId": "gov-tfs-guidelines",
  "phase2Model": "gemini-2.5-flash-lite",
  "attempt": 1,
  "maxAttempts": 3,
  "createdAt": "2026-07-02T10:00:00Z",
  "correlationId": "corr-abc-123"
}
```

**Never include:** PDF bytes, full markdown, all 50 points in one message.

**Kafka message key (recommended):**

```text
key = sessionId + ":" + pointId
```

Same key → same partition → ordered processing per point within a session.

### 6.2 Result event (optional topic)

```json
{
  "schemaVersion": "1.0",
  "sessionId": "session-2026-07-02-001",
  "pointId": "2.1",
  "status": "completed",
  "phase1Status": "Partial Compliant",
  "phase2Status": "Non-Compliant",
  "agreement": "mismatch",
  "completedAt": "2026-07-02T10:02:15Z"
}
```

### 6.3 DB is source of truth

Kafka carries **commands**. PostgreSQL/Supabase carries **state**.

| Table / concept | Stores |
|-----------------|--------|
| `dual_verify_sessions` | sessionId, totalPoints, status |
| `dual_verify_point_jobs` | per point: queued / running / completed / failed |
| `compliance_session_results` | Phase 1 + Phase 2 output (existing BCP) |

UI reads **DB**, not Kafka directly.

---

## 7. Dual verify pipeline — end to end

```text
1. User selects 20 gov points + IMPTFS doc
2. POST /dual-verify/jobs
      → INSERT session (status=queued, total=20)
      → INSERT 20 rows (status=queued)
      → PUBLISH 20 messages to dual-verify-jobs
      → RETURN { sessionId, totalPoints: 20 }

3. Worker consumes message for point 2.1
      → UPDATE point row status=running
      → Phase 1: LandingAiService.comparePoint()
      → Phase 2: BcpAnalyzeService.analyze()
      → Agreement check
      → INSERT/UPDATE compliance result
      → UPDATE point row status=completed
      → COMMIT Kafka offset
      → PUBLISH optional result event

4. UI polls GET /dual-verify/jobs/:sessionId
      → { completed: 12, failed: 1, running: 1, total: 20 }

5. When completed + failed = total → session status=finished
```

**Existing BCP endpoints reused inside worker (not from browser):**

| Step | Service / endpoint logic |
|------|--------------------------|
| Phase 1 | `LandingAiService` — `/landing-ai/compare-point` |
| Phase 2 | `BcpAnalyzeService` — `/ai/bcpanalyze` |
| Save | compliance session storage (Supabase) |

---

## 8. Failure handling — professional Kafka patterns

This is the most important section for banking-grade async processing.

### 8.1 Principle: isolate failure per point

| Failure | Effect |
|---------|--------|
| Point `2.7` Gemini timeout | Only `2.7` retries / fails; other points continue |
| Worker pod crash mid-message | Message redelivered to another worker (at-least-once) |
| Landing AI quota exceeded | Transient → retry with backoff |
| Invalid pointId in message | Permanent → DLQ immediately |

**Session status:**

```text
session.status = finished_when (completed + failed + cancelled) >= totalPoints
```

Do **not** fail the whole session because one point failed.

### 8.2 Transient vs permanent errors

| Type | Examples | Action |
|------|----------|--------|
| **Transient** | Network blip, Gemini 429, Landing AI 503, DB timeout | Retry with backoff |
| **Permanent** | Unknown docId, malformed message, schemaVersion mismatch | DLQ immediately |
| **Business** | Phase 1 OK but Phase 2 disagree | **Not a failure** — save as `mismatch`, status=completed |

### 8.3 Retry flow (recommended)

```text
dual-verify-jobs
       │
       ▼
   [Worker]
       │
       ├─ success ──► commit offset ──► DB completed
       │
       ├─ transient fail (attempt < 3)
       │       │
       │       ▼
       │   publish to dual-verify-retry
       │   (attempt++, delay header/metadata)
       │       │
       │       ▼
       │   retry consumer OR scheduled re-publish to jobs
       │
       └─ permanent fail OR attempt >= 3
               │
               ▼
           dual-verify-dlq
               │
               ▼
           DB status=failed + alert ops
```

**Retry delays (example):**

| Attempt | Delay |
|---------|-------|
| 1 | immediate |
| 2 | 30 seconds |
| 3 | 2 minutes |
| 4+ | DLQ |

### 8.4 Offset commit rule (critical)

```text
❌ WRONG: commit offset before saving to DB
✅ RIGHT: commit offset ONLY after DB transaction succeeds
```

Pseudo-flow:

```text
BEGIN
  process point
  save result to DB
  update point status
COMMIT DB
  then consumer.commitOffsets()
```

If worker dies **after** DB save but **before** commit → message replayed → **idempotent** write (see 8.5).

### 8.5 Idempotency (required for at-least-once)

Use unique constraint on `(sessionId, pointId)` in `dual_verify_point_jobs`.

Before processing:

```sql
-- If already completed, skip AI calls and commit offset
SELECT status FROM dual_verify_point_jobs
WHERE sessionId = ? AND pointId = ?
```

If `status = completed` → log "duplicate delivery" → commit offset → return.

Use `messageId` / `jobId` in logs for audit trail.

### 8.6 Dead Letter Queue (DLQ)

Messages in `dual-verify-dlq` need **human or ops replay**:

1. Fix root cause (API key, doc missing, bug)
2. Replay tool: read DLQ → publish back to `dual-verify-jobs` with `attempt=1`
3. Or admin UI: "Retry failed point"

**Monitor DLQ depth** — alert if > 0 in prod.

### 8.7 Partial failure inside one point (Phase 1 OK, Phase 2 fail)

| Scenario | Behavior |
|----------|----------|
| Phase 1 fails | Retry whole point (both phases) |
| Phase 1 OK, Phase 2 fails | Retry Phase 2 only (store Phase 1 in DB first) |
| Both OK, DB save fails | Retry save only (do not re-call AI if results cached) |

Store intermediate state:

```text
pointJob.phase1Result = {...}   // after Phase 1 success
pointJob.phase2Result = {...}   // after Phase 2 success
```

On retry, skip Phase 1 if `phase1Result` already exists.

### 8.8 Concurrency limits

Do not run 50 Gemini calls at once.

```env
DUAL_VERIFY_WORKER_CONCURRENCY=3
```

Use a semaphore in worker or partition count + fixed worker replicas.

### 8.9 Session cancellation

User cancels session → set `session.cancelled=true` in DB.  
Worker checks before each AI call; if cancelled → mark point `cancelled` → commit offset (drop work).

---

## 9. NestJS implementation (now)

### 9.1 Packages

```bash
npm install kafkajs --workspace=apps/api
```

### 9.2 Module layout

```text
apps/api/src/modules/kafka/
  kafka.module.ts
  kafka.config.ts
  kafka-producer.service.ts
  kafka-consumer.base.ts

apps/api/src/modules/dual-verify/
  dual-verify.module.ts
  dual-verify.controller.ts
  dual-verify.service.ts          # create session, publish messages
  dual-verify-worker.service.ts   # consume + Phase 1 + Phase 2
  dual-verify-job.types.ts
```

### 9.3 Producer (NestJS) — minimal config

```typescript
import { Kafka, logLevel } from 'kafkajs';

const kafka = new Kafka({
  clientId: process.env.KAFKA_CLIENT_ID ?? 'bcp-api',
  brokers: [process.env.KAFKA_BROKERS!],
  ssl: true,
  sasl: {
    mechanism: 'plain',
    username: '$ConnectionString',
    password: process.env.KAFKA_PRODUCER_CONNECTION_STRING!,
  },
  logLevel: logLevel.INFO,
});

export const jobsProducer = kafka.producer();
```

### 9.4 Consumer (NestJS)

Run consumer either:

- **Same process:** `onModuleInit` in worker service (dev only), or  
- **Separate process:** `node dist/worker/dual-verify.worker.js` (prod recommended)

```typescript
const consumer = kafka.consumer({
  groupId: process.env.KAFKA_CONSUMER_GROUP ?? 'dual-verify-workers-v1',
});

await consumer.connect();
await consumer.subscribe({ topic: 'dual-verify-jobs', fromBeginning: false });

await consumer.run({
  eachMessage: async ({ topic, partition, message, heartbeat }) => {
    const job = JSON.parse(message.value!.toString()) as DualVerifyJobMessage;
    await dualVerifyWorker.processJob(job, heartbeat);
    // commit handled inside processJob after DB success
  },
});
```

### 9.5 API endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/dual-verify/jobs` | Start session + publish N messages |
| `GET` | `/dual-verify/jobs/:sessionId` | Progress summary |
| `GET` | `/dual-verify/jobs/:sessionId/results` | Point-level results |
| `POST` | `/dual-verify/jobs/:sessionId/retry-failed` | Re-queue failed points |
| `POST` | `/dual-verify/jobs/:sessionId/cancel` | Cancel remaining work |

---

## 10. .NET Core implementation (later)

Same Azure Event Hubs, same topics, same JSON. Swap only the worker host.

### 10.1 NuGet packages

```xml
<PackageReference Include="Confluent.Kafka" Version="2.*" />
<!-- or Azure.Messaging.EventHubs for native SDK (no Kafka protocol) -->
```

**Recommendation:** Use **Confluent.Kafka** so config matches NestJS (Kafka protocol).

### 10.2 Producer (.NET)

```csharp
var config = new ProducerConfig
{
    BootstrapServers = "bcp-kafka-dev.servicebus.windows.net:9093",
    SecurityProtocol = SecurityProtocol.SaslSsl,
    SaslMechanism = SaslMechanism.Plain,
    SaslUsername = "$ConnectionString",
    SaslPassword = Environment.GetEnvironmentVariable("KAFKA_PRODUCER_CONNECTION_STRING")
};

using var producer = new ProducerBuilder<string, string>(config).Build();

await producer.ProduceAsync("dual-verify-jobs", new Message<string, string>
{
    Key = $"{job.SessionId}:{job.PointId}",
    Value = JsonSerializer.Serialize(job)
});
```

### 10.3 Consumer (.NET)

```csharp
var config = new ConsumerConfig
{
    BootstrapServers = "...:9093",
    SecurityProtocol = SecurityProtocol.SaslSsl,
    SaslMechanism = SaslMechanism.Plain,
    SaslUsername = "$ConnectionString",
    SaslPassword = Environment.GetEnvironmentVariable("KAFKA_CONSUMER_CONNECTION_STRING"),
    GroupId = "dual-verify-workers-v1",
    AutoOffsetStore = false,  // manual commit after DB success
    EnableAutoCommit = false
};

using var consumer = new ConsumerBuilder<string, string>(config).Build();
consumer.Subscribe("dual-verify-jobs");

while (true)
{
    var cr = consumer.Consume();
    var job = JsonSerializer.Deserialize<DualVerifyJobMessage>(cr.Message.Value);
    await _worker.ProcessAsync(job);
    consumer.StoreOffset(cr);
    consumer.Commit(cr);
}
```

### 10.4 Shared contract

Put `DualVerifyJobMessage` schema in:

- `packages/shared-types` (TypeScript), and  
- `.NET` shared class library or OpenAPI/JSON Schema repo  

Both teams implement against **schemaVersion 1.0**.

### 10.5 Cutover plan Nest → .NET

1. Deploy .NET worker with **same consumer group** but **scale Nest workers to 0**
2. Or use new group `dual-verify-workers-v2` and drain old topic first
3. Keep Nest API as producer until .NET API ready
4. Zero UI changes if DB + REST contract unchanged

---

## 11. Monitoring and operations

| Metric | Alert when |
|--------|------------|
| Consumer lag | Lag > 100 messages for 10 min |
| DLQ depth | Any message in DLQ |
| Point failure rate | > 10% failed in session |
| Gemini 429 rate | Sustained throttling |
| Processing time p95 | > 5 min per point |
| Event Hubs throttling | Server busy errors |

**Azure tools**

- Event Hubs metrics → **Incoming/Outgoing messages**, **Throttled requests**
- Log Analytics → correlate `correlationId` across API + worker
- Application Insights (.NET) / Nest logger → structured JSON logs

**Audit (banking)**

Log every state change: `queued → running → completed|failed` with userId, sessionId, pointId, timestamp.

---

## 12. Security checklist

- [ ] No PDFs or PII in Kafka payloads — reference doc IDs only
- [ ] Least-privilege SAS policies (send vs listen)
- [ ] Key Vault for connection strings
- [ ] TLS only (9093)
- [ ] Private Link for Event Hubs in prod
- [ ] Consumer runs in trusted network (same as Gemini/Landing AI access)
- [ ] DLQ access restricted to ops role

---

## 13. Implementation roadmap

| Week | Task |
|------|------|
| 1 | Azure namespace + 4 hubs + policies + smoke test |
| 2 | Nest producer + `POST /dual-verify/jobs` + DB tables |
| 3 | Nest worker — 1 point E2E (Phase 1 + Phase 2) |
| 4 | Retry + DLQ + idempotency + progress API |
| 5 | Wire dual-verify UI to poll API |
| 6+ | .NET worker parity + Nest consumer decommission |

---

## 14. Quick reference — failure cheat sheet

```text
One point fails?
  → Retry transient errors (max 3)
  → Then DLQ + mark failed in DB
  → Other points unaffected

Worker crashes?
  → Kafka redelivers message
  → Idempotent check prevents duplicate AI calls

Phase 2 fails but Phase 1 OK?
  → Save Phase 1, retry Phase 2 only

Whole session stuck?
  → Check consumer lag, worker logs, Gemini/Landing AI keys

Message poison / bad JSON?
  → DLQ immediately, alert ops

Need to re-run one point?
  → POST /dual-verify/jobs/:sessionId/retry-failed
  → Or replay from DLQ admin tool
```

---

## 15. Summary

| Question | Answer |
|----------|--------|
| Azure Kafka? | **Event Hubs** with Kafka endpoint on `:9093` |
| NestJS now? | **KafkaJS** producer in API, consumer in worker service |
| .NET later? | **Confluent.Kafka**, same topics + JSON schema |
| One message or all? | **One message per gov point** |
| One point fails? | **Retry → DLQ**; session continues for other points |
| Source of truth? | **Database**, not Kafka |
| Delivery? | **At-least-once** — design for **idempotency** |

---

*Document version: 1.0 — BCP dual verify Kafka architecture*
