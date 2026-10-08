# Regul V5 - Fix Plan V1 (accuracy first)

Written 08 Oct 2026. This plan comes **before** the remaining points of `REGUL-V5-FIX-PLAN.md`: it fixes why
the analysis gives wrong gaps today, based on a full audit of a real run. When V1 is done and verified, work
continues with the main plan (the points V1 does not already cover).

Audited run: `f8a76442-5c1d-41dc-8e2a-b0db972fbe1a`. Pipeline v3, prompt v9, Kimi K3. Clauses 3.3, 3.5 and 3.6
of the CBUAE AML-CFT Guidelines for FIs, against 5 internal documents:

| Short name | File | Pages |
|---|---|---|
| AML Manual | Internal A M L M a n u a l 290626 azure (1).pdf | 63 |
| Implementation Manual | internal -Implementation of AML CFTPF Manual (1).pdf | 56 |
| Document.pdf | Document.pdf.pdf | 56 |
| CandNM | CandNM-.pdf.pdf (customer and nationality risk methodology) | 20 |
| Country RM | C O UNT RR M.pdf (country risk methodology) | 5 |

Method: every verdict, covered element, gap, action and quote of the run was checked against the text of the
original PDFs (text layer, page by page). Section sizes were measured by running our own section splitter on
the same pages. Its section labels match the ones in the run ("rule 13.2.2", "7.5", "17.2"), so the sizes below
reflect what the pipeline indexed.

---

## 1. Scorecard - how much of the result is correct

| Item | Total | Correct | Partly correct | Wrong |
|---|---|---|---|---|
| Clause verdicts (compliant / partial) | 3 | 3 | 0 | 0 |
| Covered elements ("What this reference fulfills") | 11 | 9 | 2 (right conclusion, weak or wrong evidence) | 0 |
| Gaps | 5 | 2 (low risk, overlapping) | 1 (overstated) | **2 (false gaps)** |
| Actions (draft policy text) | 5 | 2 | 1 (too broad) | **2 (not needed: text already in the policy)** |
| Quotes (policy extracts) | 13 | 10 | 1 (a reference list, not evidence) | **2 (cited under the wrong document)** |
| Evidence the run should have used but did not see | - | - | - | **10 passages in 3 documents** (AML Manual p.5, p.14, p.31, p.36, p.58, p.59, p.62; Implementation Manual p.22, p.24; CandNM p.2 for 3.6) |

In short: the verdicts are right, but **2 of 5 gaps are false, 2 of 5 actions would add text the policy already
has, and 2 of 13 quotes point to the wrong document**. That is what a reviewer sees as "wrong gaps".

---

## 2. Clause by clause, with evidence

### 3.3 Protection against Liability for Reporting Persons - verdict correct, evidence incomplete

| Output | Verdict | Evidence |
|---|---|---|
| Status compliant | Correct | - |
| [1] protection for good-faith reporting | Correct | AML Manual p.42: "DIFC is protected from any criminal, civil or administrative liability ... submitted in good faith" |
| [2] extends to board, employees, representatives | Right conclusion, weak evidence | Run inferred it from the institution-level sentence on p.42. Direct evidence exists and was not retrieved: **AML Manual p.14** "The employee who reports an STR will not be held liable ... as long as the report has been made and sent in good faith" |
| [3] applies without knowing the exact crime | Correct by equivalence | good-faith standard, p.42 |
| [4] regardless of whether illegal activity occurred | Right conclusion, weak evidence | Direct evidence not retrieved: **AML Manual p.14** "whether the suspicion is proven true or not" |
| Quote "AML-CFT Law Articles 9.1, 15, 24, 25, 27 ..." (Document.pdf p.3) | Not evidence | It is a list of law references, and Document.pdf is a duplicate of the Implementation Manual |
| Coverage lines cite "rule 13.2.2 p.40" | Minor | The quote is on p.42 (p.40 is where the section starts) |

Retrieval selected only **4 sections** for this clause. The best evidence (p.14) sits at word 436 of a 505-word
section, past what the embedding model reads.

### 3.5 Money Laundering - verdict correct, 2 false gaps, 2 wrong citations

| Output | Verdict | Evidence |
|---|---|---|
| Status partial, 70% | Correct (should be partial for gaps 1-2 only) | - |
| [1] ML definition (4 acts, knowledge) | Correct | AML Manual p.6, Article (2) quoted in the definitions |
| [2] independent criminal offence | Correct | AML Manual p.6 Article (2) 2; CandNM p.2 |
| [3] suspicion without proof of predicate offence | Correct | AML Manual p.6 Article (2) 3; CandNM p.2 |
| [4] suspicion inferred from indicators | Correct | Implementation Manual p.4; AML Manual "Indicators of Suspected Money Laundering" |
| [5] amount irrelevant | Correct | Implementation Manual p.4 "no minimum reporting threshold"; AML Manual p.38, p.46 "regardless of the amount" |
| **Gap 1** no definition of "funds" | Correct, low risk | No document defines "funds" |
| **Gap 2** no definition of "proceeds" | Correct, low risk | No document defines "proceeds" |
| **Gap 3** ML not stated to cover non-money assets (crypto, securities, property ...) | **Wrong** | AML Manual **p.5** definitions: "real estate purchases ... investments in securities, artwork", "financial assets or stocks, precious commodities, or real estate"; **p.31** risk factors: "Involvement in virtual assets", "cryptocurrencies or prepaid cards"; **p.36** no appetite to "accept assets known or suspected to be the proceeds of criminal activity"; Annex 1 **B.3 p.58** precious metals/gems, **B.4 p.59** property/vehicles, **B.18 p.62** "virtual currencies/cryptocurrencies". Only intellectual property / royalties are not named (a wording improvement at most) |
| **Gap 4** timeframe and nature of funds not stated irrelevant | **Wrong** (amount part already covered) | Timeframe: Implementation Manual **p.22** "If the activity takes place over a period of time, provide the date ... and describe the duration of the activity"; **p.24** "expanding the time period for reviewing alerted transactions (e.g., from 30 days to 90 days)". Nature of funds: **p.22** reporting covers "wire transfers, foreign currency, WPS, letters of credit and other trade instruments ... money orders, credit/debit cards", plus the asset typologies above |
| Gaps 1, 2, 3 together | Overstated | One concept ("funds = assets in any form") reported as three gaps |
| Actions 1, 2 | Fine | Adopt the law's definitions of funds and proceeds in the Definitions section |
| Actions 3, 4 | **Not needed** | Would insert text the policy already covers; at finalize they become redundant notes in the AML Manual |
| Quote "The crime of Money Laundering is considered an independent crime ..." cited as **AML Manual §7.7 p.38** | **Wrong document** | The sentence exists only in **CandNM p.2** |
| Quote "There is no minimum reporting threshold ..." cited as **CandNM p.2** | **Wrong document** | Exists only in the Implementation Manual p.4 (and its duplicate Document.pdf) |

### 3.6 Predicate Offences - verdict defensible, gap overstated

| Output | Verdict | Evidence |
|---|---|---|
| Status partial, 66% | Defensible | - |
| [1] attention to NRA threats in own risk assessment | Correct conclusion, imprecise label | Cites §7.5 p.29; the NRA evidence is AML Manual p.30, p.31, p.32 (all quoted correctly in the extracts) |
| [2] consider all categories of risk | Correct | AML Manual §7.5 p.29 |
| 5 quotes | All correct | AML Manual p.29-33 |
| **Gap** no definition of predicate offence | Partly correct, overstated | No document says "felony", "misdemeanour" or "punishable in both countries", so a gap exists. But the text "contains no ... explicit adoption" is too strong: the AML Manual p.6 uses "original offence" (the UAE translation of predicate offence), **CandNM p.2** says "whether the original crime was committed inside or outside the UAE", and the Implementation Manual p.4 uses "related predicate offences" |
| Action | Too broad | Only the felony / misdemeanour and dual-criminality wording is missing |

---

## 3. Root causes (why the run got these wrong)

### RC1 - Sections are far too large for the search (main cause)

The search unit is a whole numbered section. The embedding model reads only the first ~380 words (512 tokens)
of each one. Measured on the run's documents:

| Document | Sections | Median words | Largest | Sections longer than the model reads |
|---|---|---|---|---|
| AML Manual | 46 | 313 | 3,635 | 18 |
| Implementation Manual | 55 | 188 | 2,425 | 15 |
| CandNM | 12 | 395 | 1,878 | 6 |

Where every piece of missed evidence sits:

| Missed evidence | Section (pages) | Section size | Position of the evidence |
|---|---|---|---|
| 3.3 employee protection, "sent in good faith" | AML 6 (p.13-14) | 505 words | word 436 |
| 3.3 "DIFC is protected ..." (found, by keywords) | AML rule 13.2.2 (p.40-43) | 1,439 | word 1,101 |
| 3.5 virtual assets / crypto risk factors | AML 7.5 (p.29-33) | 2,315 | word 1,187 |
| 3.5 crypto typology B.18 | AML Annex 1 (p.54-63) | 3,635 | word 3,286 |
| 3.5 duration of activity | Implementation 17.2 (p.21-24) | 1,122 | word 459 |
| 3.5 "30 days to 90 days" | Implementation 17.2 (p.21-24) | 1,122 | word 1,102 |
| 3.6 / 3.5 "independent crime ... inside or outside the UAE" | CandNM Introduction (p.1-5) | 1,878 | word 703 |

All of them are beyond what the embedding model reads, so only the keyword search can find them, and only when
the words match exactly (RC2). The whole Annex 1 (19 typologies, 10 pages) is one search unit, so the crypto
typology is invisible to the meaning search.

### RC2 - Same meaning, different words

| Regulation says | Policy says | Effect |
|---|---|---|
| timeframe | period of time, duration, time period | 3.5 gap 4 |
| tangible / intangible assets | virtual assets, cryptocurrencies, property, precious metals, securities | 3.5 gap 3 |
| predicate offence | original offence, original crime | 3.6 gap overstated |
| Financial Institutions, board members, employees | DIFC, the Bank, the employee who reports | 3.3 weak evidence |

The keyword search cannot link these; the small embedding model does it poorly; the dictionary has none of
these pairs.

### RC3 - Too few sections selected for single-paragraph clauses

3.3 is one paragraph, so it is searched with 2 queries (its text and the acronym-swapped text). The v3 relevance
gate keeps only sections that stand far above the rest for one of those queries: 4 sections. With RC1 fixed the
scores become more meaningful, but the gate needs re-checking on passages.

### RC4 - Judgment rules too literal for definition clauses

Prompt v9 asks for an express statement for legal definitions. It has no rule that:
- a concept the policy applies in practice (typologies, red flags, risk factors) is covered;
- an illustrative list ("such as, but not limited to: crypto, securities, IP ...") is one requirement, not one
  requirement per example;
- overlapping missing points are one gap, not three;
- a gap needs a materiality level.

Result: one concept became gaps 1, 2 and 3, and gap 3 was raised although the evidence that was retrieved
(p.5 definitions) already shows it.

### RC5 - Nothing double-checks a gap before it is shown

A "missing" requirement is reported straight from the first pass. There is no second, wider search for it.

### RC6 - Citations are not checked per document

After judgment, a quote is accepted when it appears **anywhere** in the run's documents. Nothing checks it is in
the document it is cited under, so two quotes were attributed to the wrong document. At finalize, notes are
placed by cited document, so this can put a note in the wrong file.

### RC7 - Duplicate document in the corpus

Document.pdf and the Implementation Manual are 100% identical text. Every quote and section from it appears
twice in the search results and in the AI context (more text to read, duplicated citations).

### RC8 (minor) - Section start page used as the evidence page in some labels

"rule 13.2.2 p.40" for a quote on p.42; "Introduction p.1" for the definitions on p.6. Labels in covered elements
use the section's first page; the document reference block is already grounded to the quote's page.

---

## 4. Fixes, in order

Each fix lists the root cause it removes, where it goes, and how we check it on this same run. Fixes 1-3 cost
nothing to verify; the AI is called only at the two checkpoints (section 5).

### Fix 1 - Expected results for this run + a free retrieval check (do first)
- **Why:** each AI run costs money; most of the errors above are retrieval errors that can be checked without AI.
- **What:**
  - Store the audit above as eval labels for 3.3, 3.5 and 3.6: the requirements, the expected status of each,
    and the expected evidence locations (document + page + a short text anchor), on the existing clause evals
    (`NdAnalysisEvalService`, `nd_clause_evals`).
  - New "Check retrieval" action on the Evals page: runs Steps 1-6 only for the labelled clauses against the
    selected documents and reports, per expected evidence, found / not found, rank, and the size of the context
    that would be sent. No AI call.
- **Done when:** the check shows today's misses (p.14, p.22, p.24, p.31, p.62, CandNM p.2) as "not found".

### Fix 2 - Search passages with a heading path (RC1)
- **Where:** new passage step after `LocalSectionSplitter.Split`; new table `nd_local_document_passages`
  (`ITenantScoped`, added to `NdWorkspaceSchemaBootstrap`); `IndexingWorkerHosted`;
  `RegulEmbeddingRetrievalService` searches passages.
- **What:** split every section into passages of ~150-300 words at paragraph / bullet / sentence boundaries with a
  1-2 sentence overlap; each passage carries its heading path ("AML Manual > Annex 1 > B.18 Other payment
  technologies") and its page. Sub-headings inside a section that the numbering splitter ignores (Annex "B.18",
  "Possible indicators", bold titles) start a new passage. The AI receives the passage plus the heading path,
  and the neighbouring passage when the passage is short. Sections stay as they are for references and finalize.
- **Done when:** retrieval check finds p.14, p.22, p.24, p.31, p.62, CandNM p.2 for their clauses.

### Fix 3 - Concept groups and the bank's own names (RC2)
- **Where:** `DictionaryExpansionService` (concept groups replace pairs), seed file, dictionary admin page; a
  per-workspace self-name list (`ITenantScoped`).
- **What (seed groups from this audit):** timeframe / time period / period of time / duration;
  assets / property / funds / virtual assets / cryptocurrencies / digital assets; predicate offence /
  original offence / original crime; suspicious transaction report / STR / SAR / suspicious activity report /
  goAML report; Financial Institution / FI / LFI / supervised institution + workspace self-names
  (DIFC, the Bank); employees / staff / board members / authorised representatives.
- **Done when:** retrieval check finds the same evidence with the old small embedding model too (keyword path).

### Fix 4 - Stronger embedding model (RC1, RC2) - needs your decision
- Azure OpenAI `text-embedding-3-small` (recommended; under $0.01 to index these 5 documents) or local
  `bge-base-en-v1.5` (free). Re-index once; `embedding_model` stored per extraction.

### Fix 5 - Retrieval speed (task from the v3 run: ~6 minutes for 3 clauses)
- **Why now:** passages multiply the number of search units by ~4-6.
- **What:** load each run's passage vectors once and score in memory (no database round trip per query);
  build the dictionary matchers once per run; log the time of each step per clause.
- **Done when:** Steps 1-6 for 3.3 + 3.5 + 3.6 take under 1 minute.

### Fix 6 - Re-tune selection on passages (RC3)
- **What:** with Fixes 2-5 in place, run the retrieval check and tune the relevance gate (no count limits stay)
  so every expected evidence passage is selected; report context size per clause.
- **Done when:** all expected evidence found for the 3 clauses, context per clause in the range of today's or
  smaller.

**Checkpoint A (one AI run, ~$1):** run 3.3, 3.5, 3.6. Expected: 3.3 cites p.14 for employees and
"proven true or not"; 3.5 gap 4 gone (p.22 / p.24 now in context); 3.5 gap 3 likely gone (p.31 / p.62 in
context).

### Fix 7 - Duplicate document detection (RC7)
- **Where:** run creation (`AnalysisRunsController`) and `RegulEmbeddingRetrievalService.LoadCorpusAsync`.
- **What:** when two selected documents have (nearly) identical parsed text, use only one in the search and show
  a warning on the new analysis page ("Document.pdf is the same as internal -Implementation ... ; it was used
  once").

### Fix 8 - Citation check per document (RC6, RC8)
- **Where:** `NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference` and the covered-elements labels.
- **What:** each quote must be found in the document it is cited under; if it is found in another document the
  citation is corrected to that document and page; if nowhere, it is dropped (as today). Labels show the quote's
  page, not the section's first page.
- **Done when:** unit tests with the two wrong citations from this run produce the correct documents.

### Fix 9 - Judgment prompt v10 rules (RC4)
- **Where:** `NdRegulPromptDefaults` (new v10 system / user texts), seeded by `NdAnalysisPromptVersionService`.
- **What (rules, domain-neutral, no document-specific examples):**
  - Clause type first: obligation, prohibition, definition / interpretation, statutory protection, penalty,
    summary, context.
  - Definition / interpretation clauses: covered when the policy states the definition, adopts the law's
    definition by reference, **or applies the concept in practice** (typologies, red flags, risk factors,
    reporting procedures).
  - "Such as / including / not limited to" lists are illustrations of one requirement, not separate requirements.
  - One gap per missing concept; overlapping points are merged into one gap.
  - Each gap names the clause words it comes from and a materiality (high / medium / low).
  - Remove the DIFC, 9.4.1 and similar test-document examples from the prompt.
- **Done when:** checked at Checkpoint B.

### Fix 10 - Gap verification before a gap is shown (RC5)
- **Where:** after judgment in `NdRegulAnalysisProcessor.ExecuteForwardJudgmentAsync` (all entry points);
  new trace step.
- **What:** for every gap: a wider search over all passages of all selected documents using the gap text, the
  clause words it comes from and the concept groups; then one short AI question: "does any passage below cover
  this? quote it". A verified quote turns the gap into covered (or partial) with that evidence; the trace says
  "recovered by verification".
- **Cost:** about $0.02-0.04 per gap with Kimi K3, only for gaps.

**Checkpoint B (one AI run, ~$1-1.5):** run 3.3, 3.5, 3.6. Expected:

| Clause | Expected result |
|---|---|
| 3.3 | Compliant; evidence AML Manual p.42 and p.14 |
| 3.5 | Partial, **one** low-risk gap: adopt the AML-CFT Law definitions of funds and proceeds; asset scope, timeframe and nature of funds covered with p.5, p.22, p.24, p.31, p.58-62 |
| 3.6 | Partial, one low-risk gap: define predicate offence (felony / misdemeanour, dual criminality); CandNM p.2 cited for inside / outside the UAE |
| All | Every quote cited under the right document; no duplicate citations from Document.pdf |

---

## 5. Cost of doing it

| Step | AI calls | Cost (Kimi K3) |
|---|---|---|
| Fixes 1-8 and their checks | none (retrieval check is local) | $0 (+ under $0.01 if Azure embeddings are chosen) |
| Checkpoint A | 3 clause judgments | ~$0.50-1.00 |
| Checkpoint B | 3 clause judgments + gap verification | ~$0.60-1.50 |
| **Total testing** | | **~$1-2.5** instead of one paid run per change |

Running cost after the fixes: per clause ~$0.10-0.20 (smaller, focused context; plus ~$0.02-0.04 per gap for
verification), about the same or lower than today's ~$0.15-0.32.

## 6. Relation to the main plan

V1 brings forward and narrows these points of `REGUL-V5-FIX-PLAN.md`: 10 (passages), 11 (embedding model), 13
(concept groups and self-names), 16 (prompt rules, as interim v10 rules), 17 (gap verification), 18 (eval labels
and retrieval recall) and the retrieval speed task. It adds duplicate-document detection and the per-document
citation check, which the main plan did not have. After Checkpoint B the main plan continues with the clause
profile (14), structured judgment (16 full) and the finalize points (19-22).

## 7. Decisions needed

1. Embedding model for Fix 4: Azure `text-embedding-3-small` (recommended) or local `bge-base`.
2. Duplicate documents (Fix 7): use one silently with a warning (recommended), or block the run until the user
   removes one.
3. OK to store this audit as the expected results (eval labels) for 3.3, 3.5 and 3.6 (Fix 1)?
