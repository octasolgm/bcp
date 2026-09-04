# Why Regul analysis needs the hybrid pipeline

**What this doc is:** the problem with the shipped pipeline, real numbers at real scale, how much the proposed hybrid pipeline saves, and a step-by-step guide to the pipeline itself.

**Companion docs:** [V1 step guide](regul-hybrid-pipeline-detail.md) · [V2 precompute](regul-hybrid-pipeline-v2.md) · [LLM cost reduction levers](regul-hybrid-pipeline-cost-reduction-plan.md) · [Overview](REGUL-HYBRID-RETRIEVAL-WORKFLOW.md)

---

## 1. The problem

`http://localhost:3002/nd/analyse-regul-full` runs the **V4** engine (`regul_pipeline_full` in `AnalysisWorkflowEngine.cs`). Its rule is simple and that's exactly the problem:

> Always send the **entire parsed internal document set**, as full markdown, to the LLM — for **every single regulatory point**, no matter how big the internal library is or how many points you're checking.

This isn't a bug, it's the design: V4 exists specifically to avoid the retrieval-miss problem documented in [REGUL-FORWARD-MATCHING-FIX-PLAN.md](../REGUL-FORWARD-MATCHING-FIX-PLAN.md), where keyword-only retrieval missed a clause because the manual said "internal audit" and the regulation said "independent audit". Sending everything guarantees nothing gets missed. But it means cost and time scale directly with **(points to check) x (size of internal library)**, and neither of those numbers is small in a real run.

### What a real run actually looks like

| Metric | Value | Source |
|---|---|---|
| Points in a real regulation | **592** | Confirmed against `bcp-api/SeedData/regulation-points-extract.json` — 592 `pointNumber` entries (CBUAE AML/CFT Guidelines) |
| Internal library size (back-solved) | **~142,000 tokens/call ≈ 200-280 pages** | Reverse-engineered from an observed run: 85M-190M total input tokens / 592 calls |
| API calls | **592**, sequential, forward only | The processor loop calls the LLM once per point, one after another |
| Runtime | **~9-15 hours** | ~1-1.5 min/call x 592, dominated by re-processing a large context (and Opus extended thinking) every call |
| Cost (Opus 5) | **~$120-160** likely, up to ~$265 worst case | See breakdown below |

A "65-page single doc, 80-point sample run" — the scale used in earlier draft estimates — is not what real users hit. Real users check the whole regulation against the whole internal library. That's 7x more points and roughly 3-4x more internal content than the small example, and cost/time don't add, they **multiply**.

### Where the ~$120-160 actually goes

The processor does apply Anthropic prompt caching to this route already (`cacheContext = policyBundle.UsesFullMarkdown` in `NdRegulAnalysisProcessor.cs`, `cache_control: ephemeral` in `ProviderLlmClients.cs`) — so the big internal-library block is written to cache once and read at a 90% discount on the other 591 calls. Caching is not the problem. Reconstructing the real run with Opus 5 pricing ($5/$25 per 1M input/output):

| Line item | Cost |
|---|---|
| 1st call — cache write (1.25x, ~142K tok) | ~$0.89 |
| 591 calls — cache read (0.1x, ~142K tok) + uncached system prompt + query + **output/thinking tokens** | ~$148 |
| **Total** | **~$149** |

**The output/thinking tokens are the real driver, not the input context.** At ~6-7K output tokens/call x $25/1M x 592 calls, output alone is roughly **$100 of the $149** — bigger than the entire (cache-discounted) input-context line. Any fix that only shrinks the context sent to the model is fixing the smaller half of the bill.

---

## 2. Why the hybrid pipeline is needed

Two separate problems, two separate fixes:

### Problem A — cost scales with library size, hybrid caps it

V4's input cost grows **linearly with the internal library**. Add a second manual, cost per run goes up proportionally, forever. The hybrid pipeline (BM25 + embedding retrieval, fused, then an adaptive-selected 15-56 sections) sends a **bounded** amount of context per point regardless of how big the library gets — a 1,000-page library costs the same per-point as a 100-page one, because retrieval always narrows it down to the same-sized relevant slice.

| | V4 (full markdown) | Hybrid |
|---|---|---|
| Context per point | Whole library (grows without bound) | 15-56 sections (bounded) |
| Cost as library grows | Linear, unbounded | Flat |
| Miss risk (synonyms, reworded clauses) | None — sees everything | Present, but BM25+embedding (not keyword-only) closes most of the gap V3 had |

### Problem B — output/thinking tokens dominate, and neither pipeline fixes that by itself

This is the bigger lever right now. Retrieval architecture (V4 vs hybrid) only touches the input side of the bill. At real scale, recalculating hybrid against the **same** 592 points and the **same** internal library:

| | V4 + caching (Opus 5) | Hybrid, uncached (Opus 5) |
|---|---|---|
| Input | ~$25 (591 cached reads + 1 write) | ~$25 (8.4K tok/call, no caching benefit — each clause retrieves a different section set) |
| Output/thinking | ~$100 | ~$100 (same model, same output profile) |
| **Total** | **~$149** | **~$125** |

Hybrid wins, but only by **~16%** at this scale — not the 4x difference it showed on the small 65-page example. The output-token cost is identical in both because it doesn't depend on how the context was assembled.

### The lever that actually moves the number: model choice

Swapping Opus 5 ($5/$25 per 1M) for **Sonnet 5** ($2/$10 per 1M) on the same 592-point run:

| Pipeline | Opus 5 | Sonnet 5 |
|---|---|---|
| V4 (cached) | ~$149 | **~$60** |
| Hybrid | ~$125 | **~$50** |

That's a ~60% cut either way — bigger than the pipeline swap. Worth confirming Sonnet's judgment quality is good enough for this structured classification task (compliant / partial / non-compliant + cited evidence) before assuming Opus's extra reasoning is required.

### Bottom line: three independent levers, not one

| Lever | What it fixes | Estimated saving |
|---|---|---|
| **Hybrid retrieval** (this doc's main topic) | Cost that grows unbounded as the internal library grows; also fixes the V3 synonym-miss bug without going back to "send everything" | ~16% at current library size, **more as the library grows** — this is the one that matters for scaling to bigger libraries, not for today's cost alone |
| **Model choice (Sonnet vs Opus)** | Output/thinking token cost, which dominates the bill today | ~60% |
| **Prompt caching on hybrid too** (not yet implemented for retrieval mode) | Input cost on top of whatever hybrid already saves | Adds back some of hybrid's edge once implemented — see [cost reduction plan, L1](regul-hybrid-pipeline-cost-reduction-plan.md#l1--prompt-caching-on-the-internal-doc-context-biggest-win-low-effort) |

Doing all three together is what gets from ~$149/run down toward the ~$30-40/run range. Doing only the retrieval swap and stopping there leaves most of the money on the table, because it doesn't touch the output-token line that's currently the biggest single cost.

---

## 3. Guide: how the hybrid pipeline works

### The shape of it

```
For each selected regulatory clause:
  1. Query expansion      FREE   — synonym + acronym lookup (gov clause only, no internal read)
  2. Sub-obligation split  FREE   — break compound clauses into atomic requirements
  3. BM25 retrieve         FREE   ⎫ parallel — score ALL internal sections, top ~100
  4. Embedding retrieve   ~$0.00003 ⎭ parallel — independent of BM25, top ~100
  5. Hybrid fusion         FREE   — union both lists, score = 0.4*BM25 + 0.6*embedding
  6. Adaptive select       FREE   — pick 15-56 sections by threshold + token budget
  7. Build context         FREE
  8. LLM judgment          paid   — the only step that costs real money
  9. Save                  FREE   — loop to next clause
```

Steps 0-7 are free or near-free (embeddings are a fraction of a cent). Step 8 is the entire bill in both this pipeline and V4 — the difference between them is only **how much gets sent into that one step**.

### Why hybrid retrieval specifically (not keyword-only, not full-markdown)

- **Keyword-only (the old V3 mode)** misses synonyms — "independent audit" in the regulation vs "internal audit" in the manual is a textual mismatch a plain keyword scorer won't bridge. That's the exact bug in [REGUL-FORWARD-MATCHING-FIX-PLAN.md](../REGUL-FORWARD-MATCHING-FIX-PLAN.md).
- **Full markdown (V4, today's `analyse-regul-full`)** never misses anything, but costs scale with library size — see Section 1-2 above.
- **BM25 + embedding, fused** gets both: BM25 catches exact terms and defined phrases (cheap, precise), embeddings catch paraphrased/reworded meaning (catches the "independent audit" vs "internal audit" case), and fusing them into one ranked list means neither retrieval method's blind spot is fatal on its own.

### Two-phase design (precompute once, run many times fast)

Rebuilding retrieval indexes on every run is wasted work if the same regulation/internal doc gets analyzed repeatedly. Split it:

**Phase 1 — Ready Pipeline (once per document, re-run only if the doc changes)**

| Side | Precompute | Stored as |
|---|---|---|
| Regulation | Query expansion, sub-obligation split, clause embedding — for **every** clause in the doc | `regul_clause_search_profile` |
| Internal | BM25 index, section embeddings — for **every** section in the manual | `regul_internal_retrieval_index` / `regul_internal_section_embeddings` |

One-time cost: ~$0.01-0.02 per document (embeddings only — everything else is free). Full detail: [regul-hybrid-pipeline-v2.md](regul-hybrid-pipeline-v2.md).

**Phase 2 — Analysis run (fast, per selection)**

Load the precomputed profiles/index for just the selected clauses and selected internal doc(s), then run steps 3-9 above per clause. BM25 search against a prebuilt index is millisecond-fast; only the LLM step (8) has real latency and cost.

### Rollout order

1. **Ship hybrid retrieval** (steps 1-7 above) behind the existing `regul_pipeline` engine flag, validated against a fixed set of clauses with known-correct verdicts (a golden set — see the cost plan's guardrail section) so retrieval changes don't silently change verdicts.
2. **Add Ready Pipeline precompute** (V2) once hybrid retrieval is stable, so repeat runs on the same docs get fast and free on the retrieval side.
3. **Extend prompt caching to hybrid** the same way V4 already has it, once the retrieved-section set is stable enough per clause to benefit (see [cost plan L1](regul-hybrid-pipeline-cost-reduction-plan.md)).
4. **Re-evaluate model choice** (Sonnet vs Opus) against the golden set — this is the single biggest lever found above and is independent of everything else on this list, so it can ship first if you want the fastest win.

### What to decide before building

| Question | Why it matters |
|---|---|
| Is Opus 5's extra reasoning actually needed, or does Sonnet 5 score the golden set the same? | Bigger cost lever than the pipeline swap — worth answering before investing in retrieval work |
| Real per-document library size (page count, file count) for a typical customer | The whole case for hybrid is that cost stays flat as this grows — need the real range to size the win |
| Can `analyse-regul-full` results tolerate an async/batch turnaround? | Batch API is ~50% off and this workflow already runs for hours, unattended — likely a free win with no UX cost |
| Do we have a golden set of already-reviewed clause verdicts? | Required before trusting any retrieval or model change not to quietly change judgments |

---

*Ties together the numbers from V1/V2 (architecture) and the cost-reduction plan (levers) with the real 592-point production scale. See those docs for implementation-level detail on each piece.*
