# AI credits and costing: how it works and how to manage it

This document explains what a client is charged for AI, what it really costs us, where to set the price and margin,
and how we stay safe when a provider changes its prices.

## 1. The three numbers on every AI call

Every AI call (an analysis clause, a prompt generation, a dual-verify check) writes one line to the ledger with:

| Number | Meaning | Who sees it |
|---|---|---|
| **Our cost (USD)** | What the provider really charged us. OpenRouter reports the exact cost per call. Anthropic and other direct providers report only tokens, so we estimate the cost from a model price list (marked `*` on screen). | Platform admin only |
| **Credits** | The unit the client is charged in and is topped up in. | Everyone in the workspace |
| **Billed (USD)** | What the credits were worth to the client at the price in force when the call ran (credits x price per credit). | Platform admin, and the client as a dollar value of their credits |

A client never sees our cost. They see credits and the dollar value of those credits.

## 2. The formula (two settings)

```
billed USD = our cost x markup
credits    = billed USD / (USD per credit)
our margin = billed USD - our cost
margin %   = (billed - cost) / billed
```

Both settings live in one place, the **AI credit price** card at the top of **Administration > Workspaces**
(platform super admin only):

| Setting | Default | Meaning |
|---|---|---|
| **1 credit is worth ($)** | 0.01 | How many dollars one credit is worth to a client. 0.01 means $1 = 100 credits. |
| **Markup (x our cost)** | 1.0 | What we multiply our real cost by. 1.0 = no margin, 1.5 = we keep a third of what the client pays, 2.0 = we keep half. |
| **Warn if margin below (%)** | 20 | The AI usage page turns the margin red when the realised margin is under this line. |

The card shows a live example as you type, for instance at 1.5x: "an AI call that costs us $1.00 is charged as $1.50
(150 credits). We keep $0.50 (33.3%)".

Saving applies to **new** AI calls only. Every ledger line keeps the credits and billed value it was written with, so
changing the price never rewrites history.

### Choosing the markup

| Markup | Margin on what the client pays | We keep per $1.00 of provider cost |
|---|---|---|
| 1.0 | 0% | $0.00 |
| 1.25 | 20% | $0.25 |
| 1.5 | 33.3% | $0.50 |
| 2.0 | 50% | $1.00 |

Margin % = (markup - 1) / markup. To reach a target margin m, use markup = 1 / (1 - m). For a 30% margin use 1.43.

## 3. Worked example (a real run)

Clause 3.1 on Kimi K3 through OpenRouter, with markup 1.5:

- Tokens: 16,272 in, 4,969 out
- Our cost (OpenRouter reported): **$0.0797**
- Billed: $0.0797 x 1.5 = **$0.1195**, which is **11.95 credits** at $0.01 per credit
- Our margin: **$0.0398** (33.3%)

A client who buys a **$5 pack** gets 500 credits and can run about 40 such clauses. If Kimi's price doubled tomorrow,
the same clause would cost us $0.16, bill $0.24 and use about 24 credits. The client gets about 20 clauses from the
same $5, and our margin percentage is unchanged.

## 4. Where to see it

| Screen | Who | Shows |
|---|---|---|
| **Administration > Workspaces** (price card) | Platform admin | Set the credit price and markup, with a live example |
| **Workspaces** row and its credit panel | Platform admin | Balance and its dollar worth, our cost, billed, our margin, spend by model |
| **Administration > AI usage** | Platform admin | Totals, by business, by model and every call: our cost, billed, margin, red when below the warning line |
| **AI credits** (workspace admin) | The client | Credits left and their dollar value. Never our cost. |
| Gap analysis results page | Everyone | Which AI model produced the analysis (chip) |

## 5. Staying safe when a provider changes its price

**Pass-through by design.** Credits are charged from the cost of each call at the time it runs, not from a fixed price
per analysis. If OpenRouter or a provider raises a model's price, the next call reports a higher cost, bills a
proportionally higher amount and uses more credits. The margin percentage stays the same. A client's prepaid $5 simply
lasts fewer calls. We do not absorb the increase.

Things that still need attention:

1. **Estimated costs (`*`).** Direct Anthropic, OpenAI, Google and similar calls report tokens only, so the cost comes
   from the model price list (setting `ai_model_prices`, USD per 1M tokens, default $3 in and $15 out for unknown
   models). If a provider raises its price and this list is not updated, we under-bill. Two ways to remove the risk:
   route models through OpenRouter, which reports the exact cost, or update the price list when a provider announces a
   change. Watch the `*` marker on the AI usage page.
2. **Keep a margin buffer.** A markup of 1.3 to 1.5 absorbs small price moves and estimation error. At markup 1.0 there
   is no buffer.
3. **Watch the margin line.** The AI usage page turns the margin red under the warning percentage. Check it weekly and
   after any provider price announcement.
4. **Cap the provider account.** Set a credit limit on the OpenRouter key (for example $10 while testing). If it is
   exceeded, requests are rejected until you raise the limit on the key page. That is a hard stop on our spend.
5. **Empty credits stop new work.** When a workspace has used all its credits, starting or re-running an analysis is
   refused. Runs already in progress finish, so one run can overshoot the balance slightly.
6. **Workspaces with no grant are unlimited.** A workspace that has never been given credits is not limited (this keeps
   existing installs working). Give every paying client a grant so the limit applies.
7. **Free models cost $0** (`:free` models on OpenRouter) but are rate limited and may log prompts, so they are for
   testing only, not client documents.

## 6. Day-to-day operations

1. **Set the price once:** Workspaces > AI credit price card. Recommended start: 1 credit = $0.01, markup 1.4 to 1.5,
   warning at 20%.
2. **Sell a pack:** on the client's row open the credit panel and add credits. $5 = 500 credits at the default rate.
   The grant records what it was worth ($5.00) at the time.
3. **Review weekly:** AI usage page, filter by business and period. Compare "Our cost" with "Billed to clients". The
   margin should sit near your target. If it is red, raise the markup or check for `*` estimates that are too low.
4. **Reconcile:** for OpenRouter, each ledger line stores the generation id, so a line can be matched to the OpenRouter
   activity page. Compare the total cost in the AI usage page with the spend on the OpenRouter key.
5. **After a provider price change:** if the provider is on OpenRouter, nothing to do. If it is a direct provider,
   update the model price list.

## 7. Known limits

- Calls made before the billed value was stored show their value at the current credit price. New calls store it at
  write time.
- OpenRouter calls made before the fix on 24 Sep 2026 were not recorded in the ledger, because a parsing bug ignored OpenRouter's
  responses (blank lines before the JSON). It is fixed. Those calls are not in the usage totals. Use the OpenRouter
  activity page for that period.
- The model price list has no admin screen yet. It is a system setting (`ai_model_prices`).
- The balance check happens before a run starts, not during it, so a long run can go slightly negative.
- A credit price change affects new calls only. Clients who already hold credits keep them, and the same credits buy
  slightly more or less AI afterwards.

## 8. Quick reference

```
Set price + margin : Administration > Workspaces > "AI credit price"
Sell credits       : Administration > Workspaces > client row > credit panel > Add credits
See cost & margin  : Administration > AI usage
Client sees        : AI credits page (credits + dollar value, never our cost)
Safe from increases: pass-through pricing + OpenRouter exact costs + margin buffer + key credit limit
```
