# Quota design: managed units, reservation, caps and alerts

Status: **DRAFT for approval. No plan price or quota value is decided here.**

This document designs how managed AI usage is metered, reserved, finalized and capped so that a customer, a bug or a
provider price change cannot create an unbounded bill. It pairs with `unit-economics.md`, which holds the cost
formulas and scenarios. Values are named variables (`Q_...`, `U_...`, `S_...`, `P_...`) with `[V]` meaning "fill from a
real source" and `[D]` meaning "decision for you". No vendor price appears here.

## 1. Goals and principles

1. **Never spend more than a hard cap**, even under concurrency, retries, streaming or a crashed worker.
2. **Meter on the server, always.** A local client is not trusted to report its own usage.
3. **One unit for everything managed** (LLM, OCR, voice, storage), so plans and caps are simple to explain.
4. **Charge from real cost.** When the provider reports the cost, use it. When it does not, estimate with a price
   table plus a safety margin, and label the line as estimated.
5. **Reserve before, finalize after.** Reserve the worst case before a call, then replace it with the actual cost.
6. **Fail closed on the money path.** If the meter is unavailable, managed calls are refused, not allowed free.
7. **Local and BYOK work is never blocked by a quota.** Only our cost is metered.

## 2. Where limits apply per architecture

| Architecture | Metered server-side | Not metered for cost |
|---|---|---|
| 1 Local/BYOK only | Licence and plan check, seats, sync storage if hosted | All AI, OCR, voice, parsing (customer pays or it is local) |
| 2 Managed only | Everything that calls a managed provider, plus storage and bandwidth | Local UI, formatting, exports built in the browser |
| 3 Hybrid | Managed calls and hosted storage/bandwidth; BYOK calls only for rate and abuse limits | Local features and BYOK model cost |

## 3. Recommendation: unmetered local versus metered server-side

### 3.1 Unmetered local features (no per-use limit, no cost to us)

These run on the customer's machine or with their own key and do not use our paid resources:
- Document parsing and OCR with local engines (for example Tesseract, RapidOCR, local Docling).
- Section splitting, chunking, keyword (BM25) search, query expansion tables and ranking.
- Local embeddings when a local model is used.
- Local speech-to-text and text-to-speech models.
- Building and downloading exports (Excel, PDF) from data already on the device.
- Any LLM call made with the **customer's own API key** (BYOK): the provider bills the customer.
- The user interface, search over already-loaded data, offline review.

Even these keep a **licence check and a generous rate limit** to stop abuse, but no unit is consumed.

### 3.2 Must be metered server-side

Anything where **we pay a third party or store data for the customer**:
- Every managed LLM call (judgment, prompt generation, dual-verify, quality assessments).
- Managed embeddings.
- Managed OCR or document intelligence (paid vendor OCR).
- Managed STT, TTS and any realtime voice session.
- Hosted storage (documents, extractions, exports) and bandwidth (egress).
- Anything that fans out into many paid calls (a whole-document analysis is many clause calls).
- Managed model access to free or rate-limited models (limit by requests, not units).

### 3.3 Why local usage must not be trusted

A local client can be modified. Any limit that only the client enforces can be removed. So the server must
enforce quotas on every call that costs us money, and must own the counters.

## 4. Data to record (metering data model)

One append-only usage ledger plus small counter tables. All rows carry the workspace (tenant) id.

```
usage_event (append-only)
  id, tenant_id, user_id, feature, resource_kind (llm | embed | ocr | stt | tts | storage | egress)
  provider, model, request_id / idempotency_key
  state (reserved | finalized | released | failed)
  reserved_mu, final_mu, cost_usd, cost_estimated (bool), price_table_version
  units_raw (tokens in/out/cache, pages, minutes, bytes)
  created_at, finalized_at, run_id

quota_counter (one row per tenant x period x resource pool)
  tenant_id, period_start, period_kind (day | month), pool, used_mu, reserved_mu, cap_mu

cap_config (per plan, per tenant override)
  plan, pool, daily_cap_mu, monthly_cap_mu, per_call_max_mu, concurrency_max, rate_per_minute

provider_budget (global)
  provider, month, budget_usd, spent_usd, reserved_usd, alert_state
```

Already present in the product: a credit ledger with cost, credits and billed value per call, and a per-call guard that
refuses to start an analysis when a workspace has no credits left. What is **missing**: reservation before a call,
daily caps, per-call caps, non-LLM resources, and provider-level budgets.

## 5. Cost inputs to normalize

For each resource we need a cost in USD for one event:

```
LLM   : cost = (T_in*P_in + T_out*P_out + T_cr*P_cache_read + T_cw*P_cache_write) / 1e6     (or the provider-reported cost)
Embed : cost = T_embed * P_embed / 1e6
OCR   : cost = Pages * P_ocr_page
STT   : cost = Minutes * P_stt_min
TTS   : cost = Characters * P_tts_char / 1000
Store : cost = GB_month * P_storage_gb_month     (charged as a monthly pool, see 6.3)
Egress: cost = GB * P_egress_gb
```

`P_*` are variables from the price table (`unit-economics.md`, section 10). The price table has a **version number**
stored on each usage event, so a later price change never silently changes past charges.

## 6. Normalized managed unit (MU)

### 6.1 Definition

```
1 MU = U_mu  USD of reference cost                                   [D: choose U_mu, for example a round fraction of a cent]
MU_charged = ceil( CostUSD_effective / U_mu )      (never below 1 for a real call)
CostUSD_effective = CostUSD_actual                  if the provider reported the cost
                  = CostUSD_estimated * (1 + S_price)   if the cost came from our price table
```

`S_price` `[D]` is a small uplift on table-based estimates so a stale table under-bills less often (section 8).

Because MU is derived from cost, one MU counter works across models, providers and resources without a separate
quota per model. A cheaper model consumes fewer MU for the same work, a premium model consumes more.

### 6.2 Optional weight per resource (only if you want quotas to differ by resource)

```
MU_charged = ceil( CostUSD_effective * W_resource / U_mu )       W_llm = 1 by default
```

Use `W_resource > 1` to make a scarce or risky resource (for example realtime voice) count more. Start with all
weights at 1 and change them only with evidence.

### 6.3 Pools

Not everything should share one counter. Recommended pools `[D]`:

| Pool | Consumes | Reason for a separate pool |
|---|---|---|
| `ai` | LLM, embeddings, managed OCR | The main variable cost |
| `voice` | STT, TTS, realtime | Different spike pattern and price unit |
| `storage` | GB-months | Accrues over time, not per call |
| `egress` | GB downloaded | Easy to abuse, easy to under-measure |

A plan then has one monthly cap per pool: `Q_basic_ai, Q_pro_ai, Q_max_ai`, and so on `[D]`.

### 6.4 Customer-facing meaning

Show the customer **managed units used and left**, and optionally a friendly translation ("about X analyses left at
your average size"), computed from their own average MU per analysis. Never show provider cost or margin to the
customer. The MU-to-dollar value is an internal price decision (the credit price today).

## 7. Reservation and finalization flow

Goal: the cap can never be exceeded, even with many concurrent calls, and unused reservation is returned.

```
1. ESTIMATE      est_mu   = ceil( CostUSD_worst_case / U_mu )
                 worst-case cost uses: input tokens counted from the actual prompt,
                 output tokens = max_output_tokens set for the call, no cache benefit assumed.
2. RESERVE       reserve_mu = ceil( est_mu * (1 + S_reserve) ) capped at per_call_max_mu
                 Atomic, in one statement per counter (day, month, provider):
                   UPDATE quota_counter
                      SET reserved_mu = reserved_mu + :reserve_mu
                    WHERE tenant_id=:t AND period=:p AND pool=:pool
                      AND used_mu + reserved_mu + :reserve_mu <= cap_mu
                 If zero rows updated  -> REFUSE (429 / "quota reached"), nothing was called.
                 Insert usage_event(state=reserved, idempotency_key).
3. CALL          Call the provider with a timeout and with max_output_tokens = the reserved bound.
4. FINALIZE      On success: actual cost from the provider (or table), MU_charged computed as in 6.1.
                 In one transaction: reserved_mu -= reserve_mu ; used_mu += MU_charged ;
                 usage_event(state=finalized, final_mu, cost_usd, cost_estimated).
                 If MU_charged > reserve_mu (estimate too low): charge the actual amount, allow the counter to go
                 over cap by that overshoot, raise an overshoot alert, and block the next call.
5. RELEASE       On failure or cancellation before any billable output: reserved_mu -= reserve_mu ;
                 usage_event(state=released). If the provider billed anyway (partial output), finalize the billed part.
6. RECOVER       A sweeper releases reservations older than a TTL (longer than the call timeout) and
                 raises an alert if any are found, since that means a worker crashed.
```

Rules:
- **Idempotency key per call.** A retried request with the same key finalizes once, so a network retry cannot charge twice.
- **Reserve at the smallest sensible unit.** Reserve per clause call, not per whole analysis, so an analysis stops
  mid-run when the cap is reached instead of overshooting by a whole run. Optionally also reserve a run-level
  budget so an obviously huge run is refused up front with a clear estimate.
- **Streaming and long calls:** reserve for `max_output_tokens`, finalize from the final usage message, release the rest.
- **Batch and fan-out:** reserve the sum for the batch before starting, finalize per item.
- **Estimated costs** stay labeled `cost_estimated = true` and are reconciled monthly against the provider invoice.

## 8. Safety margin

Three separate margins, all decisions `[D]`:

| Margin | Applies to | Purpose | Formula |
|---|---|---|---|
| `S_reserve` | Reservation | Covers a wrong estimate before the call | `reserve = est * (1 + S_reserve)` |
| `S_price` | Estimated cost from a price table | Covers a stale table and tokenizer differences | `cost_eff = cost_table * (1 + S_price)` |
| `S_cap` | Caps versus the provider's own limit | Leaves room so our cap trips before the provider's | `cap_mu = (ProviderLimit_usd * (1 - S_cap)) / U_mu` split across tenants |

Also keep the **plan-level** margin as the markup in the credit price (`Billed = Cost * Markup`). The safety margins
protect the cap. The markup protects gross margin. They are different things.

## 9. Hard caps

All caps are enforced in step 2 of the flow, in the same atomic statement, so they cannot be bypassed by concurrency.

| Cap | Scope | Purpose | Variable |
|---|---|---|---|
| Monthly cap | Tenant x pool | The paid plan allowance | `Q_plan_pool` |
| Daily cap | Tenant x pool | Stops a runaway job or leaked key from burning a month in a day | `D_plan_pool` (for example a fraction of the monthly cap, `D = Q / d_days_factor`) `[D]` |
| Per-call cap | Single call | Stops one enormous prompt | `per_call_max_mu` |
| Per-run cap | One analysis | Stops one enormous analysis | `run_max_mu` |
| Per-user daily cap | User within a tenant | Stops one member using the whole tenant allowance | `Dmax_user` |
| Concurrency cap | Tenant | Limits parallel managed calls | `conc_max` |
| Rate limit | Tenant and IP | Requests per minute | `rpm_max` |
| Global provider cap | Provider, all tenants | Our total exposure to one vendor | `ProviderBudget_usd` (section 10) |
| Global platform cap | All managed spend | Circuit breaker | `PlatformBudget_usd_day` |

What happens at a cap `[D]` (choose per plan and per cap):
- **Block** the call with a clear message and the reset time.
- **Downgrade** to a cheaper model, if the plan allows it, and label the result with the model used.
- **Queue** until the next period (only for non-urgent batch work).
- **Offer a top-up** (paid extra units) instead of blocking, with the price shown before confirming.

Never fail open: if the counter store is unreachable, refuse managed calls and keep local features working.

## 10. Provider budget alerts

Set a **budget on each provider account itself** where the provider supports it (for example a monthly credit limit on a
gateway key or a spend limit in a vendor console; verify that each provider has one and how it behaves when exceeded,
using the checklist in `unit-economics.md`). Then add our own alerts, because the provider limit is a last resort.

| Alert | Trigger | Action |
|---|---|---|
| Budget threshold | Provider spend (plus reserved) reaches 50, 75, 90, 100 percent of `ProviderBudget_usd` | Notify at each level; at 90 percent switch new work to cheaper models or queue; at 100 percent block |
| Spend velocity | Spend over the last hour or day is more than `k` times the trailing average | Page the owner; auto-tighten the daily caps |
| Price drift | Provider-reported cost per token differs from the price table by more than `x` percent | Alert and require a table review (checklist in `unit-economics.md`, section 10) |
| Estimated share | Share of spend that is `cost_estimated` exceeds `y` percent | Reduce reliance on estimates or update the table |
| Margin | Realised margin below `G_floor` for the last `w` days | Alert; consider raising markup or tightening caps |
| Reservation leak | Reservations older than the TTL exist | Investigate a crashed worker |
| Overshoot | Final charge exceeded the reservation | Review estimation and `S_reserve` |
| Cap hit rate | Many tenants hitting a cap | Review plan quotas or offer top-ups |
| Reconciliation | Monthly sum of our ledger differs from the provider invoice by more than `z` percent | Investigate missing or double-counted calls |

Alert channels `[D]`: email and an in-product banner for the platform admin; chat or paging for velocity and 100 percent events.

## 11. Plan quotas (variables only)

| Quota | Basic | Pro | Max |
|---|---|---|---|
| Monthly `ai` MU | `Q_basic_ai` | `Q_pro_ai` | `Q_max_ai` |
| Daily `ai` MU | `D_basic_ai` | `D_pro_ai` | `D_max_ai` |
| Monthly `voice` MU | `Q_basic_voice` | `Q_pro_voice` | `Q_max_voice` |
| Storage GB and egress GB per month | `S_basic`, `E_basic` | `S_pro`, `E_pro` | `S_max`, `E_max` |
| Per-call and per-run max MU | `per_call_*`, `run_max_*` | same | same |
| Concurrent managed calls | `conc_basic` | `conc_pro` | `conc_max` |
| Local/BYOK | unlimited | unlimited | unlimited |
| At cap | `[D]` | `[D]` | `[D]` |

Set each value from the scenario table in `unit-economics.md`: the monthly `ai` cap should be the amount of managed
usage at which the plan's **Heavy** scenario still meets `G_floor`.

## 12. Edge cases to design for

- **Retries by the customer:** same idempotency key means one charge; a new request means a new charge.
- **Provider returns success but no usage data:** finalize using the reserved worst case and mark it estimated.
- **Provider error after billing:** finalize the billed portion, do not release it.
- **Free or rate-limited models:** limit by request count per day and mark them not for customer data.
- **Long-running batch:** show remaining MU during the run and stop cleanly when the cap is reached, keeping finished results.
- **Credit and refund of units:** support an adjustment line for support cases, always with a reason and an actor.
- **Plan change mid-month:** prorate the cap, keep used MU, apply the new cap to the remainder.
- **Period rollover:** reset counters at a fixed timezone boundary `[D]`; do not carry unused MU unless a plan says so.
- **Clock and timezone:** define one billing timezone and use it for both daily and monthly caps.

## 13. Mapping to the current product and the gaps

| Need | Today | Gap |
|---|---|---|
| Per-call cost, credits and billed value | Stored per call for LLM calls | Extend to OCR, voice, storage, egress |
| Price, markup and margin setting | Set by the platform admin, applies to new calls | Add `U_mu`, `S_price`, `S_reserve` if MU is adopted |
| Stop when out of credits | Checked when an analysis starts | Reserve per call, daily caps, per-call and per-run caps |
| Exact provider cost | Reported by the gateway for its calls | Token-only providers still estimated: add a price-table version and monthly reconciliation |
| Provider budget | Set manually on the provider account | Add our own budget tracking and alerts (section 10) |
| Model chosen per run | Stored per run | Fine as is; keep for support and audit |

## 14. Exactly what data replaces each variable

| Variable | Real value comes from | How |
|---|---|---|
| `P_*` (all vendor prices) | Vendor or gateway price pages and invoices | Checklist in `unit-economics.md` section 10, with source URL and date |
| `U_mu` | **Your decision** | Choose so a typical call is a small whole number of MU |
| `S_price` | Difference between estimated and invoiced cost over 2 to 3 months | Reconcile monthly and set to cover the typical error |
| `S_reserve` | Ratio of actual to reserved cost over 30 days | Use a high percentile of the estimation error |
| `S_cap` | Provider behavior at its limit | Read the provider terms; test with a low limit |
| `max_output_tokens` per feature | Our measured output-token distribution | 99th percentile per feature |
| Average and 90th percentile MU per analysis | Our usage events | 30 days of finalized events by feature |
| `Q_*`, `D_*` per plan | **Your decision**, from the scenario table | After `unit-economics.md` sections 5 and 8 are filled |
| Heavy-user share and behavior | Our usage events by tenant | Distribution of monthly MU per tenant |
| Alert thresholds `k, x, y, z, w` | **Your decision** | Start conservative and tune on real alerts |
| Provider budgets `ProviderBudget_usd` | **Your decision** and provider account limits | Set after the first month of measured spend |
| TTL for reservations | Provider timeouts we observe | Longer than the longest call timeout |

## 15. Rollout order (suggested, for approval)

1. Fill the variables (section 14) and the scenario table before choosing any plan quota.
2. Build the usage ledger fields and the counters for the `ai` pool, with reservation and finalization, behind a flag.
3. Add hard daily and monthly caps and per-call and per-run caps, fail closed.
4. Add provider budget tracking and the alerts in section 10.
5. Extend metering to OCR, then voice, then storage and egress.
6. Only then publish plan quotas to customers.

## 16. Decisions needed from you

1. The MU size `U_mu` and whether to use per-resource weights.
2. Which pools to expose to customers and which to keep internal.
3. Behavior at a cap: block, downgrade, queue or top-up, per plan.
4. The three safety margins (`S_reserve`, `S_price`, `S_cap`).
5. Daily cap policy (fraction of monthly) and the billing timezone.
6. Provider budgets and alert thresholds and channels.
7. Plan quotas for Basic, Pro and Max. **Not set here. Prices are not set here either.**
