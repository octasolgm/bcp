# BCP Kafka Dev — Configured (`bcp-kafka-dev`)

**Namespace:** `bcp-kafka-dev`  
**Bootstrap:** `bcp-kafka-dev.servicebus.windows.net:9093`  
**Status:** Azure Steps 1–10 complete · Nest `.env` Step 11 complete

---

## What is configured

| Item | Value |
|------|--------|
| Resource group | (your Azure RG) |
| Namespace | `bcp-kafka-dev` |
| Tier | Standard |
| Capture | **OFF** (cost saving) |
| Local authentication | **ON** (connection strings work) |

### Event Hubs (topics)

| Hub name | Purpose |
|----------|---------|
| `dual-verify-jobs` | Main work queue |
| `dual-verify-retry` | Transient failures |
| `dual-verify-dlq` | Dead letter after max retries |
| `dual-verify-results` | Optional completion events |

### Access policies (use these — NOT RootManageSharedAccessKey in app)

| Policy | Env variable | Permission |
|--------|--------------|------------|
| `bcp-api-send` | `KAFKA_PRODUCER_CONNECTION_STRING` | Send |
| `bcp-worker-listen` | `KAFKA_CONSUMER_CONNECTION_STRING` | Listen |
| `bcp-worker-send` | `KAFKA_WORKER_SEND_CONNECTION_STRING` | Send (retry/DLQ) |

Secrets live in **root `.env` only** (gitignored). Never commit or paste in chat.

---

## Cost settings (keep low)

| Setting | Recommendation | Your setup |
|---------|----------------|------------|
| Pricing tier | Standard (required for Kafka) | ✓ |
| Throughput units | **1** for dev | Keep at 1 |
| **Capture** | **OFF** | ✓ You turned off |
| Message retention | **1 day** on jobs/retry | Set in portal |
| DLQ retention | 7 days max | OK |
| `dual-verify-results` hub | Optional — skip if unused | Optional |
| Auto-inflate | OFF for dev | Check namespace settings |
| Partitions | 4 per hub (dev) | OK |

**Capture OFF** is correct — capture writes to Azure Blob and adds cost. BCP does not need it.

---

## NestJS — already wired

Config is in root `.env`. Restart API after changes:

```bash
npm run dev:api
npm run dev:web
```

### Verify

```bash
curl http://localhost:4000/dual-verify-kafka/health
# → "transport": "kafka"

npm run kafka:smoke --workspace=apps/api
# Expected: OK Sent + OK Received
```

### UI

http://localhost:3000/landing-ai/kafka-dual-verify

Progress panel should show **Transport: kafka** (not local).

---

## Run dual verify

1. Seed gov points (once):  
   `POST http://localhost:4000/landing-ai/seed/builtin`
2. Open kafka-dual-verify page
3. Select points + attach internal PDF
4. **Run Kafka dual verify**
5. Poll progress until complete

---

## Security reminder

If connection strings were shared in WhatsApp/email:

1. **Do not use** `RootManageSharedAccessKey` in the app
2. Rotate keys in Azure Portal → Shared access policies → Regenerate
3. Update `.env` with new keys only

---

## Troubleshooting

| Issue | Fix |
|-------|-----|
| Transport still `local` | `KAFKA_ENABLED=true`, restart API, check `.env` |
| Producer connect fail | Local auth ON; broker includes `:9093` |
| Consumer fail | Use `bcp-worker-listen` string (not api-send) |
| Topic not found | Create hubs in portal (steps 4–7) |
| High Azure bill | TU=1, capture OFF, 1-day retention |

---

## Related docs

- **[KAFKA_DUAL_VERIFY_PROCESS.md](./KAFKA_DUAL_VERIFY_PROCESS.md)** — full run process, cost vs quality
- [AZURE_KAFKA_SETUP_STEPS.md](./AZURE_KAFKA_SETUP_STEPS.md)
- [KAFKA_DUAL_VERIFY_IMPLEMENTATION.md](./KAFKA_DUAL_VERIFY_IMPLEMENTATION.md)
