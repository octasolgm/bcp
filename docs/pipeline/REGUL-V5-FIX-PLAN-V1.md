# Regul V5 - Fix Plan V1 (accuracy first)

Written 08 Oct 2026. This plan comes **before** the remaining points of `REGUL-V5-FIX-PLAN.md`. It starts from a
full audit of a real run: section 1 says what was wrong, section 2 why, section 3 the tasks (bug, reason, fix, how
it works, how to test, status). The full history of this work is in `REGUL-V5-WORK-LOG.md`.

Audited run: `f8a76442-5c1d-41dc-8e2a-b0db972fbe1a`. Pipeline v3, prompt v9, Kimi K3. Clauses 3.3, 3.5 and 3.6
of the CBUAE AML-CFT Guidelines for FIs, against 5 internal documents:

| Short name | File | Pages |
|---|---|---|
| AML Manual | Internal A M L M a n u a l 290626 azure (1).pdf | 63 |
| Implementation Manual | internal -Implementation of AML CFTPF Manual (1).pdf | 56 |
| Document.pdf | Document.pdf.pdf (same text as the Implementation Manual) | 56 |
| CandNM | CandNM-.pdf.pdf (customer and nationality risk methodology) | 20 |
| Country RM | C O UNT RR M.pdf (country risk methodology) | 5 |

Method: every verdict, covered element, gap, action and quote of the run was checked against the text of the
original PDFs, page by page. Section sizes were measured by running our own section splitter on the same pages;
its section labels match the ones in the run ("rule 13.2.2", "7.5", "17.2").

---

## 1. What was wrong

### 1.1 Scorecard

| Item | Total | Correct | Partly correct | Wrong |
|---|---|---|---|---|
| Clause verdicts (compliant / partial) | 3 | 3 | 0 | 0 |
| Covered elements ("What this reference fulfills") | 11 | 9 | 2 (right conclusion, weak evidence) | 0 |
| Gaps | 5 | 2 (low risk, overlapping) | 1 (overstated) | **2 (false gaps)** |
| Actions (draft policy text) | 5 | 2 | 1 (too broad) | **2 (not needed: text already in the policy)** |
| Quotes (policy extracts) | 13 | 10 | 1 (a reference list, not evidence) | **2 (cited under the other document)** |
| Evidence the run should have used but did not see | - | - | - | **10 passages in 3 documents** (AML Manual p.5, p.14, p.31, p.36, p.58, p.59, p.62; Implementation Manual p.22, p.24; CandNM p.2 for 3.6) |

The verdicts were right, but 2 of 5 gaps were false, 2 of 5 actions would add text the policy already has, and
the strongest evidence for 3.3 was never seen.

### 1.2 Clause 3.3 Protection against Liability for Reporting Persons - verdict correct, evidence incomplete

| Output | Verdict | Evidence |
|---|---|---|
| Status compliant | Correct | - |
| [1] protection for good-faith reporting | Correct | AML Manual p.42: "DIFC is protected from any criminal, civil or administrative liability ... submitted in good faith" |
| [2] extends to board, employees, representatives | Right conclusion, weak evidence | Inferred from the institution-level sentence. Direct evidence not retrieved: **AML Manual p.14** "The employee who reports an STR will not be held liable ... as long as the report has been made and sent in good faith" |
| [3] applies without knowing the exact crime | Correct by equivalence | good-faith standard, p.42 |
| [4] regardless of whether illegal activity occurred | Right conclusion, weak evidence | Direct evidence not retrieved: **AML Manual p.14** "whether the suspicion is proven true or not" |
| Quote "AML-CFT Law Articles 9.1, 15, 24, 25, 27 ..." | Not evidence | A list of law references |

Only **4 sections** were selected for this clause; the best evidence (p.14) sits at word 436 of a 505-word section.

### 1.3 Clause 3.5 Money Laundering - verdict correct, 2 false gaps

| Output | Verdict | Evidence |
|---|---|---|
| Status partial, 70% | Correct (partial for gaps 1-2 only) | - |
| [1] ML definition (4 acts, knowledge) | Correct | AML Manual p.6, Article (2) |
| [2] independent criminal offence | Correct | AML Manual p.6 Article (2) 2; CandNM p.2 |
| [3] suspicion without proof of predicate offence | Correct | AML Manual p.6 Article (2) 3; CandNM p.2 |
| [4] suspicion inferred from indicators | Correct | Implementation Manual p.4; AML Manual indicators guideline |
| [5] amount irrelevant | Correct | Implementation Manual p.4; AML Manual p.38, p.46 |
| **Gap 1** no definition of "funds" | Correct, low risk | No document defines "funds" |
| **Gap 2** no definition of "proceeds" | Correct, low risk | No document defines "proceeds" |
| **Gap 3** ML not stated to cover non-money assets | **Wrong** | AML Manual **p.5** "real estate purchases ... investments in securities, artwork", "financial assets or stocks, precious commodities, or real estate"; **p.31** "Involvement in virtual assets", "cryptocurrencies or prepaid cards"; **p.36** no appetite to "accept assets known or suspected to be the proceeds of criminal activity"; Annex 1 **B.3 p.58** precious metals / gems, **B.4 p.59** property / vehicles, **B.18 p.62** "virtual currencies/cryptocurrencies" |
| **Gap 4** timeframe and nature of funds | **Wrong** (amount already covered) | Implementation Manual **p.22** "If the activity takes place over a period of time ... describe the duration of the activity"; **p.24** "expanding the time period for reviewing alerted transactions (e.g., from 30 days to 90 days)"; **p.22** instruments covered: "wire transfers, foreign currency, WPS, letters of credit ... money orders, credit/debit cards" |
| Gaps 1, 2, 3 together | Overstated | One concept ("funds = assets in any form") reported three times |
| Actions 3, 4 | **Not needed** | Would insert text the policy already covers |
| 2 quotes | Cited under the other document | "independent crime ..." is in CandNM p.2 (shown under AML Manual 7.7); "no minimum reporting threshold" is in the Implementation Manual p.4 (shown under CandNM). Left as is by decision |

### 1.4 Clause 3.6 Predicate Offences - verdict defensible, gap overstated

| Output | Verdict | Evidence |
|---|---|---|
| Status partial, 66% | Defensible | - |
| [1] attention to NRA threats in own risk assessment | Correct, imprecise label | Evidence is AML Manual p.30-32 (quoted correctly) |
| [2] consider all categories of risk | Correct | AML Manual 7.5 p.29 |
| **Gap** no definition of predicate offence | Partly correct, overstated | No "felony", "misdemeanour" or "punishable in both countries" anywhere, so a gap exists; but "no explicit adoption" is too strong: AML Manual p.6 uses "original offence", **CandNM p.2** "whether the original crime was committed inside or outside the UAE", Implementation Manual p.4 "related predicate offences" |
| Action | Too broad | Only the felony / misdemeanour and dual-criminality wording is missing |

---

## 2. Why it was wrong (root causes)

| # | Root cause | Evidence from this run | Fixed by |
|---|---|---|---|
| RC1 | **Sections far too large for the meaning search** (main cause) | Search unit = whole section (largest 3,635 words); the embedding model reads only the first ~380 words. Every missed passage sits beyond that point (table below) | Task 2 |
| RC2 | **Same meaning, different words** | timeframe vs period of time / duration; tangible / intangible assets vs virtual assets / property; predicate offence vs original offence | Tasks 3, 4 |
| RC3 | **Too few sections for single-paragraph clauses** | 3.3: 2 queries, 4 sections selected | Tasks 2, 6, 10 |
| RC4 | **Judgment rules too literal for definition clauses** | One concept became 3 gaps; asset-scope gap raised although p.5 (definitions) was in context | Task 9 |
| RC5 | **Nothing double-checks a gap** | False gaps went straight to the report | Task 10 |
| RC6 | Quotes not checked per document | 2 quotes under the other document | Task 8 (not done, by decision) |
| RC7 | Duplicate document | Document.pdf = Implementation Manual, 100% | Task 7 (not done, by decision) |
| RC8 (minor) | Section start page in some labels | "rule 13.2.2 p.40" for a quote on p.42 | Not changed |

Where the missed evidence sat (RC1):

| Missed evidence | Section (pages) | Section size | Position of the evidence |
|---|---|---|---|
| 3.3 employee protection, "sent in good faith" | AML 6 (p.13-14) | 505 words | word 436 |
| 3.3 "DIFC is protected ..." (found, by keywords) | AML rule 13.2.2 (p.40-43) | 1,439 | word 1,101 |
| 3.5 virtual assets / crypto risk factors | AML 7.5 (p.29-33) | 2,315 | word 1,187 |
| 3.5 crypto typology B.18 | AML Annex 1 (p.54-63) | 3,635 | word 3,286 |
| 3.5 duration of activity | Implementation 17.2 (p.21-24) | 1,122 | word 459 |
| 3.5 "30 days to 90 days" | Implementation 17.2 (p.21-24) | 1,122 | word 1,102 |
| 3.6 / 3.5 "independent crime ... inside or outside the UAE" | CandNM Introduction (p.1-5) | 1,878 | word 703 |

---

## 3. Tasks

### Overview

| # | Task | Root cause | Status | Switched on by | Cost to run |
|---|---|---|---|---|---|
| 1 | Free retrieval check | testing cost | **Built** | always available | $0 |
| 2 | Search passages with heading path | RC1, RC3 | **Built** | pipeline **v4** / v5 | $0 |
| 3 | Equivalent-term groups | RC2 | **Built** | seeds at API start; used by v4+ | $0 |
| 4 | Embedding model option | RC2 | **Built (setting, default unchanged)** | appsettings | $0 local; under $0.01 per document set on Azure |
| 5 | Retrieval speed | slow v3 run | **Built** | v4+ | $0 |
| 6 | Selection on passages | RC3 | **Built** (same no-limit relevance gate) | v4+ | $0 |
| 7 | Duplicate-document detection | RC7 | **Not done** - decided: evidence from every file is fine | - | - |
| 8 | Per-document citation check | RC6 | **Not done** - decided: citations stay as they are | - | - |
| 9 | Judgment prompt v10 | RC4 | **Built, not current** | Admin > Analysis prompts | same as today |
| 10 | Gap check before a gap is saved | RC5, RC3 | **Built** | pipeline **v5** | ~$0.05-0.10 per gap |

### Task 1 - Free retrieval check

- **What was wrong:** every change had to be tested with a paid AI run of the clauses, and a run cannot say
  whether a false gap came from the search (evidence never sent) or from the AI (evidence sent but misjudged).
- **What I changed:**
  - New table `nd_retrieval_expectations` (workspace-scoped, `ITenantScoped`, in `NdWorkspaceSchemaBootstrap`):
    the expected evidence snippets per clause, keyed like clause evals (regulation document + clause number).
  - `RegulEmbeddingRetrievalService.CheckClauseAsync`: runs Steps 1-6 exactly as an analysis (current pipeline
    version, the analysis's own documents), then looks every snippet up in the selected units and in the whole
    indexed corpus. A snippet matches when its normalized text is in the unit, or every word of 3+ letters is.
  - Endpoints in `EvalsController`: `GET nd/evals/retrieval-check/{runId}`, `PUT nd/evals/retrieval-expectations`,
    `POST nd/evals/retrieval-check/{runId}` (platform super admins).
  - "Retrieval check" button and drawer on the analysis report (`nd-retrieval-check-drawer` component).
- **How it works:** paste snippets (one per line) under each clause, Save, Run. Each snippet shows **Selected #rank**
  (it reaches the AI), **Not selected** (it is in the documents, with where, but the search did not pick it) or
  **Not in indexed text** (parsing, or the snippet differs from the document). Also shows parts searched, reworded
  searches, units selected, context size and time.
- **Resolves:** tells search problems from judgment problems, for free, before any AI run.
- **Test:** unit tests for snippet matching (line breaks, case, punctuation).

### Task 2 - Search passages with a heading path (pipeline v4)

- **What was wrong (RC1):** a whole numbered section was one search unit. The meaning search turns a unit into one
  vector from its first ~380 words only, so text deeper in a long section could only be found by exact keywords.
- **What I changed:**
  - `LocalPassageSplitter`: cuts each section into passages of ~150-300 words at paragraph and sentence ends, with a
    1-sentence overlap. Numbered sub-headings inside a section ("B.18 Other payment technologies") always start a
    new passage; short title lines ("Possible indicators") do when the open passage has enough text. Each passage
    keeps the page it starts on and a heading path: document title > parent headings > section heading >
    sub-headings.
  - New table `nd_local_document_passages` (workspace-scoped, deleted with its extraction, untyped `vector`
    column so the model can change).
  - `NdPassageIndexService`: builds and embeds a document's passages. Called by the indexing job after sections are
    embedded, and automatically before the first v4 search of a document indexed earlier.
  - `RegulEmbeddingRetrievalService` v4: the corpus is the passages; the AI reads each passage with a
    "Heading: ..." line (not in square brackets, which are citation labels).
  - Full text for the context, the pipeline panel and gap evidence comes from one helper that reads sections or
    passages (`LoadUnitTextsAsync`).
- **Measured on the 5 PDFs:** 147-180 passages per manual, median ~140-170 words, largest 271 words (every passage
  fits the model); no line of any section is lost; the B.18 crypto text gets its own heading path.
- **Resolves:** RC1 for every document: no evidence is beyond the meaning search any more.
- **Test:** unit tests (long annex, sub-headings, parent headings, pages, nothing lost); tables and vector column
  tested on PostgreSQL 16 + pgvector.

### Task 3 - Equivalent-term groups

- **What was wrong (RC2):** the clause says "timeframe"; the policy says "duration of the activity" / "time
  period". The dictionary had none of these pairs, and v2/v3 reworded a clause part only once per matched term.
- **What I changed:** 27 seed pairs from the audit in `SeedData/synonym-seed.json` (timeframe / time period /
  period of time / duration; intangible asset / virtual assets / digital assets; tangible or intangible assets /
  valuable assets / property; crypto currencies / cryptocurrencies / virtual currencies; precious metals and stones /
  precious metals or gems; monetary instruments / financial instruments; predicate offence (and offense) / original
  offence / original crime; suspicious transaction report / suspicious activity report; report suspicious activity /
  report suspicious transactions / submit an STR; authorised representatives / staff / employees; board members /
  directors; Financial Institutions / the bank / the institution; liability / held liable; are irrelevant to /
  regardless of / irrespective of; good-faith / good faith). v4 searches a part once **per alternative**
  (`BuildExpandedWordingVariants`), not only with the first.
- **Resolves:** RC2 on the keyword side, for these terms; admins add more on the dictionary page.
- **Test:** unit tests for the variants.

### Task 4 - Embedding model option (decision: keep local for now)

- **What was wrong:** the local model (bge-micro-v2) is small: it links "timeframe" and "duration" less reliably
  than a larger model.
- **What I changed:** setting `RegulRetrieval:EmbeddingProvider` in `appsettings.json`: `local` (default, unchanged,
  free) or `azure-openai` (the existing `AzureOpenAI` embedding deployment). `PassageEmbeddingService` is the one
  place that turns passages and queries into vectors; every passage stores the model name, and passages of another
  model are rebuilt on next use.
- **Recommendation:** keep `local`. Task 2 already fixes the main problem (passages fit the local model). Switch to
  Azure only if the retrieval check still shows meaning-based misses. See section 6.

### Task 5 - Retrieval speed

- **What was wrong:** the v3 run spent ~6 minutes in Steps 1-6 for 3 clauses.
- **What I changed:** v4 loads every passage vector once per run and scores queries in memory (no database query
  per search); dictionary matchers are built once per job; query vectors are cached; each clause's Steps 1-6 time
  is written to the API log (`Regul Steps 1-6 (...) for clause X in N ms`).
- **Test:** the time per clause in the API log and in the retrieval check.

### Task 6 - Selection on passages

- **What was wrong (RC3):** 3.3 selected 4 sections.
- **What I changed:** nothing new in the rule: the v3 relevance gate (no count limits, keyword-only matches kept)
  now runs on passages, where scores are more meaningful, and the equivalent-term variants add searches.
- **Measured (keywords only, offline):** 3.3 now selects 13 passages including AML Manual p.14 and p.42; 3.5 selects
  ~67 passages (~75k characters, about a third of v2's 211k) including p.5, p.22, p.31, p.62 and CandNM p.2.
  Still not selected by keywords: Implementation Manual p.23-24 ("30 days to 90 days") and AML Manual p.36
  ("accept assets ... proceeds"); the meaning search may add them, and Task 10 is the safety net.

### Task 7 - Duplicate-document detection - not done

- Decided on 08 Oct: when the same text is in several files, showing evidence from each file is fine.

### Task 8 - Per-document citation check - not done

- Decided on 08 Oct: citations stay as they are.

### Task 9 - Judgment prompt v10

- **What was wrong (RC4):** prompt v9 wanted express statements for definitions, treated each example of an
  illustrative list as a requirement, did not merge overlapping gaps, and contained examples from one bank's
  documents (DIFC, 9.4.1, "Predicate Offences").
- **What I changed:** `JudgmentSystemPromptV10` and `JudgmentUserQueryTemplateV10` in `NdRegulPromptDefaults`,
  seeded by `NdAnalysisPromptVersionService.EnsureJudgmentSemanticV10Async` **without** making it current:
  - Step 1 clause type: obligation / prohibition, definition / interpretation, statutory protection, penalty,
    summary, regulator-facing / context-only, each with its own "what counts as covered".
  - Definition / interpretation clauses are covered when the concept is applied in practice (typologies, red flags,
    risk factors, procedures).
  - "Such as / including / not limited to" lists are one requirement; parties named with the institution are not
    separate requirements.
  - One gap per missing concept, overlapping points merged; each gap names the clause words it comes from and a
    materiality (high / medium / low).
  - Domain-neutral; no examples from any client's documents.
- **Test:** unit test (valid placeholders, no document-specific names). Checkpoint B on the real clauses.

- **Result of the first test (08 Oct, 3.5 on v5 + v10): wrong, compliant.** v10 let typologies cover the
  "funds" / "proceeds" definitions and merged them into the scope point, and covered "size, timeframe and nature
  irrelevant" on a quote for the amount only. **Replaced by prompt v11** (seeded, not current): a formally defined
  term is covered only when its definition is stated or adopted by reference; every element of a multi-part
  requirement needs its own quoted evidence. Details in `REGUL-V5-WORK-LOG.md` entry 9. Test with **v11**, not v10.

### Task 10 - Gap check before a gap is saved (pipeline v5)

- **What was wrong (RC5):** a gap went straight from the AI's first answer to the report.
- **What I changed:**
  - `NdRegulGapVerifier` (pure logic, unit-tested): reads the gap lines, builds one short question per gap, reads the
    answer, and rewrites the judgment.
  - `RegulEmbeddingRetrievalService.CreateEvidenceSessionAsync` / `SearchEvidenceAsync`: the run's passages, vectors
    and dictionary held in memory, so the check runs during the parallel AI phase without touching the database.
  - `NdRegulAnalysisProcessor.VerifyGapsAsync`, called after every judgment on pipeline v5 (new analysis, rerun all,
    clause rerun, gap evidence re-check), with a "Gap check" trace per gap (pipeline panel and browser console).
- **How it works:** for each gap: search every passage of every selected document with the missing requirement and
  the clause words it comes from (with term expansion, no count limit); ask the AI: "does any of these passages
  cover it? quote it". The gap is removed only when the quote is verbatim in the passage the answer names; it then
  becomes a covered element with that quote and reference, and the other gaps and actions are renumbered. Partly
  covered: the gap stays with a note. No gap left: the clause becomes compliant. A failed check never changes the
  result.
- **Cost:** one AI call per gap, carrying all passages the wider search selected (typically 30-60): about
  $0.05-0.10 per gap with Kimi K3.

---

## 4. How to test (in order)

1. Pull, build, restart the API (creates the two new tables, loads the new seed terms, adds prompt v10 as non-current).
2. Admin > Analysis prompts: pipeline **v4**, prompt stays **v9**.
3. Open the v3 analysis report (run f8a76442) > **Retrieval check**, paste the snippets below, Save, Run.
   The first run builds the passages (a minute or two). Free.
4. If the snippets show Selected: **Checkpoint A** - run 3.3, 3.5, 3.6 on v4 / prompt v9 (~$1).
5. **Checkpoint B** - pipeline **v5**, prompt **v11** (v10 replaced, see Task 9); run 3.3, 3.5, 3.6 again (~$1-2).

Snippets for the retrieval check:

3.3
```
The employee who reports an STR will not be held liable whether the suspicion is proven true or not
DIFC is protected from any criminal, civil or administrative liability
```
3.5
```
describe the duration of the activity
from 30 days to 90 days
Involvement in virtual assets
virtual currencies/cryptocurrencies
investments in securities, artwork
Accept assets known or suspected to be the proceeds of criminal activity
The crime of Money Laundering is considered an independent crime from the original crime
There is no minimum reporting threshold
```
3.6
```
whether the original crime was committed inside or outside the UAE
related predicate offences
The findings of the recent National/ Sectorial Risk Assessment should be taken into consideration
```

Expected at Checkpoint B:

| Clause | Expected result |
|---|---|
| 3.3 | Compliant; evidence AML Manual p.42 and p.14 |
| 3.5 | Partial, one low-risk gap: adopt the AML-CFT Law definitions of funds and proceeds; asset scope and nature of funds covered (p.5, p.31, p.58-62). Timeframe (corrected 08 Oct after the first v11 run): no document states it is irrelevant, so either covered by practice with a quote from Implementation Manual p.22 / p.24, or a low gap that cites p.22 as partly addressed |
| 3.6 | Partial, one low-risk gap: define predicate offence (felony / misdemeanour, dual criminality); CandNM p.2 cited for inside / outside the UAE |

---

## 5. Cost

| Step | AI calls | Cost (Kimi K3) |
|---|---|---|
| Retrieval check | none | $0 |
| Checkpoint A | 3 clause judgments | ~$0.50-1.00 |
| Checkpoint B | 3 judgments + one gap check per gap | ~$0.60-2.00 |

Running cost after the fixes, per clause: judgment on a smaller context (~75k characters for 3.5 instead of 211k)
plus ~$0.05-0.10 per gap on v5.

## 6. Embedding, explained (question from 08 Oct)

Two different things are involved, and both are free today:

| Part | What it is | Where it runs | Cost |
|---|---|---|---|
| **Embedding model** | Turns a piece of text into a list of 384 numbers that represent its meaning; texts with similar meaning get similar numbers. Used twice: once per passage when a document is indexed, and once per clause part at analysis time | On our own API server (bge-micro-v2, a small model shipped as a file with the API build) | **Free** (server CPU only) |
| **pgvector** | A PostgreSQL extension that stores those numbers in a table column and compares them | Inside our database | **Free** |

How it is built: Extract splits a document into sections (and now passages) -> the indexing job runs the embedding
model on each passage and stores the numbers in `nd_local_document_passages` -> at analysis time each clause part
is turned into numbers the same way and compared with every passage; the closest passages are the meaning matches.
The keyword search (BM25) runs next to it and needs no model.

The local model's limit is that it reads at most ~380 words of a text. With whole sections that hid most of each
long section; with passages (Task 2) every passage fits, so the main problem is solved **without** any paid model.

Option for later (not needed now): Azure OpenAI `text-embedding-3-small` understands meaning better (for example
that "timeframe" and "duration of the activity" are the same idea). It is paid but very cheap: about $0.02 per
million tokens; indexing all 5 documents once costs under $0.01, and each clause's searches a fraction of a cent.
It sends passage text to Azure (documents already go to Azure for parsing). Switch only if the retrieval check shows
meaning-based misses: set `RegulRetrieval:EmbeddingProvider` to `azure-openai`, restart, run the check again.

## 7. Relation to the main plan

V1 brings forward points 10, 11, 13 (terms only; workspace self-names still to do), 16 (as interim prompt rules),
17 and 18 (retrieval check) of `REGUL-V5-FIX-PLAN.md`. After Checkpoint B the main plan continues with the clause
profile (14), structured judgment (16 full), workspace self-names (13) and the finalize points (19-22).
