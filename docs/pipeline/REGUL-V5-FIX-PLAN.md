# Regul V5 - Fix Plan (point by point)

Each point gives: where the bug is (file and line), what happens now, why it is wrong, what to change,
what it resolves, cost now and after the change, and how we test it. Points are in the order
they should be done. Each one ships behind the pipeline/prompt version switch, so earlier runs stay
reproducible and can be compared on the Evals page.

Background: `REGUL-V5-PIPELINE-REVIEW.md` (why) and `REGUL-V5-STEPS-VERSIONS-COSTS.md` (steps and
costs). Demo accounts are not touched by any point: they never run retrieval or AI.

Decision recorded 08 Oct 2026: **no count limits** in query expansion (B1), sub-obligation split (B2)
and section selection (B6). What reaches the AI must be decided by relevance, never by a fixed
number. Points 1, 2, 3 and 9 implement this.

Cost basis: Kimi K3 at $3 / $15 per 1M input / output tokens, Claude Sonnet 5 at $2 / $10 (our
`LLM-COST-ESTIMATE.csv`). Today a clause sends ~40-55k input tokens: Kimi K3 ~$0.15-0.32 per
clause, Sonnet 5 ~$0.10-0.15.

---

## Overview

| # | Step | Bug / change | Type | Cost per clause after (Kimi K3) | Effort |
|---|---|---|---|---|---|
| 1 | B2 | Splitter drops clause text (8-part limit, short pieces dropped, intro duplicated) | Bug | unchanged | 0.5 day |
| 2 | B5/B6 | Fusion maths drops every keyword-only match; 60-section cap | Bug + no-limit | unchanged if done with 3 | 1 day |
| 3 | B3/B4 | 300-candidate caps; embedding keeps everything within 85% of best | Bug + no-limit | unchanged | 0.5 day |
| 4 | A2/A3 | Running page headers inside clause and section text | Bug | slightly lower | 0.5 day |
| 5 | A5/B1 | Harvested acronyms go live unreviewed; wrong seed synonyms; substring matching; harvest caps | Bug + no-limit | unchanged | 1 day |
| 6 | C1 | Demo template action text on real-account runs | Bug (data) | none | 0.5 day |
| 7 | A2 | Azure DI parse price logged at the Read rate | Bug (reporting) | none | 0.1 day |
| 8 | A4 | Regulation documents embedded for nothing | Waste | none | 0.1 day |
| 9 | B7 | Clause outline capped at 80 siblings / 80 children | No-limit | negligible | 0.1 day |
| 10 | A3/A4 | Search passages with heading path (sections stay) | Improvement | unchanged | 2 days |
| 11 | A4/B4 | Stronger embedding model + re-index | Improvement | +under $0.01 per document set | 1.5 days |
| 12 | B3 | BM25 stemming, stop words, heading field | Improvement | none | 0.5 day |
| 13 | A5/B1 | Concept groups + workspace self-names | Improvement | none | 1.5 days |
| 14 | B2 | Clause profile (requirements list, cached) | New step | +$0.02-0.05 once per regulation clause | 2.5 days |
| 15 | B6/B7 | Evidence per requirement, evidence ids | Improvement | **~$0.07-0.16** (from ~$0.15-0.32) | 1.5 days |
| 16 | B8 | Structured judgment prompt v10, verdict in code | Improvement | included in 15 | 2.5 days |
| 17 | B10 | Gap verification over all documents | New step | +$0.02-0.04 per gap | 1.5 days |
| 18 | Evals | Expected labels + retrieval recall metric | New | none (rule-based) | 1.5 days |
| 19 | C4 | Finalize writer gets section text + document terms | Improvement | unchanged (~$0.01-0.05 per note) | 0.5 day |
| 20 | C5 | Note placement from the requirement's passage | Improvement | none | 1 day |
| 21 | C5 | Inline DOCX corrected copy for PDF sources | Improvement | none | 2 days |
| 22 | C5 | Re-index corrected copy as current version + re-check | Improvement | one clause re-check per finalized clause | 1.5 days |

Phases: **P0 = points 1-9** (bugs and no-limit, ~4.5 days), **P1 = 10-13** (retrieval), **P2 = 14-18**
(clause understanding, judgment, gap check), **P3 = 19-22** (finalize loop).

---

## P0 - bugs and the no-limit decision

### Point 1 - B2 sub-obligation split drops clause text

- **Where:** `bcp-api/Services/NewDashboard/SubObligationSplitter.cs`
  - line 22 `MaxParts = 8`, applied at lines 75 and 91
  - line 63 / 82 `.Where(p => p.Length >= MinPartLength)` (20 characters)
  - lines 70-73 the intro ("stem") is prepended to every piece, including the stem piece itself
- **Current:** clause 3.5 splits into 10 pieces and the last 2 are thrown away. Simulated on the
  clause text, the dropped part contains "the size or monetary value ..., the timeframe ..., and
  the nature of the funds ... are irrelevant", "money laundering is a criminal offence ...
  prosecution independent of the predicate offence", and "suspicion ... not dependent on proving
  that a predicate offence occurred". Pieces under 20 characters are dropped too. The ~290-character
  intro is glued in front of every piece, so all queries look alike and the first piece is the
  intro twice.
- **Why it is wrong:** text that is never searched can never be found. The AI then reports it as
  missing. This is the cause of the 3.5 timeframe false gap.
- **Change:**
  1. Remove `MaxParts`. Every piece is searched.
  2. A piece under 20 characters is merged into the previous piece, never dropped.
  3. Prepend only the first sentence of the intro (the lead-in, e.g. "The AML-CFT Law defines money
     laundering as ..."), not the whole intro; never prepend it to the intro itself.
  4. Prose after the last bullet ("The size or monetary value ...") becomes its own piece instead of
     staying glued to the last bullet. A paragraph break after a bullet list ends the list.
  5. Add `·` / `•` / `-` / OCR "." bullets at line start (3.10 uses ". Currency smuggling").
- **Resolves:** every sentence of the clause is searched (no limit). 3.5 timeframe and
  independent-offence requirements reach retrieval.
- **Cost:** free (local). Search time rises with the number of pieces (3.5: 8 -> ~11 pieces, a few
  hundred ms). AI cost unchanged, because B6 decides what the AI sees.
- **Test:** unit tests with 3.5, 3.10 (OCR bullets, many items) and 3.3 (prose) texts: assert every
  sentence of the clause appears in at least one piece, no piece is dropped, and the stem appears
  once per piece.

### Point 2 - B5/B6 fusion drops every keyword-only match; 60-section cap

- **Where:** `bcp-api/Services/NewDashboard/HybridFusionSelector.cs`
  - lines 23-27 weights 0.4 / 0.6, `RelativeThreshold = 0.5`, `MinKeep = 5`, `MaxKeep = 60`
  - `Fuse` (line 41) divides each side by its best score; `SelectDynamic` (line 94) keeps sections at
    >= 50% of the best fused score, then cuts at 60
- **Current (verified with the same arithmetic):**
  - A section found only by BM25 scores at most 0.4. The best fused score is at least 0.6 (the top
    embedding hit alone gives 0.6) and usually ~1.0, so the cutoff is 0.3-0.5. A keyword-only
    section therefore needs to be almost the best BM25 hit to survive, and in a normal clause it
    never does. Example: the top BM25 hit for "timeframe", found only by keywords, scores 0.38
    against a cutoff of 0.5 and is dropped.
  - A section found only by embedding with similarity >= 85% of the best (which is exactly what B4
    keeps) scores >= 0.51 and **always** passes. In the simulation 120 sections passed the 50% rule
    and the 60 cap cut them to 60.
  - So the "dynamic" selection is really "every embedding hit, cut to 60, no keyword-only hits".
- **Why it is wrong:** exact wording matches (the policy literally says "good faith", "liability",
  "regardless of the period") are the most reliable evidence, and they are the ones thrown away. The
  60 cap then removes relevant sections at random by score order.
- **Change:**
  1. Replace the weighted max-normalised sum with **Reciprocal Rank Fusion**, per query:
     score = sum over lists of 1 / (60 + rank). Rank-based, so BM25 and embedding count equally and
     neither side's score range dominates.
  2. **No count limit** (`MaxKeep` removed, `MinKeep` removed). A section is selected when it passes
     the relevance gate of at least one query (BM25 gate from point 3, embedding gate from point 3)
     for at least one piece of the clause. The union is ranked by RRF and fully sent.
  3. Safety without truncation: if the selected text exceeds the model's safe input budget (admin
     setting, default 150k tokens), the judgment is split into two or more calls over disjoint halves
     of the evidence and the results are merged (a requirement is covered if any call covers it).
     Nothing is dropped. The split is logged on the clause trace.
- **Resolves:** keyword-only evidence reaches the AI; no relevant section is cut by a count.
- **Cost:** must ship **together with point 3**. Removing the 60 cap alone, with today's 85%
  embedding rule, would send ~120-300 sections per clause (~$0.40-1.00 per clause with Kimi K3,
  possibly too large for one call). With point 3's gates the expected set is similar to today's 40-60
  sections (still ~$0.15-0.32 per clause) but chosen by relevance. The real drop in cost comes with
  point 15.
- **Test:** unit test with the simulation above (keyword-only top hit must be selected); replay
  3.3, 3.5, 3.6 retrieval and compare selected sections and context size before and after, in the
  pipeline panel.

### Point 3 - B3/B4 candidate caps and the 85% embedding rule

- **Where:**
  - `bcp-api/Services/NewDashboard/RegulEmbeddingRetrievalService.cs` line 49
    `EmbeddingRelativeThreshold = 0.85`, line 50 `EmbeddingMaxKeep = 300` (used at line 449 `.Take`)
  - `bcp-api/Services/NewDashboard/Bm25Scorer.cs` lines 87-88 `relativeThreshold = 0.5`,
    `maxKeep = 300`
- **Current:** both searches stop at 300 candidates. Embedding keeps everything within 85% of the
  best similarity. With bge-micro similarities sit in a narrow band (e.g. 0.62-0.74), so almost
  everything loosely related passes.
- **Why it is wrong:** a fixed 300 is a count limit; the 85% rule does not measure relevance, it
  measures how flat the small model's scores are.
- **Change:**
  1. Remove both 300 caps: score every section (the corpus is a few thousand sections at most; BM25
     is in memory and pgvector over one run's sections is milliseconds).
  2. Embedding gate: **score-gap (elbow) cutoff** per query: sort similarities, keep everything
     above the largest drop in the top part of the curve. No fixed count, no fixed percentage; it
     adapts to each query and each model.
  3. BM25 gate: keep the existing "at least 50% of this query's best" rule, which is relevance-based,
     and add the same elbow check.
- **Resolves:** no count limits; selection follows real score drops, so a clause with 3 relevant
  sections gets 3 and one with 50 gets 50.
- **Cost:** free. Keeps point 2's context size in today's range.
- **Test:** for 3.3 / 3.5 / 3.6 log the similarity curve and chosen cut in the pipeline panel;
  confirm the known evidence sections (AML Policy p.42 for 3.3) are inside the cut.

### Point 4 - Running page headers inside clause and section text

- **Where:** `bcp-api/Services/LocalDocs/LocalSectionSplitter.cs`. `RunningLines` (line 400, lines
  repeated on 3+ pages) is computed but used only to detect table-of-contents entries; section text
  keeps those lines.
- **Current:** clause 3.5 contains "Anti-Money Laundering and Combating the Financing of Terrorism and
  Illegal Organisations Guidelines for Financial Institutions" in the middle of its text; 3.10
  several times. Internal manuals repeat their own header in every section.
- **Why it is wrong:** those words are added to every search query and every section, so "money
  laundering", "financing", "terrorism" stop discriminating. The AI also reads them as clause text.
- **Change:** in `Split`, drop lines that are running lines (same rule: >= 12 characters, on 3+
  pages) from section text; keep the raw markdown untouched for page grounding and quote checks.
  Re-extract and re-index existing documents (free, local).
- **Resolves:** cleaner queries and sections, cleaner clause text shown to the AI and the user.
- **Cost:** free; a few hundred fewer tokens per clause sent to the AI.
- **Test:** unit test with three pages sharing a header; re-extract the AML Guidelines and check
  3.5 / 3.10 text; quote verification must still pass on old runs (it uses the raw markdown).

### Point 5 - Dictionary: unreviewed acronyms, wrong synonyms, substring matching, harvest caps

- **Where:** `bcp-api/Services/LocalDocs/DictionaryExpansionService.cs`
  - line 255 `UpsertAsync` inserts harvested acronyms with `is_active = true`
  - lines 311, 323, 325 full forms and synonyms matched with `Contains` (substring)
  - lines 27-31 synonym harvest caps: 150 sections, 400 characters per section, 15 candidates per
    document
  - `bcp-api/SeedData/synonym-seed.json`: wrong pairs
- **Current:** an acronym harvested from one bank's manual is immediately used in every workspace's
  analysis. Seed pairs "politically exposed person" = "high-risk customer", "money laundering" =
  "financial crime", "policy" = "manual" pull wrong sections; "policy" fires on almost every clause.
  "policy" also matches inside "policyholder". Synonym suggestions only look at the first 150 short
  sections of a document and stop at 15.
- **Why it is wrong:** B1 expansion has **no count limit itself** (every matched entry is used). Its
  quality is limited by what is in the dictionary and how it is matched.
- **Change:**
  1. Harvested acronyms are inserted inactive (pending review), like synonym candidates.
  2. One-off startup fix: deactivate the 4 wrong seed pairs (and remove them from the seed file;
     the seed loader is insert-only, so the file change alone does nothing).
  3. Whole-word, case-insensitive matching for full forms and synonyms.
  4. No-limit harvest: drop the 150 / 15 caps and the 400-character cut (suggestions are
     review-gated, so more suggestions only means more to review, not more noise in runs).
- **Resolves:** expansion adds only correct counterparts; no cross-workspace leakage of a bank's own
  acronyms.
- **Cost:** free.
- **Test:** unit tests for whole-word matching and inactive harvest; check the Dictionary admin page
  shows the new pending entries.

### Point 6 - Demo template action text on real-account runs

- **Where:** `bcp-web/src/lib/nd/action-plan-seed.ts` lines 52-60 (demo template catalogue).
  Real accounts switched to the AI action on 05 Oct (`useAiAction`), but older seeded rows remain.
- **Current:** clause 3.3's action "Define the escalation path and reporting deadline, and evidence
  the first reporting cycle" / "...handled within the stated deadline" is the demo template, not the
  AI.
- **Why it is wrong:** it adds requirements the clause never states and looks like an AI error.
- **Change:** one-off API job: for real-account runs (runs not owned by a demo account, same check
  as `IsDemoOwnedRunAsync`), action plans still in draft (not resolved) whose text matches a template
  pattern are replaced with the AI's own action line for that gap. Resolved or edited actions are
  left alone and listed in the log.
- **Resolves:** no template wording in real-account action plans.
- **Cost:** free.
- **Test:** run on a copy of the 3.3 run; demo runs unchanged (assert by workspace and demo flag).

### Point 7 - Azure Document Intelligence price recorded at the Read rate

- **Where:** `bcp-api/Services/LocalDocs/LocalDocumentExtractionService.cs` line 47
  `AzureDocIntelligenceUsdPerPage = 0.0015m`.
- **Current:** $1.50 per 1,000 pages is the Read model price; V5 calls `prebuilt-layout`, listed at
  about $10 per 1,000 pages.
- **Change:** confirm against the Azure invoice, then set the confirmed rate (better: an admin
  setting, so it can follow the contract).
- **Resolves:** the usage log and credits show the real parse cost.
- **Cost:** no change in what Azure charges; reported parse cost goes up ~6-7x to the real figure.

### Point 8 - Regulation documents embedded for nothing

- **Where:** `bcp-api/Controllers/NewDashboard/LocalDocumentsController.cs` lines 270 and 331
  queue indexing for every extracted document.
- **Change:** skip the indexing job for regulation documents (V5 never searches their vectors).
  Keep it if point 14 later needs regulation vectors (it does not).
- **Resolves:** less CPU and database storage.
- **Cost:** free.

### Point 9 - Clause outline capped at 80 siblings / 80 children

- **Where:** `bcp-api/Services/NewDashboard/NdRegulClauseContextService.cs`, `MaxSiblingLines = 80`,
  `MaxChildLines = 80` (heading length cut at 160 characters).
- **Change:** remove the line caps (no limit); headings are short, so even 150 siblings add only a
  few thousand characters.
- **Cost:** negligible (headings only).

---

## P1 - retrieval foundation

### Point 10 - Search passages with heading path

- **Where:** new step after `LocalSectionSplitter.Split`; new table `nd_local_document_passages`
  (must be `ITenantScoped` and added to `bcp-api/Infrastructure/NdWorkspaceSchemaBootstrap.cs`, like
  `nd_local_document_extraction_sections`); `IndexingWorkerHosted` embeds passages.
- **Current:** a whole section (sometimes 4 pages) is one search unit; unnumbered documents are one
  "Introduction" section.
- **Change:** split each section into passages of ~150-300 words at paragraph / bullet / sentence
  boundaries, 1-2 sentence overlap, small tables whole. Store `section_id`, `heading_path`
  ("AML Policy > 7 SAR > 7.7 Financial Transactions"), page, character offset. Search runs on
  passages; the AI gets the passage plus its parent section when the passage is short. Sections
  stay as they are (references, finalize).
- **Resolves:** the middle of long sections becomes findable; unnumbered documents become
  searchable; the exact place of evidence is known (used again in point 20).
- **Cost:** free (local). Re-index once.

### Point 11 - Stronger embedding model

- **Where:** `bcp-api/Services/LocalDocs/LocalEmbeddingService.cs` (bge-micro-v2, 384-d, cuts input
  at 512 tokens), `AppDbContext` `vector(384)` column, `IndexingWorkerHosted`.
- **Change:** embed passages with Azure OpenAI `text-embedding-3-small` (client already exists:
  `AzureOpenAIEmbeddingClient`) or local `bge-base-en-v1.5`. Add `embedding_model` to the extraction
  row; never compare vectors from different models; background re-index; new vector column sized
  for the model.
- **Resolves:** meaning search that distinguishes close compliance topics (good-faith protection vs
  confidentiality vs tipping off).
- **Cost:** Azure option ~$0.02 per 1M tokens: the 5 current internal documents (~150k tokens)
  cost under $0.01 to index once; query embeddings are fractions of a cent per run. Local option
  free.

### Point 12 - BM25 stemming, stop words, heading field

- **Where:** `bcp-api/Services/NewDashboard/Bm25Scorer.cs` (tokeniser line 104, stop words line 97).
- **Change:** Porter stemming (report / reporting / reported), extra filler stop words
  ("institution", "financial", "article", "law", "decision"), heading path indexed as a boosted
  field.
- **Resolves:** word-form mismatches no longer hide evidence.
- **Cost:** free.

### Point 13 - Concept groups and workspace self-names

- **Where:** `DictionaryExpansionService`, new tables for concept groups (platform) and self-names
  (per workspace, `ITenantScoped`, added to `NdWorkspaceSchemaBootstrap`); admin pages.
- **Change:** concept groups (STR / SAR / suspicious transaction report / goAML report / report to
  the FIU as one concept). Per-workspace self-names ("the Bank", "DIFC") treated as equal to
  FI / LFI / supervised institution in search, and stated to the AI as a fact.
- **Resolves:** "policy does not say FIs must ..." false gaps where the policy says "DIFC shall ...".
- **Cost:** free.

---

## P2 - clause understanding, judgment, gap check

### Point 14 - Clause profile (the requirement list)

- **Where:** new service; new table keyed by regulation point (regulation document, clause number,
  profile prompt version), tenant-scoped with the regulation document; admin review screen; replaces
  `SubObligationSplitter` as the source of search queries (the splitter stays as fallback).
- **Current:** the AI re-decomposes the clause inside every judgment call, differently each run
  (3.3: "board members, employees, authorised representatives" became separate requirements; a
  deadline was invented).
- **Change:** one AI call per regulation clause returns: clause type (obligation, prohibition,
  definition, statutory protection, penalty, summary, regulator-facing, context only), requirements
  each with a **verbatim clause anchor**, expected internal evidence, coverage rule, and search
  queries; plus "not required" items. Code rejects any requirement whose anchor is not in the
  clause text, and checks that every obligation sentence is anchored (no more, no less). Saved and
  reused by every run.
- **Resolves:** stable requirement lists; invented requirements become impossible; the clause type
  decides what counts as covered.
- **Cost:** ~$0.02-0.05 per clause **once** (Kimi K3), reused by all later runs and re-runs.

### Point 15 - Evidence per requirement

- **Where:** `RegulEmbeddingRetrievalService.BuildPreviewAsync`, `NdRegulAnalysisProcessor`
  `BuildBundleFromPreviewAsync` / `PrepareForwardJudgmentAsync`, `NdRegulPolicyContextService`.
- **Change:** retrieval runs per requirement (its own queries, expansion applied to each query);
  every passage that passes the relevance gates of point 3 for that requirement is kept (no count
  limit); context is grouped under each requirement, each passage with an evidence id (E12) that maps
  back to document, section, page and offset.
- **Resolves:** every requirement is guaranteed its own best evidence; the AI no longer reads one big
  unordered pile.
- **Cost:** context per clause expected ~25-50k characters instead of 150-210k: input ~10-15k tokens.
  **Kimi K3 ~$0.07-0.16 per clause (today ~$0.15-0.32); Sonnet 5 ~$0.05-0.06 (today ~$0.10-0.15).**
  If a clause genuinely needs more, it gets more (no limit); the split rule of point 2 protects the
  call size.

### Point 16 - Structured judgment, prompt v10, verdict in code

- **Where:** `NdRegulPromptDefaults` (new v10 texts), `NdRegulLlmSchemas` (new schema),
  `NdAnalysisPromptVersionService` (seed v10), `NdRegulJudgmentPostProcessor`, gap/action splitting
  in `bcp-web/src/lib/nd/action-plan-seed.ts` and `NdActionPlanEmbedResolver`.
- **Current:** free-text numbered `gap_description` / `suggested_action` re-parsed by regex in
  several places; verdict field comes before evidence; prompt contains document-specific examples
  (DIFC, 9.4.1, "Predicate Offences").
- **Change:** the AI judges only the listed requirements: per requirement, evidence (evidence ids +
  verbatim quote) -> reasoning -> status -> missing -> draft policy text. Code computes the clause
  verdict, fulfilments (covered requirements with references), gaps and actions. Generic,
  domain-neutral prompt with clause-type rules; workspace facts (self-names) injected.
- **Resolves:** gaps tied to the clause; no format-parsing bugs; consistent results across runs and
  domains.
- **Cost:** included in point 15's numbers (output slightly longer, input much shorter).

### Point 17 - Gap verification over all documents

- **Where:** new step after judgment in `ExecuteForwardJudgmentAsync` (and the rerun / gap-evidence
  entry points); new trace step in the AI call log.
- **Change:** for each requirement judged not covered / partly covered: wider search over all
  passages of all selected documents (its queries + the "missing" sentence + exact key terms, every
  passage passing the gates), then one short AI call: "does any passage satisfy this requirement?
  quote it". A verified quote flips the status; the trace records "recovered by gap verification".
- **Resolves:** the "gap already exists in our documents" problem directly.
- **Cost:** ~$0.02-0.04 per gap (Kimi K3), only for gaps. A clause with 2 gaps: ~$0.05-0.08 extra.

### Point 18 - Eval labels and retrieval recall

- **Where:** `NdAnalysisEvalService`, `nd_clause_evals`, Evals page.
- **Change:** add expected labels per eval clause: requirement list, expected status per
  requirement, expected evidence location. New rule-based metrics: requirement match, retrieval
  recall (did the expected passage reach the AI, no AI call), false gap rate, missed gap rate,
  stability over 3 runs.
- **Resolves:** every change is measured, not judged by re-reading 3.5.
- **Cost:** free; labelling 15-20 clauses by a compliance reviewer once.

---

## P3 - finalize loop

### Point 19 - Finalize writer uses the document's own section and terms

- **Where:** `bcp-api/Services/NewDashboard/CorrectedDocs/NdFinalizeEmbedContentService.cs`
  `BuildPrompt`.
- **Change:** give the writer the target section's text and the workspace self-names; instruct it to
  write only the missing part, in the document's terminology ("DIFC shall ...").
- **Resolves:** inserted definitions read like the manual and do not repeat what the section says.
- **Cost:** unchanged, ~$0.01-0.05 per note (a little more input).

### Point 20 - Placement from the requirement's passage

- **Where:** `NdActionPlanEmbedResolver` (today: document_reference text, quotes, section names
  guessed from the action wording).
- **Change:** the insertion point is the best passage found for the requirement (point 15), even
  when it did not satisfy it: that is where the topic lives. Its section and page give the exact
  location.
- **Resolves:** notes land in the right section without guessing.
- **Cost:** free.

### Point 21 - Inline DOCX corrected copy for PDF sources

- **Where:** `NdCorrectedDocumentService`, new converter (parsed markdown -> DOCX), existing
  `NdCorrectedDocxEmbedder`.
- **Change:** besides the PDF copy with inserted pages, produce a DOCX where each note is a paragraph
  inside the target section.
- **Resolves:** policy owners get an editable document with changes in place.
- **Cost:** free.

### Point 22 - Re-index the corrected copy and re-check

- **Where:** finalize flow (`NdCorrectedDocumentService`), internal document versioning, B0 index
  check.
- **Change:** the corrected copy becomes the current version of the internal document and is
  parsed/extracted/indexed (Azure DI parse of the new pages, or reuse of the original parse plus the
  inserted text); "Re-check finalized clauses" re-judges only those clauses.
- **Resolves:** a finalized gap does not come back on the next run.
- **Cost:** parse of the new copy (per page, or nothing if we reuse the original parse) plus one
  judgment per re-checked clause (~$0.07-0.16 after point 15).

---

## Cost summary per clause (Kimi K3)

| Stage | Today | After P0 | After P2 |
|---|---|---|---|
| Clause understanding | inside judgment | inside judgment | ~$0.02-0.05 once per clause, reused |
| Judgment | ~$0.15-0.32 | ~$0.15-0.32 (same size, better chosen) | ~$0.07-0.16 |
| Gap verification | none | none | ~$0.02-0.04 per gap |
| **Per clause, typical** | **~$0.15-0.32** | **~$0.15-0.32** | **~$0.10-0.20** |

Sonnet 5 is about 40-60% of the Kimi K3 figures at each stage.

## Order of work

1. P0 points 1, 2 + 3 together, 4, 5, 6, 7, 8, 9: one pipeline version (v3) and replays of 3.3 /
   3.5 / 3.6 compared against v2 on the Evals page.
2. P1 points 10-13: pipeline v4, re-index.
3. P2 points 14-18: prompt v10 + clause profiles + gap verification.
4. P3 points 19-22.
