# Query Expansion (Hybrid Pipeline Step 1)

Status: in progress. See `docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md` for how this fits
into the full 9-step hybrid analysis pipeline (this is Step 1 of that plan).

## Why

The future gap-analysis pipeline compares a regulation clause against internal policy text. A
gov clause might say "TFS" while the internal policy spells out "Targeted Financial Sanctions"
- same requirement, no shared wording. Keyword search (BM25, Step 3) alone would miss that
match entirely. Query expansion closes exactly this gap: before searching, expand the gov
clause's terms with their known acronym <-> full-form counterparts, so BM25 has both forms to
search with.

This is deliberately **not** an AI step. It's a flat dictionary lookup - the same "check
whether text looks like a known shape" philosophy `LocalSectionSplitter.cs` already uses for
clause detection, applied to a different shape: `Capitalized Phrase (ABBR)`.

## How the dictionary gets built

Two sources, one table (`nd_dictionary_entries`):

1. **Auto-harvested (primary, fully automatic).** Every time structural Extract runs on any
   document - regulation or internal, no distinction - a regex scans the parsed text for the
   `Capitalized Phrase (ABBR)` shape (e.g. `"Targeted Financial Sanctions (TFS)"`) and stores
   both directions: `TFS -> Targeted Financial Sanctions` and `Targeted Financial Sanctions ->
   TFS`. Runs synchronously, inline, right after structural extraction succeeds - no background
   job needed, since a regex pass over already-in-memory text takes microseconds (unlike
   indexing, which calls an embedding model per section and genuinely needs a background
   queue). A harvest failure is caught and logged - it can never fail the Extract call itself.
   The dictionary grows on its own as more documents get processed; nobody has to remember to
   add anything.

2. **Manually curated seed list (fallback only).** A small JSON file
   (`bcp-api/SeedData/dictionary-seed.json`) loaded once at API startup, for common
   cross-document terms (AML, KYC, CBUAE, UNSC, ...) that a given document might use without
   ever spelling out locally. Idempotent - only inserts entries not already present.

## What this dictionary does not solve

True synonyms with no shared acronym - "sanctions screening" vs "TFS screening" - have no
`(ABBR)` marker a regex could ever find. That's not a text-shape problem, so no amount of
dictionary-building here closes it. That gap is Step 4's job (embedding-based semantic search),
already built for internal-document indexing and reused for this purpose once Steps 3-4 are
wired up.

## Status of each piece

| Piece | Status |
|---|---|
| `nd_dictionary_entries` table | Built |
| Auto-harvest regex + wiring into Extract | Built |
| Manual seed list + startup loader | Built |
| `ExpandQuery` lookup service | Built (no live consumer yet) |
| Admin view/curation page | Built |
| Wired into live retrieval (BM25/embedding search) | **Not built** - blocked on Steps 3-4 of the hybrid pipeline, which don't exist yet |

## What's next

This dictionary has no consumer yet - Steps 3 (BM25 retrieve) and 4 (embedding retrieve) of
the hybrid pipeline need to exist before `ExpandQuery` does anything useful in a real analysis
run. Building those is the next milestone after this one.
