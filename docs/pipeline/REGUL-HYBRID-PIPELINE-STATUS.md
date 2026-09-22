# Regul Hybrid Pipeline — Full Step-by-Step Status

Plain-language walkthrough of the whole pipeline, from parsing a document all the way to
saving a finding. For each step: what it does, and whether it's actually built or still
pending.

Applies to the V5 analysis page (`/nd/analyse-regul-full-v2`) only.

---

## Stage A — Document prep (happens once per document, before any analysis run)

### 1. Parse — DONE
**Doc side: BOTH** (regulation docs and internal docs each go through this — that's why a
regulation doc like "TFS Guidelines (v12)" also shows a parse/extract status and a point
count, same as an internal document).
The raw file (PDF/DOCX) gets sent to Azure Document Intelligence, which reads it and
returns structured text. This is the only paid step in document prep (Azure DI charges
per page).

### 2. Extract (section split) — DONE
**Doc side: BOTH.**
The parsed text is broken into sections/clauses so each piece can be indexed and matched
individually, instead of treating the whole document as one blob. Free (local logic, no
AI call).

### 3. Index (embed each section) — DONE
**Doc side: INTERNAL ONLY.**
Every extracted section of an **internal** document gets converted into a vector (a list
of numbers representing its meaning) using a local embedding model that runs on our own
server. Free — no cloud API, no per-call cost. This only has to happen once per document,
not once per analysis run.

Regulation document sections are *not* pre-indexed this way — the gov clause's own text
is embedded live, on the spot, at analysis time (see Step 4). There's nothing to
pre-compute on the gov side because the same clause is only ever queried once per run.

### 4. Dictionary harvest (auto-suggest) — DONE
**Doc side: BOTH** (runs on whichever document was just extracted — a regulation doc or
an internal doc, either one can introduce a new acronym or synonym).

This is a separate thing from "query expansion" below, and it's easy to mix the two up
because both involve the same dictionaries — the difference is *when* each happens and
*what* each does:

- **Harvest = building the dictionary.** Runs automatically right after Extract, once per
  document. Scans the document's text for likely new acronyms (text-shape pattern, must
  appear 2+ times to filter out noise) and likely new synonym pairs (embedding
  similarity between phrases). Every harvested candidate is inserted as **inactive /
  pending review** — it is never used in an actual analysis run until an admin approves
  it on the dictionary admin page.
- **Expansion = using the dictionary.** Happens later, during an analysis run, one gov
  clause at a time (Step 1 below). It only *reads* the already-approved dictionary
  entries to rewrite the query — it never adds anything to the dictionary itself.

So: dictionaries grow automatically at extract time (Stage A, this step), and get applied
at analysis time (Stage B, Step 1) — growing and using are two different steps, not one.

### 5. Regulation points (the gov clause list itself) — DONE
**Doc side: REGULATION ONLY.**

Separate from all of the above: where the actual list of gov clauses shown in "REG.
POINTS" and iterated over during a run comes from. V5 now builds this list directly from
the same structural-chunking sections Extract already produced for the regulation
document (clause number + clause text, detected locally by regex over numbering
conventions like "9.4.1" or "Article 12" — no AI call). Older pages (V3/V4) still get
their gov points from a separate, older mechanism (Landing AI-based extraction) — V5 no
longer depends on that at all, so a regulation document that's only ever been through the
new Azure DI pipeline (never through Landing AI) still gets a full, usable clause list on
V5.

---

## Stage B — Analysis run (happens every time a gov clause is checked against a document)

### Step 1. Query expansion — DONE
**Doc side: REGULATION ONLY.** Only touches the selected gov clause's text — never reads
any internal document.

Before searching, the gov clause's wording gets expanded so matching isn't limited to
exact word choice, using the dictionaries that Stage A step 4 already built. Three things
happen here:
- **Acronym expand** — short form → full form (e.g. "KYC" → also search "Know Your
  Customer").
- **Acronym collapse** — full form → short form (the reverse direction).
- **Synonym expand** — different wording, same meaning (e.g. "controller" ↔
  "administrator").

This step only *looks up* existing dictionary entries — it does not create new ones (that
already happened back in Stage A, per document, at extract time). Free — dictionary
lookup, no AI call.

### Step 2. Sub-obligation split — DONE
**Doc side: REGULATION ONLY** — only ever splits the gov clause; internal documents aren't
touched by this step.

A single gov clause sometimes bundles multiple distinct obligations (e.g. "LFIs must do X
and must report Y within Z days" is really two separate requirements). Free, local,
regex-only — no AI call. Two signals are checked, most reliable first:
- **Lettered/numbered/bulleted sub-items** — "(a) ... (b) ... (c) ..." style lists, and
  plain bullet lists ("· LFIs should ... · LFIs should ..."), are both split at each item,
  with the shared intro line (e.g. "LFIs should:") kept on every item so none of them lose
  context. Verified against a real clause in TFS Guidelines (v12) — §2.5 "Policies and
  Procedures" bundles 6 separate "· LFIs should ..." obligations under one clause number;
  the splitter correctly produces 6 sub-obligations from it (bullets, not lettered parens,
  turned out to be the more common list style in this document — both are handled).
- **Multiple obligation sentences** — if a clause has 2 or more sentences that each
  independently contain an obligation word (must/shall/should/required to/etc.), each
  becomes its own sub-obligation.

If neither signal fires, the clause is left as one unit — this is deliberately
conservative, since fragmenting a clause that reads fine as one piece would hurt
retrieval more than it helps. Each sub-obligation then gets its own Step 1 expansion and
Step 3/4 retrieval; the sections found for each sub-obligation are merged back together
under the original clause (a section matching more than one sub-obligation is kept once,
at its best score) before Step 7. The pipeline panel shows the split and, for a clause
that was split, which sub-obligation each retrieved section came from.

### Step 3. BM25 retrieve — DONE
**Doc side: BOTH** — takes the (expanded) regulation clause as the query, and searches it
against every internal document's sections.
A keyword/lexical search over every section of the internal document(s), using the
expanded query from Step 1. Free, runs locally, no AI call.

**Sizing is dynamic, not a fixed number.** Instead of "always return the top 25 (or
100)," it keeps every section whose score is within a relevance threshold of the single
best match. If the best match is a clear standout, few sections come back; if many
sections are genuinely close, more come back. This matches the direction that retrieval
counts should be relevance-driven, not a hardcoded cutoff.

### Step 4. Embedding retrieve — DONE
**Doc side: BOTH** — embeds the regulation clause's own text live (on the spot, not
pre-indexed — see Stage A Step 3), then searches it against the pre-built internal-document
index.
A semantic/meaning-based search: the gov clause is embedded (turned into a vector) and
compared against every section's vector from Stage A step 3. This runs on the same local
embedding model as indexing — **free**, not a paid API call.

Same dynamic-sizing logic as Step 3 applies here too, instead of a fixed top-N.

Steps 3 and 4 run independently of each other (one doesn't wait on the other's results) —
they're two different ways of finding relevant sections, done in parallel.

### Step 5. Hybrid fusion — DONE
**Doc side: INTERNAL** — merges the two internal-side match lists from Steps 3/4 into one
ranked list; doesn't touch the gov clause itself.

Merges the BM25 list and the embedding list into one ranked list per clause, scored as
**0.4 × BM25 + 0.6 × embedding** (matching the original design). Raw BM25 scores and
cosine similarities aren't on the same scale, so each side is first normalized against its
own best score for that clause (its top match becomes 1.0) before combining — otherwise
one side's numbers would dominate just from being a bigger range, not from being more
relevant. A section found by both searches gets both sides added together, so it
naturally ranks above one only one search found; a section missing from one side scores 0
on that side rather than being dropped. Free — plain math, no AI call.

### Step 6. Adaptive select — DONE
**Doc side: INTERNAL** — trims the merged internal-section list; gov clause not involved.

Same dynamic-cutoff principle used everywhere else in this pipeline (see Step 3): keeps
every section within a relevance threshold of the clause's own best fused score, instead
of a fixed count. A soft floor/ceiling only guards the two extremes — a clause with just 1
or 2 genuinely relevant sections still gets those, and one generic clause can't pull in
the whole corpus.

### Step 7. Build context — DONE
**Doc side: BOTH** — pulls full text for the matched internal sections, and pairs it with
the regulation clause it's being built for.

Takes the fused, trimmed list from Steps 5/6, re-fetches the full text for each section
(the earlier preview only stores a short snippet), and assembles it into the block of text
that would be sent to the LLM. Previously this built directly off Steps 3/4's raw,
un-ranked, un-trimmed output (a simplified stand-in) — now that Steps 5/6 exist, it uses
their actual output, which is its originally intended job. A run saved before this change
still works (falls back to the old raw-union behavior if a saved retrieval record has no
fused list).

### Step 8. LLM judgment — BUILT, BUT PAUSED
**Doc side: BOTH** — the LLM prompt carries the regulation clause plus the internal
context built in Step 7.
The mechanism is fully real: same admin-configured LLM, same prompt template as before —
the only thing that changed is the LLM now receives the retrieval-based context from Step
7 instead of the full document markdown. **The actual LLM call itself is currently
commented out on purpose**, so no credit is spent while the rest of the pipeline is being
built and tested. It returns placeholder "not_evaluated" results instead. Every step
before it (1 through 7) is now genuinely built — this is the only piece left before the
full pipeline is real end to end.

This is the only step with meaningful cost when it's turned on (the LLM call itself).

### Step 9. Save — DONE
**Doc side: BOTH** — the saved finding records which regulation clause was checked and
which internal-document sections/matches backed the result.
The result (or, right now, the placeholder result) gets saved to the database, and the
loop moves to the next clause. Verified working end-to-end, including the retrieval match
data being saved correctly.

---

## Summary table

| Step | What it does | Doc side | Status |
|---|---|---|---|
| Parse | Azure DI reads the raw document | Both | Done (paid — Azure DI) |
| Extract | Split into sections | Both | Done (free) |
| Index | Embed each section | Internal only | Done (free, local) |
| Dictionary harvest | Auto-suggest new acronym/synonym entries (pending admin review) | Both | Done (free) |
| Regulation points | Build the gov clause list from structural chunking | Regulation only | Done (free) |
| 1. Query expansion | Look up dictionary, expand/collapse the gov clause's wording | Regulation only | Done (free) |
| 2. Sub-obligation split | Break a clause into sub-requirements, regex-based | Regulation only | Done (free) |
| 3. BM25 retrieve | Keyword search, dynamic sizing | Both | Done (free) |
| 4. Embedding retrieve | Semantic search, dynamic sizing | Both | Done (free, local) |
| 5. Hybrid fusion | Merge BM25 + embedding into one ranked list (0.4/0.6 weighted) | Internal only | Done (free) |
| 6. Adaptive select | Trim merged list to a sensible count, dynamic | Internal only | Done (free) |
| 7. Build context | Assemble text block for the LLM from the fused list | Both | Done (free) |
| 8. LLM judgment | Send context + clause to the LLM | Both | Built, LLM call paused (no cost yet) |
| 9. Save | Write result, loop to next clause | Both | Done |
