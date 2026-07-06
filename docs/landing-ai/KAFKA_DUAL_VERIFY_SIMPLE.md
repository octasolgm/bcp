# Kafka Dual Verify — Simple

> **Docs**
> - **Process guide (cost + quality + run steps):** [KAFKA_DUAL_VERIFY_PROCESS.md](./KAFKA_DUAL_VERIFY_PROCESS.md)
> - **Implementation (code + workflow + prompts):** [KAFKA_DUAL_VERIFY_IMPLEMENTATION.md](./KAFKA_DUAL_VERIFY_IMPLEMENTATION.md)
> - **Azure portal steps (start here):** [AZURE_KAFKA_SETUP_STEPS.md](./AZURE_KAFKA_SETUP_STEPS.md)
> - Quick start: [KAFKA_DUAL_VERIFY_SETUP_GUIDE.md](./KAFKA_DUAL_VERIFY_SETUP_GUIDE.md)
> - **Full guide (Azure + NestJS + .NET + failure handling):** [KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md](./KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md)

This is the simplest way to use Kafka for dual verify.

Current dual verify flow:

1. Phase 1 = Landing AI checks one gov point
2. Phase 2 = Gemini checks the same point again
3. Save both results
4. Show both in UI

## Simple idea

Use Kafka only as a queue between the UI/API and the dual verify worker.

```text
User clicks Run Dual Verify
        ↓
API creates job
        ↓
API sends message to Kafka topic
        ↓
Worker reads message
        ↓
Worker runs:
  1. Phase 1 (Landing AI)
  2. Phase 2 (Gemini)
  3. Agreement check
        ↓
Worker saves result to DB
        ↓
UI polls API and shows result
```

## Kafka topics

Keep it very small:

- `dual-verify-jobs`
- `dual-verify-results`

## Message sent to Kafka

Send one message per gov point.

```json
{
  "jobId": "job-123",
  "sessionId": "session-001",
  "pointId": "2.1",
  "govText": "Senior management must review the sanctions program annually.",
  "internalDocId": "imptfs-001",
  "model": "gemini-2.5-flash-lite"
}
```

## Worker logic

Worker reads one message and does this:

1. Load internal document
2. Run Landing AI for this point
3. Run Gemini for this point
4. Compare both answers
5. Save final result
6. Publish small result event

## Result event

```json
{
  "jobId": "job-123",
  "sessionId": "session-001",
  "pointId": "2.1",
  "status": "completed",
  "phase1Status": "Partial Compliant",
  "phase2Status": "Non-Compliant",
  "agreement": "mismatch"
}
```

## Why Kafka here

Kafka helps when:

- many points run at the same time
- analysis is slow
- you want retry if worker fails
- UI should not wait for one long request

## Very simple API flow

### 1. Start

`POST /dual-verify/jobs`

API:

- creates session
- sends Kafka message(s)
- returns `sessionId`

### 2. Check progress

`GET /dual-verify/jobs/:sessionId`

API:

- reads saved DB status
- returns completed / running / failed

### 3. Get results

`GET /dual-verify/jobs/:sessionId/results`

## Best simple rule

Do not send full PDF files inside Kafka messages.

Send only:

- `sessionId`
- `pointId`
- document ID / storage path
- model name

The worker should load the file from storage.

## Recommended minimal implementation

If you want the easiest version:

1. UI calls Nest API
2. Nest API publishes to Kafka
3. One worker consumes from Kafka
4. Worker runs Landing AI + Gemini
5. Worker saves to Supabase / DB
6. UI polls every few seconds

## Important note

Kafka dual verify **is implemented** in this repo:

- UI: `apps/web/src/app/landing-ai/kafka-dual-verify/page.tsx`
- API: `apps/api/src/modules/dual-verify-kafka/`
- Endpoints: `POST /dual-verify-kafka/jobs`, `GET /dual-verify-kafka/jobs/:sessionId`

For setup, cost tips, and step-by-step run process see [KAFKA_DUAL_VERIFY_PROCESS.md](./KAFKA_DUAL_VERIFY_PROCESS.md).

Legacy sync dual verify (no Kafka) remains in:

- `apps/web/src/app/landing-ai/dual-verify-workbench.tsx`
- `docs/landing-ai/DUAL_VERIFY_AND_ANALYSIS_WORKFLOW.md`
