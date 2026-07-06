# Azure Kafka Setup — Step by Step (BCP Dual Verify)

Use **Azure Event Hubs** with **Kafka endpoint**.  
No VM, no self-hosted Kafka.

**Time:** ~20–30 minutes  
**Cost (dev):** Standard namespace, **1 throughput unit**, **capture OFF**

> **BCP dev namespace configured?** See [BCP_KAFKA_DEV_CONFIGURED.md](./BCP_KAFKA_DEV_CONFIGURED.md)

---

## Cost savings (do this)

| Setting | Where | Dev recommendation |
|---------|--------|---------------------|
| **Capture** | Each Event Hub → Capture | **OFF** ← saves Blob storage cost |
| **Throughput units** | Namespace → Scale | **1** |
| **Tier** | Namespace | Standard (Kafka requires it) |
| **Message retention** | Each Event Hub | **1 day** (jobs/retry) |
| **Local authentication** | Namespace → Configuration | **ON** (for connection strings) |
| **Auto-inflate** | Namespace | **OFF** for dev |
| **Results hub** | Step 7 | Optional — skip if not used |

**Capture OFF is correct for BCP.** We only need the queue; no archive to Storage Account.

---

## Before you start

You need:

- Azure account ([portal.azure.com](https://portal.azure.com))
- Permission to create resources
- Choose a region close to your API (e.g. **UAE North**, **West Europe**)

---

## Step 1 — Create resource group

1. Open **Azure Portal**
2. Search **Resource groups** → **Create**
3. **Resource group name:** `rg-bcp-dev`
4. **Region:** e.g. `UAE North`
5. **Review + create** → **Create**

---

## Step 2 — Create Event Hubs namespace

1. **Create a resource** → search **Event Hubs** → **Namespace**
2. **Namespace name:** `bcp-kafka-dev` (unique worldwide)
3. **Pricing tier:** **Standard**
4. **Throughput units:** `1`
5. **Review + create**

Bootstrap server: `bcp-kafka-dev.servicebus.windows.net:9093`

---

## Step 3 — Local authentication + Kafka

1. Namespace → **Configuration**
2. **Local authentication:** **Enabled**
3. Kafka port: **9093** (Standard tier)

---

## Step 4 — Event Hub: `dual-verify-jobs`

- Partitions: **4** · Retention: **1 day** · **Capture: OFF**

---

## Step 5 — Event Hub: `dual-verify-retry`

- Partitions: **4** · Retention: **1 day** · **Capture: OFF**

---

## Step 6 — Event Hub: `dual-verify-dlq`

- Partitions: **2** · Retention: **7 days** · **Capture: OFF**

---

## Step 7 — Event Hub: `dual-verify-results` (optional)

- Skip to save cost if unused

---

## Step 8 — Policy `bcp-api-send` (producer)

- Permission: **Send** only → `KAFKA_PRODUCER_CONNECTION_STRING`

---

## Step 9 — Policy `bcp-worker-listen` (consumer)

- Permission: **Listen** only → `KAFKA_CONSUMER_CONNECTION_STRING`

---

## Step 10 — Policy `bcp-worker-send` (retry/DLQ)

- Permission: **Send** only → `KAFKA_WORKER_SEND_CONNECTION_STRING`

**Do not use RootManageSharedAccessKey in the app** — dev policies only.

---

## Step 11 — Add to BCP `.env`

Open project root `.env` (do not commit secrets):

```env
# --- Azure Event Hubs (Kafka) ---
KAFKA_ENABLED=true
KAFKA_BROKERS=bcp-kafka-dev.servicebus.windows.net:9093

KAFKA_SASL_USERNAME=$ConnectionString
KAFKA_PRODUCER_CONNECTION_STRING=Endpoint=sb://bcp-kafka-dev.servicebus.windows.net/;SharedAccessKeyName=bcp-api-send;SharedAccessKey=PASTE_KEY_HERE

KAFKA_CONSUMER_CONNECTION_STRING=Endpoint=sb://bcp-kafka-dev.servicebus.windows.net/;SharedAccessKeyName=bcp-worker-listen;SharedAccessKey=PASTE_KEY_HERE

KAFKA_WORKER_SEND_CONNECTION_STRING=Endpoint=sb://bcp-kafka-dev.servicebus.windows.net/;SharedAccessKeyName=bcp-worker-send;SharedAccessKey=PASTE_KEY_HERE

KAFKA_CLIENT_ID=bcp-dual-verify
KAFKA_CONSUMER_GROUP=dual-verify-workers-v1

KAFKA_TOPIC_JOBS=dual-verify-jobs
KAFKA_TOPIC_RETRY=dual-verify-retry
KAFKA_TOPIC_DLQ=dual-verify-dlq
KAFKA_TOPIC_RESULTS=dual-verify-results
```

Replace:

- `bcp-kafka-dev` → your namespace name
- `PASTE_KEY_HERE` → keys from Step 8–10

---

## Step 12 — Quick test (optional)

After NestJS Kafka code is added:

```bash
npm install kafkajs --workspace=apps/api
node apps/api/scripts/kafka-smoke-test.mjs
```

Expected: **Sent OK** → **Received OK**

---

## Step 13 — Production extras (when ready)

| Task | Where in Azure |
|------|----------------|
| Store secrets safely | **Azure Key Vault** → reference from App Service |
| Private network | Namespace → **Networking** → Private endpoint |
| Monitor errors | **Diagnostic settings** → Log Analytics workspace |
| Alert on failures | **Alerts** → metric: incoming messages / errors |
| Separate prod namespace | Create `bcp-kafka-prod` (don’t use dev in prod) |

---

## Checklist — print this

```text
[ ] Step 1  Resource group rg-bcp-dev
[ ] Step 2  Event Hubs namespace (Standard tier)
[ ] Step 3  Bootstrap server: __________.servicebus.windows.net:9093
[ ] Step 4  Event Hub: dual-verify-jobs
[ ] Step 5  Event Hub: dual-verify-retry
[ ] Step 6  Event Hub: dual-verify-dlq
[ ] Step 7  Event Hub: dual-verify-results (optional)
[ ] Step 8  Policy bcp-api-send + connection string
[ ] Step 9  Policy bcp-worker-listen + connection string
[ ] Step 10 Policy bcp-worker-send + connection string
[ ] Step 11 .env updated (not in git)
[ ] Step 12 Smoke test passed
```

---

## What each Azure piece does

```text
Event Hubs Namespace  =  Kafka "cluster" (managed by Azure)
Event Hub             =  Kafka "topic" (a named queue)
Partition             =  Parallel lanes (use 4 for dev)
Connection string     =  Password for NestJS / .NET to connect
Consumer group        =  Worker team name (set in app, not Azure UI)
```

---

## Common mistakes

| Mistake | Fix |
|---------|-----|
| Basic tier | Use **Standard** |
| Wrong port | Use **9093** with SSL |
| Put PDF in message | Only send IDs in app code — not an Azure setting |
| One hub for everything | Use separate hubs: jobs, retry, dlq |
| Root key in production | Use `bcp-api-send` / `bcp-worker-listen` policies |

---

## Next after Azure

1. Step 11 — add keys to `.env` (see [BCP_KAFKA_DEV_CONFIGURED.md](./BCP_KAFKA_DEV_CONFIGURED.md))
2. Restart API: `npm run dev:api`
3. Open `/landing-ai/kafka-dual-verify`
4. Smoke test: `npm run kafka:smoke --workspace=apps/api`

Full architecture: [KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md](./KAFKA_AZURE_DUAL_VERIFY_FULL_GUIDE.md)
