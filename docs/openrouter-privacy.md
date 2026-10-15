# OpenRouter data protection

Written 15 Oct 2026. Only the OpenRouter request changed. Analysis, prompts, models, pipeline and pages are untouched.

## What changed
Every request to OpenRouter now includes:

```json
"provider": { "zdr": true, "data_collection": "deny" }
```

- `zdr: true`: OpenRouter routes only to endpoints with a zero-data-retention policy (they keep no prompts or answers).
- `data_collection: "deny"`: OpenRouter excludes endpoints that store data or may train on it.

Code: `bcp-api/Services/Llm/OpenRouterPrivacy.cs` (new) and `OpenRouterLlmClient` in `bcp-api/Services/Llm/ProviderLlmClients.cs`.
Only the OpenRouter client is affected. Direct provider clients (OpenAI, Anthropic, Google, ...) are not.

## Settings (all optional, default on)
| Setting | Default | Effect |
|---|---|---|
| `Privacy:OpenRouter:ZeroDataRetention` | true | sends `zdr: true` |
| `Privacy:OpenRouter:DenyDataCollection` | true | sends `data_collection: "deny"` |

Set both to false and the request body is exactly what it was before.

## What it means for results
- The model is the same. The company serving it can differ: under these rules OpenAI models were served via Azure and Anthropic models via Amazon Bedrock.
- A model with no qualifying endpoint fails with a plain message ("OpenRouter has no provider for '<model>' that meets this installation's
  data-protection rules ...") and is never retried unprotected.

Live test, 15 Oct 2026, tiny request per model, strict rules:

| Works (provider seen) | Does not work |
|---|---|
| anthropic/claude-opus-5, claude-sonnet-5, claude-haiku-4.5 (Amazon Bedrock) | qwen/qwen3.8-max-0902, qwen3.5-plus-20260420, qwen3.5-flash-02-23 (work with no-training only, not with zero retention) |
| openai/gpt-5.6-sol, gpt-5, gpt-5-mini, gpt-4o, gpt-4o-mini (Azure) | the ":free" models |
| google/gemini-3.6-flash, 3.5-flash, 2.5-flash, 2.5-flash-lite (Google) | |
| x-ai/grok-4.6, grok-4.3 (xAI) | |
| deepseek/deepseek-v4-pro (Reka), deepseek-v4.1-flash (Together) | |
| moonshotai/kimi-k3 (DeepInfra), kimi-k2.6 (CoreWeave) | |
| z-ai/glm-4.6 (DeepInfra), glm-4.5-air (Z.AI) | |

## Also set in the OpenRouter account (openrouter.ai/settings/privacy)
- Turn OFF "Allow free endpoints that train on request data" (free models then stop working).
- Keep OFF: paid endpoints that train, free endpoints that publish prompts, 1% data discount.
- Optional: the Zero Data Retention toggles apply the same rule to the whole account.
- For a client or production use a separate API key with guardrails.

## How to check
- Request body: `OpenRouterLlmClient.ExtraFields` in `ProviderLlmClients.cs`; tests in `OpenRouterPrivacyTests.cs`.
- Which provider served a call: the Activity page at openrouter.ai.
