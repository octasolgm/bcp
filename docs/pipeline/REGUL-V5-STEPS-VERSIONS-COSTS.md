# Regul V5 - Every Step, Version and Cost (parse to finalize)

Scope: real accounts on `/nd/analyse-regul-full-v2` (workflow engine `regul_pipeline_hybrid_v5`).
Demo accounts run none of this: no parsing, no AI, no cost.

Written 08 Oct 2026 from the code on `feature/regul-clause-context-and-evals`. For the detailed
problem analysis behind the suggestions see `REGUL-V5-PIPELINE-REVIEW.md`, and for the task list
`REGUL-V5-FIX-PLAN.md`, both in this folder.

How to read the cost column:
- **Free** = runs on our own server (CPU only), no outside service, no per-call charge.
- **Paid** = an outside service bills us per page or per token.
- Prices are list prices: the Azure price list for Azure services, and our own
  `LLM-COST-ESTIMATE.csv` for AI models. The real charge for each AI call is in the AI usage log
  (OpenRouter returns the exact cost, other providers are estimated from tokens). Check one real
  run there before quoting numbers to a client.

---

## 1. The whole flow at a glance

| # | Step | What it does (one line) | Runs | Engine / version in use | Cost |
|---|---|---|---|---|---|
| A1 | Upload | Stores the file | once per document | storage | Free |
| A2 | Parse | File -> text with page numbers | once per document | Azure Document Intelligence, `prebuilt-layout`, API 2024-11-30, markdown output | **Paid, per page** |
| A3 | Extract | Text -> numbered sections / clauses | once per document | `LocalSectionSplitter` (regex rules) | Free |
| A4 | Index (embed) | Each section -> meaning vector | once per document | `bge-micro-v2` (384-d, local ONNX, SmartComponents.LocalEmbeddings 0.1.0-preview) | Free |
| A5 | Dictionary harvest | Finds acronyms and synonym candidates | once per document | `AcronymHarvester` regex + local embeddings | Free |
| A6 | Regulation points | Regulation sections -> selectable clause list | once per regulation | from A3 | Free |
| B0 | Index check | Parses/indexes any selected internal doc not yet ready | each run | A2-A4 | Paid only if a doc was never parsed |
| B1 | Query expansion | Adds acronym/synonym counterparts to the search | per clause | dictionary (20 seed acronyms, 10 seed synonyms + harvested); no count limit | Free |
| B2 | Sub-obligation split | Splits the clause into parts to search | per clause | `SubObligationSplitter` regex, max 8 parts (to be removed: no limit) | Free |
| B3 | BM25 search | Keyword search over all sections | per part | in-memory BM25 (k1 1.5, b 0.75) | Free |
| B4 | Embedding search | Meaning search over all sections | per part | pgvector cosine on A4 vectors | Free |
| B3b/B4b | Expanded-wording search | Same two searches with acronyms/synonyms swapped | per part | **Pipeline v2** (current default) | Free |
| B5 | Fusion | Merges both result lists | per clause | 0.4 x BM25 + 0.6 x embedding | Free |
| B6 | Select | Keeps the strongest sections | per clause | >= 50% of best, min 5, max 60 sections (to be removed: no limit) | Free |
| B7 | Build context | Full text of selected sections + clause outline | per clause | prompt user block 1 + clause outline (v9) | Free |
| B8 | AI judgment | Verdict, evidence, gaps, actions | per clause | **Prompt v9**, model chosen in admin settings | **Paid, per token** |
| B9 | Post-process + save | Checks quotes, references, gap/status consistency | per clause | `NdRegulJudgmentPostProcessor` | Free |
| C1 | Gaps + draft actions | One gap per numbered line, AI action as first draft | per clause | front end seeding (real accounts: AI action) | Free |
| C2 | Gap evidence re-check (optional) | User uploads evidence, clause judged again | per clause re-checked | A2-A4 for the new file + B1-B9 + evidence check call | **Paid** (parse + 2 AI calls) |
| C3 | Maker / checker review | People edit, resolve, approve | per run | UI | Free |
| C4 | Finalize - policy text | AI turns each resolved action into policy wording | per resolved action | finalize-writer model setting (falls back to the Regul model) | **Paid, per token** |
| C5 | Finalize - embed | Writes the notes into a new copy of the document | per document | PDF: new page after the cited page (PdfSharpCore); DOCX: paragraph after the anchor (OpenXML) | Free |

Not used by V5 (and therefore not paid): Landing AI parsing, reverse mapping, qualitative
assessment, Azure OpenAI semantic chunking (a separate test page only).

---

## 2. Versions in use today

| Item | Current version | Where it is set |
|---|---|---|
| Workflow engine | `regul_pipeline_hybrid_v5` | chosen when the run is created from the V5 page |
| Retrieval pipeline | **v2** - expanded-wording search (v1 = expansion words only appended) | Admin > Analysis prompts, pipeline switch; stored on each run and each clause |
| Judgment prompt - system | **v9** = v8 rules + supporting-context rule (~11,700 characters) | Admin > Analysis prompts |
| Judgment prompt - user block 1 (excerpts) | v9 row, same text as v5 (~1,100 characters + the excerpts) | Admin > Analysis prompts |
| Judgment prompt - user block 2 (clause) | **v9** = clause outline headings + clause + v5 instructions (~3,300 characters + clause) | Admin > Analysis prompts |
| Judgment model | admin setting (Regul workflow LLM). Runs this week used **Kimi K3** (reasoning effort high, output cap 65,536 tokens); **Claude Sonnet 5** was compared. Catalogue default for OpenRouter is `anthropic/claude-sonnet-5` | Admin > LLM settings |
| Parallel clauses | 8 at a time | env `BCP_REGUL_FORWARD_JUDGMENT_CONCURRENCY` |
| Retries | up to 3 deliveries per call (provider error, cut-off, bad JSON) + 1-2 content retries (partial verdict with no gap or action) | code |
| Finalize writer model | own admin setting, falls back to the Regul workflow model | Admin > LLM settings |
| Parse engine | Azure DI `prebuilt-layout`, API 2024-11-30 | appsettings |
| Embedding model | bge-micro-v2, 384 dimensions | code (fixed) |

Every run records its pipeline version, prompt versions and model; the analysis list shows them in
the "AI setup" column, and saved clause evals keep them too.

---

## 3. Cost per step - current numbers

### A2. Parse (Azure Document Intelligence) - paid, once per document

- Billed per page, once, when a document is first parsed. Re-extract, re-index and new analysis
  runs do **not** re-parse.
- **Azure list price for the Layout model is about $10 per 1,000 pages ($0.01 per page).** The code
  records **$0.0015 per page**, which is the price of the cheaper Read model
  (`LocalDocumentExtractionService.AzureDocIntelligenceUsdPerPage`). Our usage log may be
  understating parse cost about 6-7 times. Please confirm against the Azure invoice or the pricing page,
  then correct the constant.
- Example: 5 internal documents + 1 regulation, ~300 pages in total = **~$3.00 once** at the Layout
  list price (our log would show ~$0.45).

### A3-A6, B1-B7, B9, C1, C3, C5 - free

Local code and local model on our server. The only cost is server CPU/RAM. Indexing a few hundred
sections with bge-micro takes seconds.

Small waste: the extract endpoint also queues the **regulation** document for embedding, but V5
never searches regulation vectors. It costs no money, only CPU time.

### B8. AI judgment - paid, the main running cost

What one clause sends:

| Part | Size |
|---|---|
| Fixed prompt text (system + both user blocks) | ~16,000 characters (~4,000 tokens) |
| Clause text + outline headings | ~1,000-6,000 characters |
| Retrieved policy sections (B6) | measured **148,000-211,000 characters (40-55 sections)** on 3.5 / 3.6 |
| **Total input** | **~40,000-55,000 tokens per clause** |
| Output | ~2,000-10,000 tokens (Kimi K3 reasoning is included in output tokens) |

Estimated cost per clause (list prices from `LLM-COST-ESTIMATE.csv`):

| Model | Input $/1M | Output $/1M | Per clause (estimate) | 100-clause regulation |
|---|---|---|---|---|
| Kimi K3 (current) | 3 | 15 | **~$0.15-0.32** | ~$15-32 |
| Claude Sonnet 5 | 2 | 10 | **~$0.10-0.15** | ~$10-15 |
| Claude Opus 5 | 5 | 25 | ~$0.25-0.38 | ~$25-38 |

Notes:
- The "cost per clause" column in `LLM-COST-ESTIMATE.csv` assumes ~12,000 input tokens. Real V5
  clauses send about 4 times that, because B6 keeps up to 60 whole sections. The CSV understates
  the real V5 cost.
- Each retry is a full second call (same input again).
- Speed measured this week: Kimi K3 40-170 seconds per clause, Sonnet 5 about 23 seconds.

### C2. Gap evidence re-check - paid

Per re-checked clause: parse the uploaded evidence (Azure DI, per page), then one full judgment
call (same size as B8) plus one evidence-check call. That is roughly 1.2-1.5 times a B8 call.

### C4. Finalize policy writer - paid, small

One call per resolved action that goes into a new document copy: ~1,500-3,000 input tokens, output
a few hundred words plus reasoning. About **$0.01-0.05 per note** with Kimi K3, less with Sonnet 5.
Re-finalizing a run writes all notes again and pays again.

### Example: one typical run

5 internal documents (~250 pages), 1 regulation (~50 pages), 20 clauses, Kimi K3:

| Item | Cost |
|---|---|
| Parse 300 pages once (Layout list price) | ~$3.00 (one time, reused by later runs) |
| 20 clauses x ~$0.24 | ~$4.80 |
| Finalize, 15 notes | ~$0.40 |
| **First run** | **~$8.20** |
| **Each later run on the same documents** | **~$5.20** (no parse) |

---

## 4. Each step defined - how it works today and what I suggest

### A1. Upload
How it works: the file is stored, with tenant/workspace scoping.
Suggestion: keep.

### A2. Parse - paid
How it works: Azure gets a short-lived link to the file, runs the Layout model and returns
markdown. Page numbers are rebuilt from Azure's page data, including for Word files, and
`<!-- BCP_PDF_PAGE:N -->` markers are written. Tagged page headers and footers are removed.
Assessment: right engine, real page numbers, tables kept.
Suggestion:
- Also remove repeated running-header lines Azure does not tag (for example "Anti-Money Laundering
  ... Guidelines for Financial Institutions", which sits inside clause 3.5's text). They distort
  search and the AI context. Free, local.
- Fix the recorded price (see section 3).

### A3. Extract - free
How it works: regex detects numbered headings ("7.7 Title", "Article 12", "Annex 1"). Text up to
the next heading is one section. TOC lines, footnotes and wrapped references are filtered out.
Assessment: correct for regulation clauses. Too coarse as the search unit for internal documents:
long sections, unnumbered documents become one "Introduction" section, and there is no heading path.
Suggestion: keep sections, and add smaller search passages (~150-300 words, with the heading path
"Doc > 7 SAR > 7.7 ...") for retrieval. Free.

### A4. Index - free
How it works: each whole section is turned into a 384-number vector by bge-micro-v2 and stored in
pgvector.
Assessment: the weakest step. The model is very small, and text after ~512 tokens of a section is
ignored.
Suggestion: embed the short passages with a stronger model:
- Azure OpenAI `text-embedding-3-small` (recommended): $0.02 per 1M tokens, so all 5 current
  internal documents (~150k tokens) cost **under $0.01** to index once. Search queries cost
  fractions of a cent per run.
- Or local `bge-base-en-v1.5`: free, better than today, below the Azure option.

### A5. Dictionary harvest - free
How it works: regex finds "Full Form (ABBR)" pairs and unknown short forms. Local embeddings
suggest synonym pairs. Seed lists hold 20 acronyms and 10 synonyms.
Assessment: useful, with two bugs. Harvested acronyms go live without admin review. Several seed
synonyms are wrong ("politically exposed person" = "high-risk customer", "money laundering" =
"financial crime", "policy" = "manual").
Suggestion: review before activation; concept groups instead of pairs; a per-workspace list of the
bank's own names ("the Bank", "DIFC") treated as "Financial Institution". Free.

### A6. Regulation points - free
How it works: the regulation's extracted sections become the clause list. The parent, sibling and
child headings are sent as context (prompt v9).
Suggestion: keep. Strip running headers (A2).

### B0. Index check - free (paid only for a never-parsed doc)
How it works: every entry point (new analysis, rerun all, clause rerun, gap re-check) makes sure
the selected internal documents are parsed and indexed first.
Suggestion: keep.

### B1. Query expansion - free
How it works: finds dictionary terms in the clause. Pipeline v2 searches a second copy of the
clause with the terms swapped (CDD <-> customer due diligence).
Limit: none on expansion itself (every matched dictionary entry is used). What limits it is the
dictionary: unreviewed harvested acronyms, wrong seed synonyms, substring matching, and harvest
caps (150 sections / 15 suggestions per document).
Decision (08 Oct): no limits. Suggestion: keep expansion uncapped, remove the harvest caps (they
only create review suggestions), fix the dictionary quality (fix plan point 5), apply expansion to
short per-requirement queries.

### B2. Sub-obligation split - free
How it works: splits at bullets / (a)(b) items or at sentences with must/shall/should, puts the
intro sentence in front of each part, and **keeps at most 8 parts**.
Assessment: **bug**. On clause 3.5 it makes 10 parts and drops the last 2. These include the
"size / timeframe / form of funds irrelevant" sentence, which is why that requirement came back as a
false gap.
Decision (08 Oct): no limit. Suggestion: now, remove the 8-part cap and never drop a short piece
(fix plan point 1). Next, replace it with an AI "clause profile": one call per
regulation clause, saved and reused by every run and every bank. It lists the exact requirements,
each tied to a verbatim quote from the clause. Cost about $0.02-0.05 per clause, **once**.

### B3. BM25 search - free
How it works: classic keyword ranking over all sections. Keeps results scoring at least half of
the best.
Suggestion: add word stemming (report/reporting/reported), more filler stop words, and run it on
passages. Free.

### B4. Embedding search - free
How it works: pgvector nearest sections. Keeps everything within 85% of the best similarity (up
to 300).
Suggestion (no limit): new model (A4); remove the 300 cap; replace the 85% rule, which keeps far
too much with a small model, by a score-gap cutoff per query (keep everything above the largest
drop in similarity, no fixed count). Free, or a fraction of a cent with Azure (fix plan point 3).

### B5. Fusion - free
How it works: each list is divided by its own best score, then weighted 0.4 / 0.6.
Suggestion: Reciprocal Rank Fusion (standard, no tuning), done per requirement. Free.

### B6. Select - free
How it works: keeps sections scoring at least half of the best, 5 to 60, for the whole clause.
Assessment: **bug**. The fusion maths means a section found only by keyword search scores at most
0.4 against a cutoff of 0.3-0.5, so it is almost always dropped, while every embedding hit passes;
the 60 cap then cuts at random by score (fix plan point 2). Too much text (150-210k characters), and
nothing guarantees each requirement its own evidence.
Decision (08 Oct): no count limit. Suggestion: rank-based fusion (RRF); keep every passage that
passes the relevance gates for at least one requirement, with no minimum or maximum count; if the
total is larger than one AI call can take, split the judgment into several calls instead of dropping
anything. Because selection is per requirement and relevance-based, the expected context is
~25-50k characters instead of 150-210k, which also cuts B8 input cost about 4 times.

### B7. Build context - free
How it works: full text of the selected sections, labelled "[Doc - 7.7 p.37]", in score order,
plus the clause outline.
Suggestion: group evidence under each requirement. Give each passage an id (E12) that the AI
cites, so references can never be invented.

### B8. AI judgment - paid
How it works: one call per clause with prompt v9. The model decomposes the clause, finds evidence,
decides the verdict and writes gaps and actions, all as free text.
Assessment: good rules in the prompt, but nothing enforces them, and the clause is re-decomposed
differently every run.
Suggestion: the judge receives the fixed requirement list (B2 suggestion) and returns a status per
requirement (evidence first, then the decision). Code computes the clause verdict. Remove the
document-specific examples (DIFC, 9.4.1) from the prompt. Expected cost per clause with Kimi K3:
**~$0.07-0.16** (input ~12k tokens instead of ~50k). With Sonnet 5: **~$0.05-0.06**.

### B9. Post-process + save - free
How it works: drops quotes that are not verbatim, fixes page/section references from where the
quote really is, keeps status and gaps consistent, stores the AI call log.
Suggestion: keep. Add a new **gap verification** step: for every requirement judged missing,
search all passages of all documents again and ask one short question ("does any passage satisfy
this? quote it"). Paid, but only for gaps: ~$0.02-0.04 per gap. This is the direct fix for "gap
already exists in our documents".

### C1. Gaps + draft actions - free
How it works: the numbered gap list is split into gaps; real accounts get the AI's action lines as
first drafts; demo accounts get the template catalogue.
Assessment: runs seeded before 05 Oct still carry template text. Your 3.3 "reporting deadline /
first reporting cycle" action is template text, not the AI.
Suggestion: one-off reseed of those draft actions on real-account runs. Later, structured gap and
action per requirement (no text splitting).

### C2. Gap evidence re-check - paid
How it works: the uploaded evidence is parsed and indexed, the clause is judged again over the
run's documents plus the evidence, and an evidence-check call compares the result.
Suggestion: keep; it gets cheaper with the smaller B8 context.

### C3. Maker / checker review - free
Suggestion: keep.

### C4. Finalize - policy text - paid
How it works: one AI call per resolved action rewrites the action into policy wording, given only
its own gap.
Suggestion: also give the writer the target section's text and the document's own terms ("DIFC
shall ..."), and tell it to write only the missing part. Same cost.

### C5. Finalize - embed - free
How it works: PDF gets a new page after the cited page (a PDF page cannot be reflowed). DOCX gets a
real paragraph after the matching paragraph. Every finalize makes a new version of the copy.
Suggestion: take the insertion point from the passage found for the requirement. Also offer an
inline DOCX copy for PDF sources. Index the corrected copy as the new current version, so a re-run
sees the gap as closed (today it re-reads the original).

---

## 5. Cost today vs suggested, per clause

| | Today (v2 + prompt v9) | Suggested |
|---|---|---|
| Clause understanding | inside the judgment call, every run | profile once per regulation clause: ~$0.02-0.05 one time, reused |
| Search | free (weak) | free local, or under $0.01 per document set with Azure embeddings |
| Judgment input | ~40-55k tokens | ~10-15k tokens |
| Judgment, Kimi K3 | ~$0.15-0.32 | ~$0.07-0.16 |
| Judgment, Sonnet 5 | ~$0.10-0.15 | ~$0.05-0.06 |
| Gap double-check | none | ~$0.02-0.04 per missing requirement |
| **Total per clause, Kimi K3** | **~$0.15-0.32** | **~$0.10-0.20** |
| Accuracy | false gaps from missed text | each requirement searched and each gap re-checked |

The suggested pipeline is more accurate and also cheaper, mainly because the judge stops reading
150-210k characters of loosely related text per clause.

---

## 6. What I suggest doing first

1. **P0, ~2 days, free to run**: stop B2 dropping clause text; strip running headers; harvested
   acronyms inactive until reviewed; remove the wrong synonyms; reseed template draft actions on
   real-account runs; fix the Azure DI price constant; fixed top-k + RRF in search.
2. **P1**: passages + heading path, stronger embedding model, stemming, per-requirement evidence.
3. **P2**: clause profile, structured judgment, gap verification, prompt v10, eval labels.
4. **P3**: finalize placement from passages, inline DOCX copy, re-index corrected copies.

Decisions needed: embedding model (Azure `text-embedding-3-small` recommended vs local
`bge-base`), judgment model (Kimi K3 vs Sonnet 5 - with the smaller context Sonnet's speed and
price advantage stays, and its weaker points are covered by the requirement list and gap check),
and whether to start P0.
