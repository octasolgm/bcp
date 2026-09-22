# How Our Actual Pipeline Differs From The Diagram

The diagram ("Regul Hybrid Analysis Run — Data + Cost + Parallel Retrieval") was a
planning picture. Here's where the real, built pipeline matches it and where it doesn't.

## What the diagram gets right

- The overall step order (0 through 9) is correct.
- Step 1 Query expansion really does have the 3 parts shown (synonym + acronym expand +
  acronym collapse), and really doesn't read internal documents. Matches.
- Step 3 (BM25) and Step 4 (embedding) really do run independently of each other, not one
  waiting on the other. Matches.
- **Step 5 Hybrid fusion now matches exactly** — built using the same 0.4 BM25 + 0.6
  embedding weighting the diagram specified.
- Step 9 (Save, loop next clause) matches exactly.

## What's different or wrong

### The diagram doesn't show where the dictionaries come from

Step 1's box says "Synonym + Acronym expand + Acronym collapse. Does NOT read internal" —
that part is accurate for the *expansion* itself. But the diagram never shows the step
that actually *builds* those dictionaries in the first place: a **harvest** step that
runs automatically right after Extract, on every document (regulation or internal),
scanning it for likely new acronyms/synonyms and adding them as pending-review candidates.
Harvest happens once per document at extract time; expansion happens once per clause at
analysis time — the diagram only shows the second half of that picture.

### Step 4 is labeled "PAID ~$0.00003/clause" — this is wrong

The diagram assumes embeddings come from a paid cloud API (like OpenAI's embedding
endpoint). What actually got built is a **local embedding model** (runs on our own
server, no external API, no per-call charge). Step 4 should be labeled **FREE**, same as
Step 0. This was a planning assumption that never made it into the real build — the local
model turned out to be good enough and free, so there was no reason to pay for a cloud
one.

### Steps 5 and 6 — now built, one detail differs from the diagram

- **Step 5 Hybrid fusion** — built with the exact 0.4 BM25 + 0.6 embedding weighting shown.
  Each side is normalized against its own best score for the clause before combining (the
  diagram doesn't specify this detail, but it's necessary — raw BM25 scores and cosine
  similarities aren't on the same scale, so combining them unnormalized would let whichever
  side happens to produce bigger numbers dominate for reasons that have nothing to do with
  relevance).
- **Step 6 Adaptive select** — the diagram's "15-56" range is a rough envelope, not a fixed
  target: the real selection is relevance-driven (same dynamic-cutoff principle as Steps
  3/4), with the 15-56ish range only showing up as a soft floor/ceiling guarding the two
  extremes. This matches the earlier explicit direction that retrieval counts should never
  be a hardcoded number.

Step 7 (build context) now consumes Step 5/6's fused, trimmed list, as originally intended
— it no longer takes Steps 3/4's raw, un-ranked union.

### Step 2 (sub-obligation split) — now built, one addition beyond the diagram

The diagram shows gov clauses getting split into sub-obligations before retrieval, via
lettered/numbered lists ("(a) ... (b) ..."). The real implementation also catches plain
bullet lists ("· LFIs should ..."), which turned out to be at least as common in the
actual regulation documents (confirmed against a real bundled clause in TFS Guidelines
v12). Both list styles are treated as equally reliable signals.

### Step 8 is built but intentionally turned off

The diagram shows Step 8 as the main paid cost (~$0.05/clause). That part of the diagram
is directionally correct — it is the one genuinely paid step, and it is built to use the
same LLM/prompt as before, just with retrieval-based context instead of full markdown.
The difference: **the actual LLM call is currently commented out** so it consumes no
credit while the rest of the pipeline is being built and tested. The diagram doesn't show
this "paused" state because that's a build-sequencing decision, not part of the original
plan.

### The "$4.50 for 80 clauses" total no longer applies

That number in the diagram was based on Step 4 being paid and Step 8 running for real.
With Step 4 actually free and Step 8 paused, the real cost of running the current pipeline
end-to-end (Parse aside) is effectively **$0** until Step 8 is turned back on — at which
point the cost is just Step 8's LLM calls, not Step 8 + Step 4 combined.

## Corrected cost picture (current real pipeline)

| Step | Diagram said | Actually is |
|---|---|---|
| Parse (Azure DI) | not shown separately | Paid (per page) |
| Step 0 Load indexes | FREE | FREE — correct |
| Step 1 Query expansion | FREE | FREE — correct |
| Step 2 Sub-obligation split | FREE | FREE — correct, done |
| Step 3 BM25 retrieve | FREE | FREE — correct |
| Step 4 Embedding retrieve | PAID ~$0.00003/clause | **FREE — local model, diagram is wrong** |
| Step 5 Hybrid fusion | FREE | FREE — correct, done (exact 0.4/0.6 weighting) |
| Step 6 Adaptive select | FREE | FREE — correct, done (dynamic, not a fixed 15-56) |
| Step 7 Build context | FREE | FREE — correct, now off the fused list as intended |
| Step 8 LLM judgment | PAID ~$0.05/clause, MAIN COST | Built, but **LLM call paused — $0 right now** |
| Step 9 Save | FREE | FREE — correct |

## Bottom line

Every step's shape now matches the diagram (order, what each step does, the 0.4/0.6 fusion
weighting) except two things: Step 4's cost label is wrong (it's free, not paid — a local
model was used instead of a cloud API), and Step 8's LLM call is intentionally paused for
now rather than running live. Steps 1 through 7 are fully built and match the intended
design; only Step 8's actual LLM call remains switched off.
