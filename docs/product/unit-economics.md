# Unit economics: three architectures, formulas and scenarios

Status: **DRAFT for approval. No plan price is proposed or finalized here.**

This document gives the formulas to price Basic, Pro and Max, and the scenarios to test them. Every vendor price,
fee rate and support cost is a **named variable** (written `P_...`, `F_...`, `C_...`). Nothing here is a current
vendor price. Section 9 lists what is needed to replace each variable with a real value, and section 10 is the
verification checklist to run before any number is used.

Conventions
- Money is USD unless a variable says otherwise. `M` means one calendar month.
- `[V]` marks a variable that must be filled from a real source. `[A]` marks an assumption that must be replaced by
  measured usage. The usage numbers in the scenarios are `[A]`, not measurements.
- In this product the managed cost of one AI call is already recorded (see section 11): our real cost, credits
  charged, and billed value per call.

## 1. The three architectures

| # | Architecture | Where AI runs | Who pays the model bill | What we still pay for |
|---|---|---|---|---|
| 1 | **Local / BYOK only** | On the customer's machine, or with the customer's own provider key | The customer | Licence and plan gating, updates, support, payment fees, taxes, any hosted sync or storage |
| 2 | **Managed cloud AI only** | Our provider accounts (for example a gateway or direct vendor) | Us, recovered through plan price and metering | Everything in 1, plus model usage, managed OCR/STT/TTS, storage and bandwidth for AI inputs and outputs |
| 3 | **Hybrid: local/BYOK plus managed AI** | Local or BYOK for heavy or private work; managed for convenience, speed or premium models | Split: customer for BYOK, us for managed | Everything in 1, plus managed usage up to plan quotas |

Economic consequences (qualitative, to be confirmed with numbers):
- Architecture 1 has the lowest variable cost and the lowest revenue ceiling per customer. Margin is dominated by
  support and payment fees.
- Architecture 2 has the highest variable cost and the largest exposure to provider price moves and heavy users.
  Metering and caps are mandatory.
- Architecture 3 lets a plan include a bounded managed allowance while heavy users move to BYOK, which caps our
  exposure without removing the managed option.

## 2. Cost model: formulas

All formulas are per customer per month unless stated. `N` is the number of paying customers in the month.

### 2.1 Fixed monthly infrastructure

```
FixedInfra  = C_hosting + C_database + C_auth + C_monitoring + C_email + C_domain_ssl
            + C_ci_cd + C_backups + C_other_fixed                        [V each]
FixedPerCustomer = FixedInfra / N                                        (falls as N grows)
```

Rule: report unit economics **at the current N and at target N**, because fixed cost per customer at N = 20 is very
different from N = 500.

### 2.2 Payment fees (per customer)

```
Fee_pay = F_pay_pct * Charge + F_pay_fixed          [V]  (per successful charge)
Charge  = PlanPrice + Tax_collected_on_top          (depends on tax model, see 2.8)
```

If billed annually: `Fee_pay_month = Fee_pay / 12`, but keep the refund and chargeback exposure (2.7) on the full
annual amount.

### 2.3 Managed model usage (LLM, per call, summed over the month)

```
Cost_call = ( T_in    * P_in
            + T_out   * P_out
            + T_cr    * P_cache_read
            + T_cw    * P_cache_write ) / 1,000,000            [V: P_* in USD per 1M tokens]

Cost_LLM_month = SUM over calls (Cost_call) * (1 + R_retry)      [A: R_retry = share of retried calls]
```

Where `T_in` is non-cached input tokens, `T_out` output tokens, `T_cr` cache-read tokens and `T_cw` cache-write tokens.
If a gateway reports the cost per call, use the reported cost instead of the formula and keep the formula only as a
forecast. Managed embeddings: `Cost_embed = T_embed * P_embed / 1,000,000`.

Managed OCR / document parsing: `Cost_ocr = Pages * P_ocr_page` `[V]`.

### 2.4 Voice: speech-to-text and text-to-speech

```
Cost_STT = Minutes_audio_in  * P_stt_min                          [V]
Cost_TTS = Characters_out    * P_tts_char / 1,000  (or Minutes_out * P_tts_min)   [V]
Cost_voice_month = Cost_STT + Cost_TTS
```

Voice that runs on the customer's device has `Cost_voice = 0` for us. Voice through a managed realtime or streaming
API may also bill per audio token or per connected minute. Use whichever unit the vendor bills in, and record which.

### 2.5 Storage and bandwidth

```
Cost_storage   = (GB_stored_avg) * P_storage_gb_month                [V]
Cost_bandwidth = (GB_egress)     * P_egress_gb                       [V]
Cost_db_rows   = (rows or GB in database beyond included) * P_db_unit  [V]
Cost_files_month = Cost_storage + Cost_bandwidth + Cost_db_rows
```

Egress is the easiest to underestimate: every list refresh, document download, export and status poll adds up. It
must be measured, not guessed (section 9).

### 2.6 Support cost

```
Cost_support = Tickets_per_customer_month * Minutes_per_ticket / 60 * C_support_hour   [A][V]
             + C_support_tooling / N                                                    [V]
```

Support hours are usually the largest cost for a low-price plan. A plan price that ignores them is not a real price.

### 2.7 Refunds and chargebacks

```
Loss_refund      = Rate_refund     * PlanPrice                       [A]
Loss_chargeback  = Rate_chargeback * (PlanPrice + F_chargeback_fee)  [A][V]
Loss_refund_cost_of_service = Rate_refund * (variable cost already incurred)  (we usually cannot recover it)
```

Refunds also usually return the payment fee only partly. Keep `F_pay_pct * Charge` as a sunk cost when computing the
loss on a refunded charge.

### 2.8 Taxes and Merchant-of-Record (MoR) fees

Two possible models. Do not assume one.

```
Model A: we are the seller and handle tax ourselves
  Tax_collected = TaxRate * NetPrice          (passes through to the tax authority, is not revenue)
  Cost_tax_admin = C_tax_software + C_accountant_tax / N          [V]

Model B: a Merchant of Record is the seller
  Fee_mor = F_mor_pct * Charge + F_mor_fixed                       [V]
  (this usually replaces Fee_pay and Cost_tax_admin; confirm exactly what is included)
```

Revenue for margin purposes is **net of tax collected**: `NetRevenue = Charge - Tax_collected`.

### 2.9 Gross margin per plan

```
Revenue_month   = PlanPrice_net_of_tax
COGS_month      = Fee_pay (or Fee_mor)
                + Cost_LLM_month + Cost_embed + Cost_ocr + Cost_voice_month
                + Cost_files_month
                + FixedPerCustomer_attributable          (allocate only the share that scales with the plan)
                + Cost_support
                + Loss_refund + Loss_chargeback
GrossMargin_month = Revenue_month - COGS_month
GrossMargin_pct   = GrossMargin_month / Revenue_month
```

Target: `GrossMargin_pct >= G_target` `[V: set by you]`. The **price floor** for a plan is:

```
PlanPrice_min = ( COGS_excl_pay_and_refund_and_tax ) / ( 1 - F_pay_pct - Rate_refund - Rate_chargeback - G_target )
              (add F_pay_fixed and tax model terms once known)
```

Managed-usage pass-through with markup (already implemented for credits, see section 11):

```
Billed = Cost_managed * Markup             GrossMargin_managed = 1 - 1/Markup
```

## 3. Plan definitions used in the scenarios

The plan contents are **proposals to be approved**, expressed as quotas, not prices.

| Quota | Basic | Pro | Max |
|---|---|---|---|
| Managed units per month (section 6 of quota-design.md) | `Q_basic` | `Q_pro` | `Q_max` |
| Included managed OCR pages per month | `Pg_basic` | `Pg_pro` | `Pg_max` |
| Included managed voice minutes per month | `V_basic` | `V_pro` | `V_max` |
| Stored GB | `S_basic` | `S_pro` | `S_max` |
| Local/BYOK features | unlimited | unlimited | unlimited |
| Support level | standard | priority | priority plus onboarding |

## 4. Scenario usage assumptions `[A]`

These are placeholders to be replaced with measured usage (section 9). They define volume only, not price.

| Driver | Conservative | Expected | Heavy user |
|---|---|---|---|
| Clause analyses per month, Basic | `a_b_c` | `a_b_e` | `a_b_h` |
| Clause analyses per month, Pro | `a_p_c` | `a_p_e` | `a_p_h` |
| Clause analyses per month, Max | `a_m_c` | `a_m_e` | `a_m_h` |
| Average tokens per analysis: input / output / cache-read | `t_in`, `t_out`, `t_cr` | same | same, with larger `t_in` |
| Managed OCR pages per month | fraction `f_ocr_c` of quota | `f_ocr_e` | 100 percent or more of quota |
| Managed voice minutes per month | `f_v_c` of quota | `f_v_e` | 100 percent or more of quota |
| GB stored, GB egress | `gs_c`, `ge_c` | `gs_e`, `ge_e` | `gs_h`, `ge_h` |
| Support tickets per month | `k_c` | `k_e` | `k_h` |
| Refund and chargeback rate | `r_c` low | `r_e` | `r_h` high |
| Retry share `R_retry` | low | mid | high |

Interpretation:
- **Conservative:** light usage, few tickets. Shows the best-case margin and whether a plan is priced too high.
- **Expected:** the median customer. This is the number to plan the business on.
- **Heavy:** a customer who uses every included allowance and pushes past it. Shows the worst-case loss per customer
  and is the case caps must protect (quota-design.md).

## 5. Scenario computation template

For each plan `X in {Basic, Pro, Max}` and each scenario `s in {conservative, expected, heavy}`:

```
Cost_LLM(X,s)   = a(X,s) * [ t_in*P_in + t_out*P_out + t_cr*P_cache_read ] / 1e6 * (1 + R_retry(s))
Cost_ocr(X,s)   = Pages(X,s) * P_ocr_page
Cost_voice(X,s) = Minutes_stt(X,s)*P_stt_min + Chars_tts(X,s)*P_tts_char/1000
Cost_files(X,s) = gs(s)*P_storage_gb_month + ge(s)*P_egress_gb
Cost_support(X,s) = k(s) * Minutes_per_ticket/60 * C_support_hour
Loss(X,s)       = r(s) * PlanPrice(X)         (+ chargeback fee)
COGS(X,s)       = Fee_pay(X) + Cost_LLM + Cost_ocr + Cost_voice + Cost_files + Cost_support + Loss + FixedAttributable(X)
GM(X,s)         = PlanPrice_net(X) - COGS(X,s)
```

### Result table to fill once variables are known

| Plan | Scenario | Revenue net | COGS | Gross margin | GM % | Meets `G_target`? |
|---|---|---|---|---|---|---|
| Basic | Conservative | | | | | |
| Basic | Expected | | | | | |
| Basic | Heavy | | | | | |
| Pro | Conservative | | | | | |
| Pro | Expected | | | | | |
| Pro | Heavy | | | | | |
| Max | Conservative | | | | | |
| Max | Expected | | | | | |
| Max | Heavy | | | | | |

A plan is acceptable only if **Expected** meets `G_target` and **Heavy** does not go below `G_floor` `[V: your
minimum acceptable margin, possibly 0 or slightly negative for the heaviest case, decided by you]`.

### Worked example with DUMMY numbers (arithmetic demonstration only)

These are not vendor prices and not proposed plan prices. They only show how the formulas combine.

```
Dummy inputs: PlanPrice_net = 100, F_pay = 3 % + 0.30 on a charge of 100, Cost_LLM = 20, Cost_ocr = 5,
              Cost_files = 2, Cost_support = 15, FixedAttributable = 8, Rate_refund = 2 %
Fee_pay = 0.03*100 + 0.30 = 3.30      Loss_refund = 0.02*100 = 2.00
COGS    = 3.30 + 20 + 5 + 2 + 15 + 8 + 2.00 = 55.30
GM      = 100 - 55.30 = 44.70        GM % = 44.7 %
```

## 6. Architecture comparison to complete

| Item | 1 Local/BYOK | 2 Managed only | 3 Hybrid |
|---|---|---|---|
| Model cost to us | 0 | full | up to managed allowance |
| Exposure to provider price change | none | high | bounded by quota |
| Support load | high (customer setup, keys, local install) | medium | high to medium |
| Revenue ceiling per customer | lowest | highest | mid to high |
| Data privacy claim | strongest | needs a data-processing story | mixed, per feature |
| Metering needed | licence only | full | full for managed, licence for local |

Fill the cost rows with section 5 results per architecture: architecture 1 sets the managed terms to zero; architecture 2
sets all AI terms to managed; architecture 3 splits usage with a share `s_managed` `[A]` (managed) and `1 - s_managed` (BYOK).

## 7. Which resources are metered

See quota-design.md, section 3 for the metering recommendation (local unmetered versus server-side metered).

## 8. Sensitivities to run before pricing

Run the scenario table with each of these changed and record the change in GM %:
1. `P_in` and `P_out` up 25 percent, and down 25 percent.
2. Heavy-user share of customers `h` at 5, 10 and 20 percent of a plan.
3. `Rate_refund` and `Rate_chargeback` doubled.
4. Support minutes per ticket doubled.
5. N at 20, 100 and 500 customers (fixed-cost dilution).
6. MoR versus self-seller tax model.
7. Egress doubled (often the least understood number).
8. `s_managed` from 20 to 100 percent in the hybrid architecture.

## 9. Exactly what data replaces each variable

| Variable(s) | Real value comes from | Method |
|---|---|---|
| `P_in, P_out, P_cache_read, P_cache_write` | Vendor or gateway published price for each model we allow | Read the vendor page or the gateway model list on the day, record the URL and date (checklist, section 10) |
| Managed cost per call | The provider's own per-call cost report, or our ledger | Already stored per call for gateway calls; token-only providers need the price table |
| `T_in, T_out, T_cr, T_cw` per analysis | Our usage log (`prompt_tokens`, `completion_tokens`, `cached_tokens` per ledger line) | Average and 90th percentile per feature over at least 30 days |
| `R_retry` | Count of judgment retries divided by calls | Add a counter (the analysis already retries on missing gaps or actions) |
| `P_ocr_page`, `Pages` | Managed OCR vendor price; pages processed per customer per month | Vendor page; document page counts from our extraction tables |
| `P_stt_min, P_tts_char` and volumes | Vendor price; audio minutes and characters per customer | Vendor page; a usage meter (does not exist yet) |
| `P_storage_gb_month, P_egress_gb` | Hosting/database provider invoice and price sheet | Invoice lines for storage and egress divided by GB actually used |
| `GB_stored, GB_egress` per customer | Storage bucket sizes and egress by day | Provider dashboard, or our own byte counters on downloads and exports |
| `C_hosting`, `C_database`, other fixed costs | Last 3 months of invoices | Sum by line item; separate one-off from recurring |
| `F_pay_pct, F_pay_fixed, F_chargeback_fee` | Payment provider contract and fee schedule | Contract or dashboard fee report |
| `F_mor_pct, F_mor_fixed` and what is included | Merchant of Record contract | Contract; confirm tax handling, refunds and chargeback terms in writing |
| `TaxRate` and rules by country | Tax adviser or the MoR | Written advice per target market |
| `Rate_refund, Rate_chargeback` | Payment provider report; benchmark until we have history | 12 months of real data, or a stated assumption until then |
| `Minutes_per_ticket, k` | Support tool export: tickets per customer and handling time | Tag tickets by plan; measure median and 90th percentile |
| `C_support_hour` | Real loaded cost of the person or vendor doing support | Payroll or contract cost divided by productive hours |
| `N` (customers) | Billing system | Actual and target |
| `G_target`, `G_floor` | **You** | A decision, not a measurement |
| Plan quotas `Q_*, Pg_*, V_*, S_*` | **You**, informed by the scenario table | A decision after sections 5 and 8 |

## 10. Current-price verification checklist

Run this before any variable is trusted. Record the result in a table with columns: variable, value, source URL or
document, date checked, checked by, next review date.

- [ ] For each model in the allowed list: open the vendor or gateway price page **today** and copy input, output and
      cache prices (and any long-context or batch surcharge).
- [ ] Confirm the price unit (per 1M tokens, per 1K tokens, per character, per audio minute) and convert once.
- [ ] Confirm whether cached input is billed separately and whether a cache-write surcharge applies.
- [ ] Check whether the gateway adds its own fee or a markup over the vendor, and whether its reported cost includes it.
- [ ] Check free-tier and rate-limit rules (for example free models with daily request limits) and mark free models as
      not for customer data.
- [ ] For OCR, STT and TTS: confirm the billing unit, the minimum billed unit and any per-request minimum.
- [ ] For storage and bandwidth: confirm included allowances, overage price and whether egress to the same region is free.
- [ ] Check the date of the last price change and the vendor's stated notice period for changes.
- [ ] Payment and MoR: re-read the fee schedule and the refund and chargeback terms.
- [ ] Taxes: confirm the rules for each target country with the adviser or MoR.
- [ ] Re-run the whole checklist on a fixed schedule (for example monthly) and after any price announcement.

## 11. How this maps to what is already built

- **Cost per call is stored.** Each AI call writes our real cost (exact for gateway calls, estimated for token-only
  providers), the credits charged and the billed value.
- **Price and markup are settings.** One credit is worth `USD_per_credit`, and `Billed = Cost * Markup`. Margin on
  managed usage is `1 - 1/Markup`.
- **Not built yet** (needed by this document): managed OCR, voice and storage/bandwidth cost capture, the normalized
  managed unit, reservation before a call, and caps and alerts. See quota-design.md.

## 12. Decisions needed from you (no price is set here)

1. Which architecture or mix to launch with (1, 2 or 3) and the target hybrid share `s_managed`.
2. Target and floor gross margin (`G_target`, `G_floor`).
3. Tax model: self-seller or Merchant of Record.
4. Whether heavy users are blocked, throttled, downgraded to a cheaper model or offered a paid top-up.
5. Plan quotas for Basic, Pro and Max.
6. Only after the scenario table is filled with real values: plan prices. **This document does not set them.**
