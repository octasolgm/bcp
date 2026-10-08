# Regul V5 Pipeline Review - every step, what is right, what is wrong, what to fix

Scope: the real-account compliance analysis on `/nd/analyse-regul-full-v2` (workflow engine
`RegulPipelineHybrid`, retrieval pipeline v2, judgment prompt v9), from uploading a document to
embedding the finalized policy text back into the internal document. Demo accounts are out of scope
and must stay untouched (see CLAUDE.md).

Written: 08 Oct 2026. Based on the code on branch `feature/regul-clause-context-and-evals` and the
clause 3.3 / 3.5 examples from the CBUAE AML-CFT Guidelines for Financial Institutions.

Legend used for every step:

| Mark | Meaning |
|---|---|
| OK | Correct as built, keep |
| PARTLY | Right idea, implementation limits quality, fix inside the step |
| WRONG | Produces wrong results today, must be fixed |
| NEW | Step that does not exist and is needed |

---

## 1. The problem in one page

What you see:

1. **False gaps** - the analysis reports something as missing that the internal documents already
   say (3.3: good-faith protection is in the AML Policy p.42 for "DIFC"; 3.5: the timeframe point).
2. **Invented requirements** - gaps and actions about things the clause never asks for (3.3:
   "define the reporting deadline", "evidence the first reporting cycle").
3. **Inconsistent results** - the same clause gives different gaps between runs.

Your hypothesis was "we only send relevant chunks, so the model misses content". That is part of it,
but the code shows **four separate causes**, and each one produces false gaps on its own:

| # | Cause | Where | Effect |
|---|---|---|---|
| C1 | Part of the clause is never searched | Step 2 sub-obligation splitter keeps only the first 8 pieces | 3.5: the paragraph with "size / timeframe / nature of funds are irrelevant", "ML is an independent offence" and "suspicion does not need proof of the predicate offence" is cut off before retrieval (verified, section 3) |
| C2 | Retrieval cannot find what it is not built to find | tiny embedding model (bge-micro-v2, 3 layers), whole sections embedded and cut at 512 tokens, whole clause used as the query, no stemming in BM25, weak synonym list | The policy passage that answers a requirement in different words (e.g. "regardless of the amount or the period of the transactions") is not in the top results |
| C3 | Too much unordered text reaches the judge | Step 6 keeps up to 60 whole sections (148k to 211k chars measured) ordered by score, not by requirement | Evidence that *is* in the context is overlooked ("lost in the middle"); results vary between runs |
| C4 | The judge decides what the clause requires, alone, every run | one prompt does clause understanding + requirement decomposition + evidence search + verdict + gaps + actions in one call; free-text gap list | The model re-decomposes the clause differently each run, splits "board members, employees and authorised representatives" into separate requirements, adds a deadline, and nothing in code checks that a gap traces back to the clause text |

Plus one non-pipeline cause that explains part of your 3.3 example:

| # | Cause | Where |
|---|---|---|
| C5 | The action text "...is being handled within the stated deadline", "define the escalation path and reporting deadline, and evidence the first reporting cycle" is **our own demo template**, not the AI | `bcp-web/src/lib/nd/action-plan-seed.ts` lines 52-60 (the `suspicious|str|sar|reporting` rule). Real accounts were switched to the AI's own action on 05 Oct, but action plans seeded before that (or reseeded from an older run) still carry template text. Those rows must be reseeded, see section 6 |

So the fix is not one prompt tweak. It is: search the whole clause, search it well, give the judge
less but better evidence per requirement, fix the requirement list before judging, and double-check
every gap against the whole corpus before reporting it.

---

## 2. Current workflow - map

```
STAGE A  Document prep (once per document)
  A1 Upload -> A2 Parse (Azure DI) -> A3 Extract (regex sections) -> A4 Index (embed sections)
                                         \-> A5 Dictionary harvest (acronyms)
  Regulation doc only: A6 Regulation points (clause list from A3 sections)

STAGE B  Analysis run (per clause)
  B0 Ensure internal docs indexed
  B1 Query expansion (acronym / synonym lookup)
  B2 Sub-obligation split (regex)
  B3 BM25 search            \  per sub-obligation, plus a second pass with
  B4 Embedding search       /  acronyms/synonyms swapped (pipeline v2)
  B5 Fusion (0.4 BM25 + 0.6 embedding)
  B6 Adaptive select (keep >= 50% of best, 5..60 sections)
  B7 Build context (full text of selected sections + clause outline headings)
  B8 LLM judgment (prompt v9) + retries
  B9 Post-processing (quote verification, status/gap consistency, page grounding) + save

STAGE C  Remediation
  C1 Gaps split from gap_description -> C2 action plans seeded -> C3 maker/checker review
  C4 Finalize -> C5 AI rewrites each resolved action into policy text -> C6 embed into PDF/DOCX copy
```

---

## 3. Stage A - Document prep

### A2. Parse - Azure Document Intelligence (prebuilt-layout, markdown) - OK, small fix

How it works: `AzureDocumentIntelligenceClient` sends a signed URL, polls, gets markdown plus page
spans. `LocalDocumentExtractionService.ParseWithAzureDocIntelligenceAsync` rebuilds per-page text
(also for Word, from page spans) and writes `<!-- BCP_PDF_PAGE:N -->` markers. PageHeader /
PageFooter / PageNumber comments are stripped.

Assessment: correct engine choice for both scanned and native files, real page numbers, tables kept.

Problem: running headers that Azure does **not** tag as PageHeader stay in the text as normal lines.
Your 3.5 clause text literally contains "Anti-Money Laundering and Combating the Financing of
Terrorism and Illegal Organisations Guidelines for Financial Institutions" in the middle of the
clause, several times in 3.10. The same happens in internal manuals ("Internal AML Manual - Confidential").
These lines:
- are added to every BM25 query and every section, inflating scores for the words "money
  laundering", "financing", "terrorism" everywhere (they stop discriminating);
- shift the embedding of short sections towards the document title;
- are shown to the judge as if they were clause text.

Fix (A2.1): after parse, detect lines repeated on 3+ pages (the splitter already has this logic in
`LocalSectionSplitter.RunningLines`, used only for TOC detection) and remove them from the stored
section/clause text (keep the raw markdown for page grounding). Applies to regulation and internal
documents.

### A3. Extract - regex section split - PARTLY

How it works: `LocalSectionSplitter.Split` detects numbered headings ("9.4.1 Title", "Article 12",
"Annex 1"), with many guards (footnotes, TOC, wrapped references, annex namespacing, repeated
top-level numbers). Text before the first heading becomes "Introduction". Free and deterministic.

Assessment: good for **regulation clause extraction** (the clause list must follow the regulator's
numbering, and this does). Not good enough as the **retrieval unit for internal documents**:

| Problem | Effect |
|---|---|
| Sections have no size limit. A long policy section ("7 Suspicious Activity Reporting", 4 pages) is one unit | It is embedded only by its first ~512 tokens (see A4); BM25 length normalisation (b = 0.75) pushes long sections down |
| Documents without numbering (SOPs, forms, Word documents with styles only) become one giant "Introduction" section | Effectively unsearchable by embedding; the judge gets the whole document or nothing |
| No heading path is stored (a "7.7.2" section does not know it sits under "7 SAR / 7.7 Financial Transactions") | A passage like "within 2 business days" without the heading is ambiguous to search and to the judge |
| Bold-only / styled headings without numbers are not detected | Content merges into the previous section |

Fix (A3.1 - NEW "retrieval chunks", keep sections as they are):
- Keep the section split exactly as is (it feeds page/section references and the finalize embed).
- Add a second layer: split every internal section into **passages of ~150-300 words**, cut at
  paragraph / bullet / sentence boundaries, 1-2 sentence overlap, tables kept whole when small.
- Prefix each passage with a context header used for search only:
  `"<Document title> > 7 Suspicious Activity Reporting > 7.7 Financial Transactions (p.37)"`.
- Store `parent_section_id`, `heading_path`, `page`, `char_offset` per passage, so the judge can be
  given the passage plus its parent section when needed ("small-to-big" retrieval) and the finalize
  step knows the exact place.
- Documents with no detected numbering: same passages, heading path from markdown headings / bold
  lines.

### A4. Index - embedding - WRONG (main retrieval weakness)

How it works: `IndexingWorkerHosted` embeds `section.ClauseText` with `LocalEmbeddingService`
(SmartComponents.LocalEmbeddings, model **bge-micro-v2**, 384 dimensions) into pgvector.

Problems:
1. **bge-micro-v2 is the smallest model in its family** (3 transformer layers). It is fine for
   "are these two sentences about the same topic" but weak at the fine distinctions compliance needs
   ("good-faith reporting protection" vs "confidentiality of reports" vs "tipping off").
2. **Truncation**: the library cuts input at 512 tokens. Any section longer than ~350-400 words is
   represented only by its beginning. The answer to a requirement is often in the middle of a
   section.
3. **Query side**: the whole clause (3.5 is ~3,000 characters) or a sub-obligation of up to ~1,400
   characters is embedded as the query, also cut at 512 tokens. A long query vector is a blur of many
   topics, so the nearest neighbours are "generally about money laundering".
4. Symmetric use: queries and passages are embedded the same way, with no query instruction (bge
   models are trained with a "Represent this sentence for searching relevant passages:" prefix for
   queries).

Fix (A4.1):
- Embed the A3.1 passages (short, so no truncation), with the heading-path prefix.
- Replace the model. Two options, both work with the existing code shape:

| Option | Quality | Cost | Notes |
|---|---|---|---|
| Azure OpenAI `text-embedding-3-small` (1536-d) - **recommended** | Large step up | ~$0.02 per 1M tokens: all 5 current internal docs (~150k tokens) cost under $0.01 to index | Client already exists (`AzureOpenAIEmbeddingClient`). Documents already go to Azure for parsing, so no new data-residency category |
| Local `bge-base-en-v1.5` or `gte-base` (768-d, ONNX) | Clear step up, below option 1 | Free | Stays fully on our server; needs the ONNX model file shipped |

- Store `embedding_model` on the extraction row; never compare vectors from different models;
  re-index existing documents once (background job).

### A5. Dictionary harvest (acronyms / synonyms) - PARTLY, two bugs

How it works: after extract, `AcronymHarvester` finds "Full Form (ABBR)" pairs and unknown
all-caps tokens; synonym candidates come from embedding similarity. Seeds:
`SeedData/dictionary-seed.json` (20 acronyms), `SeedData/synonym-seed.json` (10 pairs).

Problems:
1. **Auto-harvested acronyms go live immediately** (`UpsertAsync` inserts `is_active = true`). The
   status doc says they wait for admin review; the code does not. A bank's own internal acronym
   harvested from its manual is then used in every workspace's query expansion (dictionaries are
   platform-wide).
2. **Several seed synonyms are wrong for search**:
   - `politically exposed person` <-> `high-risk customer` (a PEP is one kind of high-risk customer,
     not the same thing; every PEP clause now also pulls generic risk sections)
   - `money laundering` <-> `financial crime` (much broader)
   - `policy` <-> `manual` (fires on almost every clause, adds noise)
   - `customer due diligence` <-> `know your customer checks` (fine) but `CDD` / `KYC` are separate
     acronyms that never meet each other
3. Synonyms match by plain substring (`"policy"` also matches `"policyholder"`); no plural /
   inflection handling ("report" vs "reporting" vs "reported").
4. Pairs instead of concept groups: STR, SAR, suspicious transaction report, suspicious activity
   report, goAML report, "report to the FIU" are one concept but are stored as unrelated pairs.
5. The institution's own name is the most important synonym and is missing. Regulations say
   "Financial Institution / FI / LFI / supervised institution"; the manual says "the Bank", "DIFC",
   "the Company". This one gap is behind many "policy does not state that FIs must..." false gaps.

Fix (A5.1):
- Harvested acronyms inactive until approved (same as synonyms).
- Replace pairs with **concept groups** (one row per concept, many surface forms), whole-word,
  case-insensitive, with simple inflection (stemmed match).
- Clean the seed: remove the 4 bad pairs above, add an AML/CFT and general banking concept list
  (STR/SAR/goAML/FIU report; CO/MLRO/compliance officer; EWRA/business risk assessment;
  TFS/sanctions screening; CDD/KYC/customer identification; UBO/beneficial owner; etc.).
- **Workspace-level "self names"**: a small per-workspace list ("the Bank", "DIFC", ...) that is
  treated as equivalent to FI/LFI/supervised institution in search and is passed to the judge
  prompt as a fact ("In these documents the institution calls itself: ..."). Stored per tenant
  (`ITenantScoped`), since it is client data.

### A6. Regulation points (clause list) - OK, one fix

How it works: the regulation's structural sections become the selectable clauses, "{docId}:{no}".
v9 adds the outline (parent / siblings / children headings) as supporting context.

Fix: strip running headers (A2.1) from the clause text. Otherwise fine.

---

## 4. Stage B - Analysis run

### B0. Ensure indexed - OK

All entry points (new analysis, rerun all, clause rerun, gap evidence re-check) parse/extract/index
missing internal documents first. Keep.

### B1. Query expansion - PARTLY

How it works: `DictionaryExpansionService.ExpandQueryDetailedAsync` finds dictionary terms in the
clause; v1 appends the counterparts to the query; v2 also searches a copy of the text with every
match swapped (`BuildExpandedWording`).

Assessment: v2 is the right idea. Its value is capped by A5 (dictionary quality) and by the fact
that the query is the whole clause text (a few swapped words in a 400-word query move BM25 and the
embedding very little).

Fix: after B2-new (requirements with their own short search queries, see section 5), expansion is
applied to each **short query**, where a swapped term actually changes the result. Keep the v2
swap logic, feed it concept groups.

### B2. Sub-obligation split (regex) - WRONG

How it works: `SubObligationSplitter.Split` splits at bullets / (a) (b) items or at sentences with
must/shall/should; prepends the intro text to each item; **keeps at most 8 parts**
(`MaxParts = 8`).

Verified on clause 3.5 (the text you pasted, simulated with the same regex):

```
pieces: 10   kept: 8
KEPT 0    the intro itself (and the intro is then prepended to it again - duplicated)
KEPT 1-4  the four money laundering acts (piece 4 also carries the funds/proceeds definitions)
KEPT 5-7  crypto / securities / contracts asset examples
DROPPED 8 intellectual property
DROPPED 9 physical property + "The size or monetary value ..., the timeframe ..., and the nature
          of the funds ... are irrelevant to the suspicion and reporting" + "ML is a criminal
          offence ... prosecution independent of the predicate offence" + "suspicion ... not
          dependent on proving that a predicate offence has occurred" + the NRA sentence
```

So the timeframe requirement - the exact one that came back as a false gap - is **never searched**.
The internal passage about transaction periods is found only by luck (if another query happens to
pull it).

Other problems:
- The splitter cuts by layout, not by meaning: 5 asset-type bullets are 5 "obligations", while the
  real requirement ("assets of any form, not only money") is one. The definitions of funds and
  proceeds, which are separate requirements, are glued onto the last ML act.
- The ~290-character intro is prepended to every piece, so all 8 queries look alike and retrieve the
  same "definition of money laundering" sections.
- OCR bullets (". Currency smuggling" in 3.10) are not detected.
- It does nothing for prose clauses (3.3 has no bullets and no modal verb, so it is searched as one
  query, which is acceptable there by luck).

Fix:
- Quick fix (P0): remove the `MaxParts` truncation (or merge the tail into the last kept part, never
  drop text), do not prepend the stem to the stem, cap the prepended stem to its first sentence.
- Real fix (P1): replace the regex split with **clause profiling** (section 5, step B2-new): the
  requirement list is produced once per regulation clause by an LLM, reviewed, cached, and every
  requirement carries its own short search queries.

### B3. BM25 - PARTLY

How it works: `Bm25Scorer` (k1 1.5, b 0.75), tokens `[a-z0-9]{2,}`, 26 stop words, keep sections
scoring >= 50% of the best, up to 300.

Problems: no stemming ("report"/"reporting"/"reported", "launder"/"laundering" are different
tokens); query is a long clause so every common word scores; running headers inflate (A2.1);
sections instead of passages.

Fix: Porter stemming (or a light English lemmatiser), larger stop list including regulatory filler
("institution", "financial", "law", "article", "decision"), run on A3.1 passages with the heading
path as a boosted field, queries from B2-new. Keep the dynamic cutoff idea.

### B4. Embedding search - WRONG (follows from A4)

How it works: embed query, pgvector cosine top 300, keep everything >= 85% of the best similarity.

Problem: with a small model, cosine scores cluster in a narrow band (for example 0.62-0.74), so
"85% of the best" keeps very many loosely related sections. Combined with A4's truncation this is the
main reason the right passage is missing or buried.

Fix: new model on passages (A4.1); fixed top-k per query (e.g. 30) instead of a relative cutoff,
because cosine scores are not comparable across queries; query instruction prefix if a bge model
is used.

### B5. Fusion - PARTLY

How it works: each side is divided by its own best score, then 0.4 BM25 + 0.6 embedding.

Problem: max-normalisation is fragile: one outlier BM25 hit (e.g. a section that repeats "money
laundering" 40 times) squashes all other BM25 scores toward 0; a section found by only one side is
capped at 0.4 or 0.6.

Fix: **Reciprocal Rank Fusion** (score = sum of 1/(60 + rank) over the lists, per query) - standard,
scale-free, needs no tuning. Fuse per requirement, not per clause.

### B6. Adaptive select - WRONG for this task

How it works: keep sections with fused score >= 50% of best, minimum 5, maximum 60; the whole set
for the clause, in score order.

Problems:
- Selection is per clause, not per requirement. A requirement whose best passage ranks 70th for the
  whole clause is dropped, even though it is the only passage for that requirement. That is exactly
  the "it gives a gap for a point that exists somewhere in the docs" pattern.
- 60 whole sections = 150k-210k characters. More text is not more recall for the judge: it dilutes
  attention and causes run-to-run variance.

Fix: **per-requirement evidence packs**: top 5-8 passages per requirement after fusion (optionally
re-ranked, B6.1), expanded to their parent section when the passage is short, deduplicated across
requirements. Typical context drops to 20-40k characters while recall goes up, because every
requirement is guaranteed its own best evidence.

B6.1 (NEW, P2) - **re-ranker**: a cross-encoder (local `bge-reranker-base` ONNX, free; or a hosted
rerank model) scores (requirement, passage) pairs and is much more precise than either BM25 or
embeddings. Optional but the single biggest precision gain after A4.

### B7. Build context - PARTLY

How it works: full text of the selected sections, labelled "[Doc - 7.7 p.37]", score order; v9 adds
the clause's outline headings.

Fix: group the evidence **by requirement**:

```
REQUIREMENT R3: Suspicion and reporting do not depend on the size, value, timeframe or form of the funds.
  [E12] AML Policy.pdf > 7 Suspicious Activity Reporting > 7.3 Red flags (p.39)
        "...regardless of the amount involved or the period over which the transactions..."
  [E13] ...
```

plus a short "institution self-names" fact line (A5.1) and keep the outline headings (v9 is good).
Evidence ids (E12) are what the judge cites; code maps them back to document, section, page and the
exact offset, so references can never be invented.

### B8. LLM judgment - PARTLY (prompt asks the model to do too much at once)

How it works: one call per clause: system prompt v9 (about 2,500 words of rules), user block 1 =
excerpts, user block 2 = clause + outline + instructions; forced JSON / tool schema with free-text
`covered_elements`, `gap_description`, `suggested_action`; retries if a partial verdict has no gap or
action.

What is good: strict-scoping rule, institution coverage rule, legal-definition vs context rule,
verbatim-quote rule, quote verification afterwards, retry on delivery errors.

Why it still fails:
1. **The model re-derives the requirement list every run.** Variance in decomposition directly
   becomes variance in gaps. 3.3 shows the failure: the clause names "board members, employees and
   authorised representatives" and "even if they did not know the underlying crime", the model turns
   each phrase into a separate requirement, then reports them missing, although the system prompt
   explicitly says institution-level wording covers staff.
2. **Rules are not enforced in code.** "Do not include requirements not stated in the clause" is
   only an instruction. Nothing checks that each gap points to words in the clause.
3. **Free-text numbered lists** (`[1] ... - Missing: ...`) are parsed back by regex in several
   places (gap split, action split, finalize embed); every format slip creates a bug (several were
   fixed this week).
4. **Verdict before reasoning**: the schema lists `overall_status` first; non-reasoning models
   commit to a verdict before looking at evidence.
5. **Document-specific examples in a generic prompt**: "DIFC", "9.4.1", "Predicate Offences next to
   Money Laundering", AML acronym lists. You will onboard other domains and other banks; these
   examples bias the model toward the test documents. Examples must be generic or come from the
   workspace (self-names, dictionary).
6. **No notion of clause type**. 3.3 is a *statutory protection* (a right the law gives the bank);
   the only sensible internal counterpart is that the policy acknowledges it so staff are not
   deterred from reporting. 3.5 is a *definition / awareness* clause. 3.11 is a *penalty* clause.
   3.1 is a *summary* whose obligations are detailed in later chapters. Each type has a different
   "what counts as covered" rule; today one generic rule set is applied to all.

Fix: section 5 (B2-new profile + structured per-requirement judgment + verdict computed in code).

### B9. Post-processing and save - OK, extend

Quote verification against chunks and the full parsed documents, status/gap consistency, page
grounding, gap/action split, AI call log. All good.

Missing: a **gap verification** pass (B10-new below). Today a gap is final the moment the judge
writes it.

---

## 5. Target pipeline (what V5 should become)

Only the steps that change are described; everything else stays.

```
A  prep:   Parse -> strip running headers -> sections (unchanged) -> passages + heading path
           -> embed passages (better model) ; dictionary = concept groups + workspace self-names

B  run:    B2-new  Clause profile (cached per regulation clause)          [1 LLM call per clause, reused]
           B3/B4   Hybrid search per requirement query (stemmed BM25 + embeddings, expansion applied)
           B5      RRF fusion per requirement
           B6      Evidence pack per requirement (top 5-8, parent expansion, optional re-rank)
           B8-new  Judgment: per requirement status + evidence ids, verdict computed in code
           B10-new Gap verification: wide search for each "missing" requirement, focused check
           B9      Post-processing + save (as today)

C  remediation: action = draft policy text per missing requirement, with a target location
           (document + section + passage) chosen from retrieval; finalize embeds there;
           corrected copy is re-indexed as the new current version (closed loop)
```

### B2-new. Clause profile - the "understand the clause" step

One LLM call per **regulation clause** (not per run, not per bank): the regulation is shared, so the
result is cached on the regulation point, keyed by (regulation document version, clause no, profile
prompt version), visible and editable by a platform admin. Output (strict JSON):

```json
{
  "clause_type": "obligation | prohibition | definition | statutory_protection | penalty |
                  summary_of_obligations | regulator_facing | context_only",
  "addressee": "financial institution | senior management | staff | regulator | other",
  "requirements": [
    {
      "id": "R1",
      "statement": "The policy recognises that reporting suspicion in good faith is protected from criminal, civil and administrative liability.",
      "clause_anchor": "provide Financial Institutions, as well as their board members, employees and authorised representatives, with protection from any administrative, civil or criminal liability",
      "expected_internal_evidence": "A statement in the SAR/STR section that staff or the institution are protected when reporting in good faith.",
      "coverage_rule": "Institution-level wording covers board, staff and representatives. Restating every role is not required.",
      "materiality": "core | supporting",
      "search_queries": ["protected from liability for reporting in good faith", "safe harbour suspicious activity report", "no civil criminal administrative liability SAR good faith"]
    }
  ],
  "not_required": ["reporting deadline (not stated in this clause)", "procedure steps (covered by section 7)"]
}
```

Rules enforced **in code** after the call:
- every `clause_anchor` must be a verbatim substring of the clause text (otherwise the requirement is
  rejected and the call retried) - this is the mechanical guarantee of "no more than the clause";
- every sentence of the clause that contains an obligation word or a defined term must be covered
  by at least one anchor, or be listed as context - the guarantee of "no less than the clause";
- the outline context (v9) is passed in, so sibling topics are excluded.

Expected profiles for the two examples (what an auditor would write):

| Clause | Type | Requirements | Not required |
|---|---|---|---|
| 3.3 | statutory_protection | R1 policy acknowledges good-faith reporting protection from liability (institution-level is enough); R2 (supporting) protection applies even without knowing the exact crime / whether it occurred | any deadline, reporting cycle, naming each role |
| 3.5 | definition | R1 ML defined by the four acts (or the law's definition adopted by reference); R2 "funds" and "proceeds" understood broadly (any asset form, incl. virtual assets); R3 size / value / timeframe / form of funds irrelevant to suspicion and reporting; R4 ML is an independent offence, suspicion does not require proof of the predicate offence | the NRA sentence (context only), predicate offence list (own clause 3.6) |

With this, the 3.3 gap "extend protection to board members by name / define the deadline" cannot
be produced: neither is in the requirement list, and the judge is only allowed to judge listed
requirements.

### B8-new. Structured judgment

Input: the profile + per-requirement evidence packs. Output per requirement (schema order matters:
evidence first, verdict last):

```json
{ "requirement_id": "R3",
  "evidence": [{ "evidence_id": "E12", "quote": "verbatim text" }],
  "reasoning": "short",
  "status": "covered | partially_covered | not_covered | not_applicable",
  "missing": "only when not fully covered: what exactly is missing, in one sentence",
  "draft_policy_text": "only when not fully covered: the wording to add" }
```

Code then derives: clause verdict (all core covered -> compliant; none -> non_compliant; else
partial), fulfilments = covered requirements with quote + document/section/page from the evidence id,
gaps = one per not covered requirement, action = its draft text. No more numbered free-text lists to
re-parse.

### B10-new. Gap verification - the direct fix for "gap already exists in our documents"

For each requirement judged `not_covered` or `partially_covered`:
1. Wider search over **all** passages of all selected documents: its search queries + the `missing`
   sentence + exact keyword search of its key terms, top 30, re-ranked.
2. A short focused call: "Requirement: ... Does any passage below satisfy it, fully or partly? If
   yes, quote it." Cheap: small context, only for gaps.
3. If evidence is found and the quote verifies, the requirement flips to covered (or partial) with
   that evidence, and the trace records "recovered by gap verification" so you can measure how often
   the first pass missed.

Optional stronger variant for small corpora: when all selected documents together are under the
model's context budget (today's 5 documents are ~133k tokens), send the full documents **once per
run with prompt caching** and ask the verification question per gap. With caching the documents are
paid in full once and then at ~10% per call. This brings back full-document certainty only where it
matters (gaps), without the 3-minutes-per-clause cost of sending full documents for every clause.

### Clause-type rules (replace document-specific examples in the prompt)

| Type | Counts as covered | Never a gap |
|---|---|---|
| obligation / prohibition | policy imposes the same duty / ban (any wording, any section, institution self-name counts) | details the clause does not state |
| definition | policy states the definition, or adopts the law's definition by reference, or uses it operationally (red flags, training) in a way that shows the concept is applied | word-for-word restatement |
| statutory_protection | policy acknowledges the protection (so staff are not deterred) | naming each protected role; procedures |
| penalty | policy informs staff of the consequence (training / disciplinary section) | restating amounts |
| summary_of_obligations | each listed obligation exists somewhere in the policy at headline level | details (judged under the detailed clauses) |
| regulator_facing / context_only | nothing expected | everything |

---

## 6. Stage C - Remediation and finalize

### C1-C2. Gaps and first-draft action plans - PARTLY

How it works: gaps split from `gap_description` by "[n]"; real accounts get the AI's own
`suggested_action` lines; demo accounts keep the template catalogue in `action-plan-seed.ts`.

Problems:
- Your 3.3 action text is the demo template (see C5 in section 1). Runs whose action plans were
  seeded before 05 Oct still carry it. Fix: a one-off "reseed draft action plans from the AI action"
  for real-account runs where the action plan is still in draft and its text matches a template
  pattern (no change to demo runs).
- With B8-new the gap/action pairing is structured, so the regex split goes away.

### C3. Maker / checker review - OK

### C4-C5. Finalize and AI policy text - PARTLY

How it works: `NdFinalizeEmbedContentService` rewrites each resolved action into policy prose, one
note per gap (fixed this week to receive only its own gap).

Fix:
- Give the writer the **document's own terminology**: institution self-name, defined terms, and the
  text of the target section, so the inserted definition reads like the manual ("DIFC shall ..."),
  not like the regulator.
- Instruct it to write only the missing part (the requirement's `missing` sentence), never restate
  what the section already says (prevents duplicate content).

### C6. Embed into the document - PARTLY

How it works: DOCX - a real paragraph inserted after the anchor paragraph. PDF - a new page inserted
after the page of the cited evidence or of the section the action names (PDF text cannot be
reflowed with PdfSharpCore).

Fix:
- Target location comes from retrieval, not from text guessing: the best passage for the requirement
  (even when it did not satisfy it) is where the topic lives; its `parent_section_id` and page give
  the exact insertion point (A3.1 stores them).
- For PDF sources, also offer a **DOCX corrected copy** (Azure DI markdown -> DOCX) so the inserted
  definition sits inline in the right section; keep the PDF with inserted pages as the visual copy.
- **Close the loop**: index the corrected copy as the new current version of the internal document,
  and offer "re-check finalized clauses" so the clause is re-judged against it. Today a re-run uses
  the original document, so a finalized gap shows up again.

---

## 7. Prompts - concrete changes

| Prompt | Change |
|---|---|
| NEW clause profile prompt | as in 5 (B2-new); clause-type definitions; strict JSON; anchors must be verbatim |
| Judgment (v10) | input = profile + evidence packs; judge only listed requirements; evidence first, status last; per-requirement output; remove DIFC / 9.4.1 / AML-specific examples, replace with generic rules + workspace facts (self-names, dictionary terms found in the clause); keep: strict scoping, legal-outcome equivalence, OCR tolerance, verbatim quotes |
| NEW gap verification prompt | one requirement, wide evidence, "quote it or say not found" |
| Finalize policy writer | add document terminology + target section text; write only the missing part |
| Retry note | keep, but with B8-new it is rarely needed (verdict is computed in code) |

Also: temperature 0 (or lowest allowed) for profile and verification calls; for Kimi K3 keep
reasoning effort as today.

---

## 8. How we measure "not less, not more"

The clause evals added on 07 Oct store results; add **expected** labels so we can score:

1. **Requirement recall / precision** (profile quality): expected requirement list per eval clause
   vs generated.
2. **Retrieval recall@k** (no LLM, free, runs in seconds): for each expected requirement, the
   expected evidence location (document + page/section). Measures whether the right passage reaches
   the judge. This is the number that tells us whether a false gap is a retrieval problem or a
   judgment problem.
3. **False gap rate** and **missed gap rate** per clause vs the auditor's expected gaps.
4. **Stability**: run the same clause 3 times, count differing requirement statuses.

Start with 15-20 clauses across 3-4 regulations (not only AML 3.x), labelled once by a compliance
reviewer. Every pipeline or prompt change is then a before/after number instead of a re-read of
3.5.

---

## 9. Phased plan

| Phase | Items | Effort | Fixes |
|---|---|---|---|
| P0 quick wins (no design change) | B2: stop dropping clause text (MaxParts), no duplicated stem; A2.1 strip running headers (reg + internal); A5.1 harvested acronyms inactive, remove the 4 bad synonym pairs; reseed template action plans on real-account runs; B5 RRF; B4 fixed top-k | ~2 days | C1, part of C2, C5 |
| P1 retrieval foundation | A3.1 passages + heading path; A4.1 new embedding model + re-index; B3 stemming; concept-group dictionary + workspace self-names; B6 per-requirement packs (using regex split until P2) | ~5-7 days | C2, C3 |
| P2 intelligence | B2-new clause profile (cached, admin-editable); B8-new structured judgment + verdict in code; B10-new gap verification; prompt v10; clause-type rules; eval labels and metrics | ~7-10 days | C4, remaining C3 |
| P3 remediation loop | location from passages; DOCX inline copy for PDFs; writer gets document terminology; re-index corrected copy + re-check | ~4-5 days | finalize quality |
| Optional | B6.1 re-ranker | ~2 days | precision |

Each phase ships behind the existing pipeline version switch (`NdRegulPipelineVersions`: v3 = P1,
v4 = P2) and prompt versions, so old runs stay reproducible and you can compare in the evals page.
Demo accounts are not touched by any of this (they never run retrieval or AI).

---

## 10. Decisions needed

1. Embedding model: Azure OpenAI `text-embedding-3-small` (recommended) or local `bge-base`?
2. Clause profiles: OK to cache them per regulation clause and let platform admins review/edit them?
3. Gap verification: wide-search variant only, or also the full-document cached variant when the
   corpus fits?
4. Start with P0 now (small, safe), then P1?
