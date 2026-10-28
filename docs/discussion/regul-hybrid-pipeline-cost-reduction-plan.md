# Regul Hybrid Pipeline — LLM Cost Reduction Plan

**Builds on:** [V1 step guide](regul-hybrid-pipeline-detail.md) · [V2 precompute](regul-hybrid-pipeline-v2.md) · [Overview](REGUL-HYBRID-RETRIEVAL-WORKFLOW.md)

**Problem this solves:** V2 moves everything free (expansion, sub-obligations, BM25 index, embeddings) out of the per-run loop. It explicitly leaves Step 8 (LLM judgment) untouched — "LLM every time? Yes." That one step is the entire cost: ~$0.05/clause vs ~$0.00003 for embeddings and $0 for everything else. Retrieval is a rounding error; **judgment is the bill.**

At 80 clauses/run ~ $4.50, a team running a few dozen manuals a week is looking at real recurring spend once this goes to production users. This doc is levers to cut that, ranked by expected impact vs effort, without touching the retrieval design in V1/V2.

---

## Where the money actually goes

| Step | Cost | Share of $4.50 (80 clauses) |
|------|------|------------------------------|
| Steps 0-7 (expansion, BM25, embedding retrieve, fusion, select, context build) | ~$0.0024 | <0.1% |
| Step 8 — LLM judgment | ~$4.00 (~$0.05 x 80) | ~99% |
| Step 9 — Save | $0 | 0% |

Any optimization that doesn't touch Step 8 is optimizing the wrong 1%. The retrieval side (V1/V2) is worth keeping as-is — it's already free.

---

## Levers, ranked

### L1 — Prompt caching on the internal-doc context (biggest win, low effort)

Within one analysis run, and across repeat runs on the same internal manual, the **internal sections fed into Step 7's context** overlap heavily clause to clause (the same core policy sections keep getting retrieved for related clauses). Both Anthropic and OpenAI charge roughly **10% of normal input price** for cached-prefix tokens.

- Structure the Step 8 prompt so the **stable part** (system instructions + the internal document's retrieved sections, sorted deterministically) comes first, and the **variable part** (this clause's text + judgment question) comes last.
- Cache TTL just needs to outlive one analysis run (minutes), so this doesn't need new infrastructure — it's a prompt-ordering change plus turning on the provider's cache flag.
- Expected saving: if context tokens are ~70% of the Step 8 prompt (typical for a judgment call with 15-56 sections attached), caching that portion cuts the input-token cost of Step 8 by roughly 60%.

**Effort:** low — reorder the prompt builder, no schema/DB change.

### L2 — Judgment cache keyed by (clause, section-set, prompt version)

If a user re-runs the same clause against the same resolved section-set (same internal doc version, same retrieval result) — which happens on retries, re-opens, and reviewer/checker workflows touching the same run — the LLM call is identical work. V2 already tracks `ready_at`/`version` for retrieval; extend the same idea one step further.

- Hash `(regulation_point_id, sorted section IDs + their content hash, judgment prompt version)` → store the LLM verdict + gap text against that hash.
- On a re-run, check the hash before calling the LLM. Hit = $0.
- Invalidate automatically when internal sections re-extract or prompt version bumps (same invalidation trigger V2 already defines for retrieval).

**Effort:** medium — one new table + a lookup before Step 8, mirrors the `regul_clause_search_profile` pattern already proposed in V2.

### L3 — Cheap triage model before the expensive judgment model

Not every clause needs the full-strength model. A cheap/fast model (e.g. a smaller Claude/GPT tier) can do a first pass: "does this clause's expanded terms + top section even plausibly overlap?" and route:

- **Clearly no overlap** (low fusion score, low semantic similarity) → mark `not_applicable` / flag for lightweight review, skip the expensive model entirely.
- **Plausible overlap** → escalate to the current judgment model as today.

This is the standard cascade pattern (cheap filter, expensive judge only on the hard cases). Real savings depend on what fraction of clauses in a typical run are genuinely out of scope for a given internal doc — worth measuring on a few real runs before committing effort here.

**Effort:** medium-high — needs a second model call + a confidence threshold to tune, and a way for reviewers to override an auto-skip (don't silently drop clauses without a visible "skipped, low confidence" state).

### L4 — Reranker to shrink the adaptive-select window

Step 6 currently sends 15-56 sections into Step 8's context. A cheap cross-encoder (or even a second embedding pass scored against the fused candidates) between fusion and adaptive-select can push the genuinely relevant sections to the top, letting you lower the ceiling (e.g. cap at 25-30) without losing the sections that actually matter.

- Fewer sections in context = fewer input tokens = cheaper Step 8, on top of whatever L1 caching gives you.
- Also tends to *improve* judgment accuracy (less noise for the model to wade through), so this is a rare lever that helps cost and quality together.

**Effort:** medium — one more free/cheap step in the pipeline, no architecture change.

### L5 — Trim to snippets, not full sections

Right now the adaptive-selected sections presumably go into context in full. If sections are long (multi-paragraph policy text) but only a sentence or two is relevant to a given clause, sending the whole section wastes tokens. A snippet extraction step (even simple: paragraph containing the top-matching sentence + one paragraph of surrounding context) shrinks Step 7's output before it hits Step 8.

**Effort:** medium — depends on how granular the current section chunks already are; if sections are already short (a few sentences), skip this lever, it won't move the needle.

### L6 — Concise structured output schema

Output tokens are often priced the same or higher than input tokens. If Step 8 currently asks for prose explanations, switching to a tight schema (verdict enum, short cited-quote, 1-2 line gap reason, structured element list only when non-compliant) cuts output cost directly and makes downstream parsing (the gap UI) more reliable too.

**Effort:** low — prompt/schema change, likely already partially structured given the existing gap/action-plan UI.

### L7 — Batch API, if real-time isn't required

If analysis runs don't need an immediate answer (user submits a run and checks back), most providers offer an async batch tier at roughly **50% off** standard pricing. Combine with L1 (caching doesn't apply the same way in batch mode for all providers — check current terms) for the largest single-lever discount if latency tolerance allows it.

**Effort:** low-medium — mostly a job-queue change (already have a run/loop structure per V1/V2) plus handling the delayed-result UX.

---

## Suggested rollout order

1. **L6** (output schema) and **L1** (prompt caching / prompt ordering) first — pure prompt changes, no schema/infra work, safe to ship fast, likely 50-65% cost cut on Step 8 combined.
2. **L4** (reranker) next — compounds with L1, also likely improves accuracy.
3. **L2** (judgment cache) once V2's retrieval caching ships, since it reuses the same versioning/invalidation plumbing.
4. **L7** (batch) if/when the product can tolerate non-instant results — biggest single number but a UX decision, not just engineering.
5. **L3** (triage cascade) last — highest effort, most risk of silently misclassifying a clause, needs a measured false-skip rate before trusting it on real users.

Rough combined target: L1+L6+L4 alone could plausibly bring $0.05/clause down to $0.02-0.025/clause without changing what the model is asked to judge — before L2/L7 even apply.

---

## Guardrail before shipping any of this: a golden eval set

None of these levers should ship against real users without a fixed set of clauses (say 30-50, spanning compliant / non-compliant / partial / ambiguous) with known-correct verdicts, run before and after each change. Cost cuts that quietly shift verdicts (especially L3's triage, L4's rerank, L5's snippet trimming) are the failure mode that matters most here — cheaper-but-wrong is worse than the current cost. This is worth setting up once and reusing for every lever above, not built per-lever.

---

## Open questions for you

| Question | Why it matters |
|----------|-----------------|
| Which LLM provider/model is Step 8 on today? | Determines exact caching mechanics (Anthropic prompt caching vs OpenAI cached input) and batch API terms/discount |
| Are section chunks already short, or full policy paragraphs? | Decides whether L5 (snippet trim) is worth doing at all |
| Can analysis runs tolerate a "results in a few minutes" UX, or must it be synchronous? | Gates whether L7 (batch, ~50% off) is usable |
| Do you have (or can you build) a golden set of already-reviewed clause verdicts? | Needed before L3/L4/L5 ship, to prove accuracy didn't regress |

---

*Discussion doc — not yet implemented. Companion to [V1](regul-hybrid-pipeline-detail.md) and [V2](regul-hybrid-pipeline-v2.md); this covers the one step those two leave untouched.*
