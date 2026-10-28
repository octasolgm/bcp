# Workflow: semantic chunking and indexing

Status: Extract is built and working. Indexing is **not built yet** for this path - see the
gap called out in step 4 below. This is the meaning-based path - clauses are found by how
similar consecutive sentences are, not by reading numbers.

## The steps, in order

```
Upload  ->  Parse (once, same record as structural)  ->  Extract (semantic)  ->  Index (NOT YET BUILT)
```

### 1. Upload - once

Same as structural. Not repeated.

### 2. Parse - once, ever, same saved record

This is the **exact same parsed text** the structural workflow uses - nothing is re-parsed, no
extra Azure credit is spent. You can run semantic Extract on a document that was parsed weeks
ago, any number of times, with no cost from this step.

### 3. Extract (semantic) - sentence splitting + Azure OpenAI embeddings + similarity cut

This is where it genuinely differs from structural. No regex, no numbering is read at all.

1. The same saved parsed text is split into individual sentences (page markers are kept so each
   sentence still knows its source page).
2. **Each sentence**, one at a time, is sent to Azure OpenAI's embedding API and comes back as a
   vector of numbers representing its meaning. This is a real, metered API call per sentence -
   Azure has no memory between calls, so our backend keeps its own local list matching each
   sentence to its vector and its page number as the calls come back. For a 23-page document with
   ~300 sentences, that's ~300 individual calls, 8 running at once (not batched into 40 calls).
3. Going sentence by sentence, we compare each sentence's vector to the previous sentence's vector
   (cosine similarity - a 0 to 1 score of how close in meaning they are).
4. While consecutive sentences stay similar, they get added to the same chunk. The moment
   similarity **drops** below 0.5, that's read as "this is a new idea" and a new chunk starts.
5. A chunk is also force-cut at 60 sentences as a last-resort safety net only - not a normal
   limit, just a guard against one chunk silently growing unbounded. In practice real clauses
   should never hit this.
6. A sentence that legitimately continues across a page break (e.g. a clause starting on page 2
   and finishing on page 3) is **not** split just because the page changed - only a real drop in
   meaning-similarity ends a chunk, so the page boundary itself has no effect on the cut.

**Example**, the page-spanning case already discussed: suppose a clause's sentences run from the
bottom of page 2 into the top of page 3. Structural extraction already handles this correctly too
(it only cuts on a new numbered heading), but here specifically: sentence 40 (page 2) and sentence
41 (page 3) are compared by meaning, found similar, and stay in the same chunk - the chunk's
`sourcePage` is recorded as wherever the chunk started (page 2), and nothing about the page turn
itself causes a cut.

Output (saved to `semantic_sections_json` on the **same** extraction record used by structural -
a separate column, doesn't touch or overwrite `sections_json`):
```json
{ "clauseNo": "Chunk 7", "clauseText": "In addition to the regular screening utilizing the UN Consolidated List, institutions must also check...", "sourcePage": 2 }
```

Cost: embedding calls are cheap (`text-embedding-3-small`, roughly $0.02 per 1M tokens) but not
free like structural - every run of semantic Extract makes real API calls, even on a document
already Extracted before, because nothing from a previous run is cached.

### 4. Index (semantic) - NOT YET BUILT

This is the honest gap. Today, the background Indexing job that automatically embeds chunks into
the permanent, searchable `nd_local_document_extraction_sections` table **only ever reads
structural chunks** (`sections_json`). It has no code path that reads `semantic_sections_json` at
all. Running semantic Extract will **not** trigger indexing and will **not** produce any rows in
that table.

This was a deliberate scope decision, not an oversight: semantic extraction was built first to
let you compare its chunk quality against structural's before deciding whether semantic chunks
should also be indexed, and if so, how they'd coexist with structural's rows in the same table
(same document, two different chunkings, one search index - that needs its own design decision).
Until that decision is made, semantic results are viewable but not searchable.

## What's shared with the structural workflow, and what's different

| Step | Shared? |
|---|---|
| Upload | Same for both |
| Parse | **Exact same saved result reused by both** - this is the whole point |
| Extract | **Different method entirely** - embeddings + similarity here, regex in the structural workflow |
| Index | **Not shared at all yet** - structural is indexed automatically today; semantic has no indexing path built |

## The two embedding uses people mix up

Worth restating plainly because it's the most confusing part: the embeddings inside step 3 above
(Azure OpenAI, one per sentence, used only to decide where to cut) are **not** the same
embeddings that get saved for future search by Indexing. Those are a separate, later step, use a
different (local, free) model, run per final chunk rather than per sentence, and - per the gap
above - don't currently run for semantic chunks at all.
