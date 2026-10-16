# Privacy settings and how to verify them

Reference for the `Privacy` configuration section, what each setting does, and how a security reviewer can check it.
Written 15 Oct 2026. Part of [README.md](README.md) (plan) and [data-protection.md](data-protection.md) (client brief).

## 1. Settings

All settings are optional. Defaults are the strict ones. A ready-made profile for client installs is `bcp-api/appsettings.InHouse.example.json`.

| Setting | Default | What it does |
|---|---|---|
| `Privacy:OpenRouter:ZeroDataRetention` | true | Every OpenRouter request carries `provider.zdr = true`: OpenRouter routes only to endpoints with a zero-data-retention policy |
| `Privacy:OpenRouter:DenyDataCollection` | true | Every OpenRouter request carries `provider.data_collection = "deny"`: endpoints that store data or train on it are excluded |
| `Privacy:DisableQualitativeAssessment` | false | When true, the optional full-text "qualitative assessment" never runs, even if ticked. It sends the regulation and policy text as a whole to the AI model |
| `Privacy:AllowedOutboundHosts` | empty (no limit) | When set, the API refuses any outgoing HTTP call to a host not in the list (exact names or `*.suffix`). This machine (localhost) is always allowed. Covers AI models, document reading, storage and sign-in calls |
| `Privacy:RequireHttpsOutbound` | false | When true, plain `http` calls are refused, except to hosts in `Privacy:AllowInsecureHosts` and localhost |
| `Privacy:AllowInsecureHosts` | empty | Hosts on a trusted private network that may be called over plain http |
| `RegulRetrieval:EmbeddingProvider` | `azure-openai` in `appsettings.json` | Set `local` for client installs so search vectors are made on the server (bge-micro-v2) and nothing leaves. An admin can also change it in the app |

Not covered by the allow-list: database connections (PostgreSQL), Kafka, and anything not made through the standard HTTP client factory. The client's firewall
covers those.

## 2. What happens when a model cannot be served under the rules

With strict routing, OpenRouter answers 404 "No endpoints found matching your data policy" for a model no provider serves under the rules. The app turns that into:
"OpenRouter has no provider for '<model>' that meets this installation's data-protection rules (zero data retention and no training on your data). Choose another
model, or change Privacy:OpenRouter settings if your policy allows it." It never retries without the protection.

## 3. Model check (live test, 15 Oct 2026)

Tiny requests to every OpenRouter model in the catalog, with `zdr + data_collection deny`. Which provider answered is what OpenRouter reported on that request and
can change.

| Works under strict rules (provider seen) | Does not work under strict rules |
|---|---|
| anthropic/claude-opus-5, claude-sonnet-5, claude-haiku-4.5 (Amazon Bedrock) | qwen/qwen3.8-max-0902, qwen3.5-plus-20260420, qwen3.5-flash-02-23 (no zero-retention endpoint; they work with no-training only, via Alibaba) |
| openai/gpt-5.6-sol, gpt-5, gpt-5-mini, gpt-4o, gpt-4o-mini (Azure) | The free models (":free"): none qualify, or are unavailable |
| google/gemini-3.6-flash, 3.5-flash, 2.5-flash, 2.5-flash-lite (Google) | |
| x-ai/grok-4.6, grok-4.3 (xAI) | |
| deepseek/deepseek-v4-pro (Reka), deepseek-v4.1-flash (Together) | |
| moonshotai/kimi-k3 (DeepInfra), kimi-k2.6 (CoreWeave) | |
| z-ai/glm-4.6 (DeepInfra), glm-4.5-air (Z.AI) | |

Note: zero retention can send a model to a different company than the default route (for example OpenAI models via Azure, Anthropic models via Amazon Bedrock).
For a client with data-residency rules, check the provider's region before approving.

## 4. How to verify

| Question | How to check |
|---|---|
| Is the app sending the OpenRouter rules? | Sign in as the platform super admin and open `GET /nd/admin/settings/privacy-posture`. `openRouter.sentWithEveryRequest` shows exactly what is added to each request. Or read `OpenRouterLlmClient.ExtraFields` in `bcp-api/Services/Llm/ProviderLlmClients.cs` |
| Does OpenRouter honour it? | Account level: openrouter.ai/settings/privacy, opt out of providers that train on your data (paid and free models). Per request: the Activity log on OpenRouter shows the provider that served each call |
| Is outbound traffic limited? | The same endpoint shows `outbound.restricted` and the allowed hosts. Then try a call to a host not in the list: the app logs "Blocked outbound call" with the host name |
| Is the full-text review off? | `qualitativeAssessmentDisabled` on the endpoint |
| Where are search vectors made? | `embeddingProvider` on the endpoint (`local` means on the server) |
| Is Azure Document Intelligence on HTTPS and which resource? | `documentReading.azureDocumentIntelligenceHost` and `usesHttps` on the endpoint |
| Which region does Azure process in? | Azure portal, the Document Intelligence resource, Overview, Location (not visible from the app). Also check Networking (public access vs private endpoint) |
| Do logs hold document text? | See section 5 |

## 5. Log audit (15 Oct 2026)

Search of the API source for log statements that include document text, prompts or AI answers found none. Logs record page counts, character counts, token counts,
clause numbers and the provider and model names. Two residual cases to know about: error bodies from storage, the node bridge and AI providers are logged or included
in error messages, each cut to 300 characters; a provider error could in rare cases repeat part of the request. Re-run the audit before each client release:

```
grep -rnE "Log(Information|Warning|Error|Debug)\(" --include=*.cs bcp-api/Services bcp-api/Controllers bcp-api/Workers | grep -iE "prompt|markdown|excerpt|content|body"
```

## 6. Other AI providers (direct APIs)

The same rule applies to every provider: what we send is clause text and retrieved policy passages (and, only if enabled, the qualitative text). Direct provider
APIs (OpenAI, Anthropic, Google, xAI, Moonshot, DeepSeek, Zhipu, Qwen) have no per-request retention switch in our client, so their account-level terms apply and the
outbound allow-list is the control: leave only approved hosts in `Privacy:AllowedOutboundHosts`.

Two things to be aware of:
- The older dual-verify flow can send whole PDF files to providers that accept PDFs (Google, Anthropic). It is not used by the main analysis. For client installs
  keep those providers off the allow-list unless that flow is approved.
- A model the client hosts themselves can be reached with the OpenAI-compatible client by setting its base address (`OpenAI:BaseUrl`) to the client's own endpoint
  and choosing the provider "openai" with the model's own name: the app accepts model names outside its dropdown list. This has NOT been tested against a real
  self-hosted model (for example vLLM), so test it in the pilot before promising a fully contained install.
