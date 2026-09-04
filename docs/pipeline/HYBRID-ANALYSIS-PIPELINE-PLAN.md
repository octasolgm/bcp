# Regul Hybrid Analysis Pipeline - Implementation Plan

Not implemented yet. This is a step-by-step plan only, written against the
"Regul Hybrid Analysis Run" diagram - for scheduling and review, not for
building right now.

## What this is

For one analysis run: every extracted regulation clause (left/gov side) is
compared, one at a time, against the full set of extracted internal-policy
sections (right/internal side), to judge whether the internal policy
covers, satisfies, or gaps that clause. Nine steps per clause, most of them
free local computation, with two steps that call an external model and
carry real cost. The diagram's own example: 80 clauses, about $4.50 total.

## Where the input "chunks" come from - already built

This whole pipeline consumes the exact output of the local Parse + Extract
work already shipped (see `TASKS.md` / `LIBRARY-REFERENCE.md` in this
folder). Both the gov clauses on the left and the internal sections on the
right are `LocalSection` records (`ClauseNo`, `ClauseText`, `SourcePage`)
produced by `LocalSectionSplitter.Split()` - the regex-based, no-AI
extraction step. No new extraction work is needed to feed this pipeline;
Step 0 below just loads what Extract already produced for the internal
document(s) selected for the run.

## Indexing happens once, automatically, chained after Extract - not per analysis run

This is a deliberate design decision, not an implementation detail: an
internal document's section embeddings must be computed **once**, right
after Extract finishes for that document, as an automatic background
step - not triggered by, or recomputed inside, an analysis run. The chain
becomes:

**Upload -> Parse -> Extract -> Index** - all automatic, all chained, the
same pattern Parse -> Extract already follows today. As soon as Extract
produces sections for an internal document, a background job computes
each section's embedding and stores it (the pgvector column from Step 0
below). By the time anyone starts a real analysis run against that
document, indexing has normally already finished, often well before the
run even starts.

This needs its own status, tracked the same way `Status` (parse) and
`ExtractStatus` (extract) already are on `nd_local_document_extractions`:
an **`IndexStatus`** field - `pending -> processing -> indexed -> failed`.

**An analysis run must never fail or block hard on this.** The only time
indexing genuinely isn't ready yet is a race: a document that finished
extracting moments ago, before the background indexing job has caught up.
When an analysis run needs a document whose `IndexStatus` isn't
`indexed`:

- The run does **not** fail. It either triggers indexing right there as a
  fallback (if nothing is already in flight), or waits briefly for the
  background job that's already running.
- The user does **not** see internal jargon like "indexing" or
  "embedding" - reuse the exact progress pattern already built for Parse
  and Extract (`extractionProgressLabel` / `extractionProgressPct` on
  `RegulationDocument`, already wired into the UI). The new-analysis
  progress screen gets one more labeled stage - something like "Preparing
  documents..." - that simply covers this wait, the same way "Parsing..."
  and "Extracting..." already do for those steps.

## Step-by-step breakdown

### Step 0 - Load indexes (internal side only) - FREE

**What gets indexed is the split sections, not the raw parsed markdown.**
The full markdown from Parse is only an intermediate artifact - nobody
searches it directly. Extract already turned it into `LocalSection`
records (`ClauseNo`, `ClauseText`, `SourcePage`); each one of those becomes
a single row/document in both indexes below. The markdown's job is done
once Extract has run - it never enters retrieval.

Two indexes, with two different lifecycles:

- **Keyword/BM25 index** - rebuilt fresh at the start of every analysis
  run, in memory, from that run's internal sections. Cheap enough (a few
  hundred sections, milliseconds) that there's no need to persist it
  between runs. Recommended: **Lucene.NET** (Apache 2.0, free, mature BM25
  implementation) rather than writing BM25 from scratch.

- **Embedding vectors** - computed **once, when Extract runs** for the
  internal document (not once per analysis run, and not once per gov
  clause). Store each section's embedding vector alongside its
  `SectionsJson` at that point. Step 0 of every future analysis run then
  just **loads** those already-computed vectors - it never recomputes
  them. This matters because even a local embedding model costs real
  compute time to run, and a paid API would cost real money, to redo on
  every single analysis run against the same unchanged document.

  **Storage: pgvector**, a free, open-source Postgres extension - Supabase
  enables it by default (it's one of their advertised features), and it
  works identically on a self-hosted Postgres for the on-prem case, since
  it's just a Postgres extension either way (`CREATE EXTENSION vector;`).
  No new database or paid vector-store service (Pinecone, Weaviate, etc.)
  needed.

  **How the search itself works:** an embedding model turns text into a
  fixed-length list of numbers (e.g. 384 of them for a small model) - text
  with similar meaning ends up with numbers that are numerically close
  together. pgvector adds a `vector(384)`-style column to hold that list
  per row, plus distance operators to compare vectors:  `<=>` (cosine
  distance), `<->` (Euclidean), `<#>` (inner product). To search, embed
  the gov clause the same way, then ask Postgres for the closest stored
  rows - literally `ORDER BY embedding <=> query_embedding LIMIT 100`.

  **No special ANN index needed at this scale.** Without an index,
  Postgres compares the query against every stored row one by one - for
  the few hundred sections one internal document has, that's already fast
  enough on its own. pgvector's approximate-nearest-neighbor indexes
  (IVFFlat, HNSW) trade a little accuracy for a lot of speed, but only
  start mattering once search spans thousands-to-millions of rows - e.g.
  a future version that searches across an entire document library at
  once, not the single active document this pipeline targets today. Not
  worth building until that's actually the shape of the problem.

### Step 1 - Query expansion (gov side only) - FREE

Expands the gov clause's wording with synonyms and acronym
expansions/collapses before searching (e.g. "TFS" <-> "Targeted Financial
Sanctions", "AML" <-> "Anti-Money Laundering") so the keyword search in
Step 3 isn't defeated by wording differences alone. Never reads the
internal side (by design, per the diagram - keeps this step's cost and
blast radius scoped to the one gov clause).

**No intelligence involved - a person, or the document itself, already
wrote the definition down; this step just looks it up.** At runtime it is
a flat dictionary lookup against a table like:

```json
{
  "TFS": ["Targeted Financial Sanctions"],
  "AML": ["Anti-Money Laundering"],
  "CBUAE": ["Central Bank of the UAE"],
  "KYC": ["Know Your Customer"]
}
```

Scan the gov clause's words against this table; if `TFS` appears, add
`Targeted Financial Sanctions` as an extra search term. A string match
over a few hundred entries - microseconds, no network call, no AI. The
"knowing" happened before this step ever runs, not during it.

**Built by the system, not by a human remembering to add entries - that's
the whole point.** A human-maintained list doesn't scale to the number of
terms real regulatory documents use, and is exactly as failure-prone as
"a person might forget to add one" - so the primary source below is fully
automatic and never depends on anyone remembering anything:

1. **Auto-harvested from the documents themselves (primary source, fully
   automatic).** Regulatory documents almost always spell out a term the
   first time they use it - `"Targeted Financial Sanctions (TFS)"` -
   because that's how legal and compliance writing works, not because of
   anything this system does. A regex checks every sentence for that
   specific *shape*: a Capitalized Phrase immediately followed by
   `(SHORT-CAPS)` in parentheses - it does not read for meaning, it only
   checks character patterns (capital letter starts each word, then `(`,
   then 2-6 capital letters, then `)`). When it matches, the program does
   dumb copy-paste - and captures **both directions from one match**,
   so neither "I only know the short form" nor "I only know the full
   form" is a gap:

   > Raw text: *"...officers must screen customers under Targeted
   > Financial Sanctions (TFS) and Anti-Money Laundering (AML)
   > obligations."*
   >
   > Mechanically extracted (both directions, from one match each):
   > `TFS -> Targeted Financial Sanctions` and
   > `Targeted Financial Sanctions -> TFS`;
   > `AML -> Anti-Money Laundering` and
   > `Anti-Money Laundering -> AML`

   This is the exact same technique as `LocalSectionSplitter` finding
   `"6.2 Independent Audit"` as a clause heading - checking whether text
   *looks like* the target shape, not understanding it. It runs
   automatically on **every** document that goes through Extract, so the
   table grows on its own as more documents get processed, with zero risk
   of a human forgetting an entry - there is no human step in this path
   at all.

2. **A manually curated seed list (fallback only)**, for the rare term
   that's used somewhere but genuinely never spelled out with its full
   form in *any* document processed so far. Written once, by a person,
   into a JSON/YAML file checked into the repo - ordinary config-file
   work for filling small, specific gaps, not the primary mechanism and
   not something that needs to stay comprehensive on its own, since
   source 1 keeps closing gaps automatically as more documents arrive.
   (An AI could help draft this starter list once during setup, a
   one-time cost, categorically different from calling AI per query.)

**What this dictionary does *not* attempt to solve: true synonyms with no
shared acronym.** "Sanctions screening" and "TFS screening" aren't
connected by any `(ABBR)` marker a regex could ever find - no amount of
automation here closes that gap, because it isn't a text-shape problem.
That's handled by Step 4 (embeddings) instead, not by making this
dictionary bigger - see the note there.

### Step 2 - Sub-obligation split (gov side only) - FREE

A single gov clause sometimes bundles multiple obligations ("the
institution must do X and must also do Y"). This step splits such a
clause into its sub-obligations before retrieval, so each one can be
matched against internal policy independently. Recommended approach:
rule-based splitting on conjunctions ("and", ";", numbered sub-items like
"(a)"/"(b)") - the same style of deterministic, explainable logic as
today's `LocalSectionSplitter`, not an AI call. This is a genuine design
judgment call, similar in spirit to today's extraction regex: worth
validating against a handful of real clauses before trusting it broadly,
and simple enough to start as "one clause = one obligation" (skip
splitting entirely) for a first version if sub-obligations turn out to be
rare in practice.

### Step 3 - BM25 retrieve - FREE

Classic keyword/lexical search: the expanded gov query (from Step 1)
against the internal section index (from Step 0), returning roughly the
top 100 keyword-matched sections. Runs via Lucene.NET, purely local, no
AI, no cost.

### Step 4 - Embedding retrieve - PAID (small)

Semantic search: embeds the gov clause's meaning and compares it against
the pre-computed embeddings of every internal section (from Step 0),
returning roughly the top 100 semantically-similar sections. Runs
independently of Step 3, in parallel with it - same input clause, two
different retrieval strategies, combined afterward in Step 5.

**This is the step that catches true synonyms and paraphrases - not
Step 1.** Step 1's dictionary only solves acronym expansion (`TFS` <->
`Targeted Financial Sanctions`), because that has a detectable textual
marker to regex against. Genuinely different wording for the same concept
- "customer due diligence" vs "know your customer obligations," no
acronym involved, no shared letters at all - has no textual marker a
regex could ever detect. That is precisely what embeddings are for: the
embedding model converts both phrases into number-vectors that land close
together because they *mean* similar things, with no maintained synonym
table required at all. This is why the pipeline runs BM25 (Step 3, backed
by Step 1's exact-term expansion) and embeddings (this step) in parallel
rather than relying on either alone - each one catches what the other
structurally cannot: BM25 + dictionary for exact/near-exact wording,
embeddings for meaning-based matches with no shared wording whatsoever.

This step needs an embedding model. **Anthropic does not offer its own
embeddings API** - their documented guidance points to **Voyage AI** as
the recommended embeddings partner for use alongside Claude. Two real
options for this specific step, worth deciding deliberately given this
platform's privacy-first design so far:

- **Local, self-hosted, free** (recommended, consistent with everything
  else built today) - a small open-source embedding model (e.g.
  `all-MiniLM-L6-v2` or `bge-small-en`) run in-process via ONNX Runtime
  (`Microsoft.ML.OnnxRuntime`, free, MIT-licensed .NET bindings). Zero
  per-clause cost, zero data leaves the server - the same reasoning that
  drove replacing Landing AI with local OCR earlier this session applies
  identically here: the gov clause text and internal section text are
  exactly the kind of content a bank does not want leaving its network,
  even to an embeddings-only API.
- **Voyage AI (cloud, paid)** - if retrieval quality testing shows the
  local model underperforms and the client's privacy requirements allow a
  narrowly-scoped, embeddings-only external call. Check
  `voyageai.com` directly for current pricing - not covered in the
  bundled Claude API reference used for this doc's other cost estimates,
  and worth confirming at implementation time regardless, since pricing
  changes.

Either way this is a small line item - the diagram's own estimate is
about $0.00003/clause, and the local option makes it $0 outright.

### Step 5 - Hybrid fusion - FREE

Combines the two retrieval lists (BM25 top ~100, embedding top ~100) into
one ranked list, scored as a weighted union: 0.4 x BM25 score + 0.6 x
embedding score per the diagram. Pure application code - sorting and
arithmetic, no AI, no cost.

### Step 6 - Adaptive select (15-56 sections) - FREE

Picks how many of the fused, ranked internal sections to actually hand to
the LLM in Step 8 - somewhere between 15 and 56, presumably based on a
score-drop-off or confidence threshold rather than a fixed number (so a
clause with a few obviously relevant sections doesn't drag in 56
marginal ones, and a clause with genuinely broad internal coverage isn't
starved at 15). Pure local logic - no AI, no cost. The exact threshold
rule is a tuning decision to make once real score distributions are
available from a pilot run.

### Step 7 - Build context - FREE

Assembles the selected internal excerpts (from Step 6) plus the gov
clause (plus any sub-obligation split from Step 2) into the prompt context
that Step 8 will actually send. String/prompt assembly - no AI, no cost.
This is also where prompt-caching structure gets decided (see the cost
note under Step 8).

### Step 8 - LLM judgment - PAID (the main cost)

The one step that genuinely requires reasoning, not retrieval: given the
gov clause and its matched internal excerpts, judge whether internal
policy satisfies, partially covers, contradicts, or has a gap against
that clause - and explain why. This is not something local regex/BM25 can
do credibly for a compliance tool; it needs a real LLM. Every dollar in
this pipeline is effectively spent here.

**Model choice and cost, using current Claude pricing:**

| Model | Input $/1M tok | Output $/1M tok |
|---|---|---|
| Claude Haiku 4.5 | $1.00 | $5.00 |
| Claude Sonnet 5 | $2.00 | $10.00 |
| Claude Opus 5 | $5.00 | $25.00 |

Estimated cost per clause depends heavily on how much context Step 6
selects (15 vs 56 sections). Working through both ends of that range,
assuming roughly 400 tokens per selected internal excerpt plus the gov
clause and judgment rubric, and roughly 500 output tokens for a
structured judgment (verdict + explanation + citations):

| Scenario | Est. input tokens | Haiku 4.5 cost/clause | Sonnet 5 cost/clause |
|---|---|---|---|
| Low end (~15 sections) | ~7,000 | ~$0.010 | ~$0.019 |
| High end (~56 sections) | ~24,000 | ~$0.027 | ~$0.053 |

The diagram's own figure (~$0.05/clause) lines up closely with **Sonnet 5
at the high end of the adaptive-select range** - which is a reasonable
planning assumption, but this needs real measurement once Step 6's actual
selection sizes are known from a pilot. **Haiku 4.5 at the same context
size comes in at roughly half that cost** and is worth testing first for
judgment quality before defaulting to Sonnet - a compliance-analysis
judgment task is exactly the kind of well-scoped, rubric-driven task
Haiku often handles well, but it should be validated against real gov
clauses before committing to it, not assumed.

**Prompt caching is a real lever here.** The judgment rubric/instructions
and output-format spec are identical across all ~80 clause calls in one
run - that's a strong, stable prefix to cache (`cache_control` on the
system prompt / instructions block). Cache writes cost 1.25x the base
input rate (5-minute TTL) or 2x (1-hour TTL); cache reads cost about
0.1x. Since all 80 calls in a run happen close together, the 5-minute TTL
is the right choice - reads on calls 2 through 80 cost roughly a tenth of
what that shared prefix would otherwise cost repeated 80 times. Worth
structuring the Step 7 prompt so the stable rubric/instructions come
first and the per-clause, per-run content (gov clause + selected
excerpts) comes after the cache breakpoint.

**Privacy note:** this is the one step in the whole pipeline that
necessarily sends real document content (the selected internal excerpts)
to an external API, if a cloud model is used. It's a much smaller surface
area than sending whole raw documents (as Landing AI did before this
session's local-OCR work) - only the already-extracted, already-selected
excerpts for one clause at a time leave the network - but it is still an
external call. For a fully on-prem deployment (see
`docs/roadmap/ON-PREM-DEPLOYMENT-ROADMAP.md`), this step is the one place
that conversation would need to happen with the client: either accept
this narrowly-scoped external call for judgment quality, or run a
self-hosted open-weight model on their own infrastructure instead (lower
judgment quality is the realistic tradeoff there, and worth testing before
committing to it as the on-prem answer).

### Step 9 - Save, loop next clause - FREE

Persists the judgment result and moves to the next gov clause, repeating
Steps 1 through 9 (Step 0's indexes are reused, not rebuilt). Pure
application code and a database write - no AI, no cost.

## Cost summary

| Step | Cost | Technology |
|---|---|---|
| 0 - Load indexes | Free | Lucene.NET (BM25) + pgvector (embeddings), built once per run |
| 1 - Query expansion | Free | Local synonym/acronym dictionary |
| 2 - Sub-obligation split | Free | Rule-based text splitting |
| 3 - BM25 retrieve | Free | Lucene.NET |
| 4 - Embedding retrieve | Free (local model) or small paid (Voyage AI) | Local ONNX embedding model, recommended |
| 5 - Hybrid fusion | Free | Weighted score combination |
| 6 - Adaptive select | Free | Threshold/ranking logic |
| 7 - Build context | Free | Prompt assembly |
| 8 - LLM judgment | **Main cost** - ~$0.01-$0.05/clause depending on model and context size | Claude Haiku 4.5 or Sonnet 5, prompt-cached rubric |
| 9 - Save | Free | Database write |

For an 80-clause run: **roughly $0.80-$2.20 with Haiku 4.5, or
$1.50-$4.25 with Sonnet 5**, depending on how much context Step 6 selects
per clause - both ranges bracket the diagram's original $4.50 estimate,
with Haiku offering a real cost reduction worth testing against judgment
quality before committing to a model.

## Implementation task list (not started)

1. Add a `vector` column (pgvector) to the internal-sections table, plus
   an `IndexStatus` field (`pending`/`processing`/`indexed`/`failed`) on
   `nd_local_document_extractions`, matching the existing `Status` /
   `ExtractStatus` pattern.
2. Chain an automatic background indexing step after Extract succeeds
   (Upload -> Parse -> Extract -> Index), so embeddings are normally
   already computed long before any analysis run needs them. Add
   Lucene.NET for the per-run, in-memory BM25 index (no persistence
   needed for BM25 - cheap to rebuild each run).
3. Handle the race case in the analysis-run flow: if a selected internal
   document's `IndexStatus` isn't `indexed` yet when a run starts, trigger
   indexing as a fallback or wait for the in-flight job - never fail the
   run - and surface it as a labeled progress stage ("Preparing
   documents...") reusing the existing `extractionProgressLabel` /
   `extractionProgressPct` pattern, not raw internal status names.
4. Decide and build Step 4's embedding source - local ONNX model
   (recommended) vs Voyage AI - and get real cost/quality numbers before
   committing. This choice determines what gets stored in the pgvector
   column added in task 1.
5. Build the domain synonym/acronym dictionary for Step 1: a regex that
   detects the `Capitalized Phrase (ABBR)` shape anywhere in a document's
   parsed text (run automatically whenever Extract runs, growing the
   table on its own across documents), plus a manually curated seed file
   for common cross-document terms (AML, KYC, CBUAE) that a given
   document might not locally define.
6. Build Step 2's sub-obligation splitting rules, or defer it (start
   treating one clause as one obligation) until real documents show it's
   needed.
7. Implement Step 5's fusion scoring and Step 6's adaptive-select
   threshold - needs a pilot run's real score distributions to tune
   sensibly, not guessed up front.
8. Design Step 7's prompt structure with the cache breakpoint placed
   correctly (stable rubric first, per-clause content after).
9. Write and test Step 8's judgment prompt/rubric against a set of
   already-known-good clause/internal-section pairs, starting with Claude
   Haiku 4.5, only moving to Sonnet 5 if judgment quality testing shows
   Haiku falls short.
10. Wire Steps 0-9 into one orchestrated per-clause loop, run against a
    real analysis, and measure actual cost/latency against the estimates
    in this doc before rolling out broadly.
