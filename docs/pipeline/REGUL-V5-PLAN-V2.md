# Regul V5 - Plan V2

The next tasks for the V5 analysis (`/nd/analyse-regul-full-v2`), in simple words: what is wrong today, what changes,
where it changes, and which problem it solves. Work already done is in `REGUL-V5-WORK-LOG.md` (tasks T1-T30).

---

## 0. Ground rules for every task

1. **The regulation clause is never summarised or reworded.** The AI always receives the full clause text, word for
   word. Anything that lists the points of a clause uses the clause's own exact words (copied, not paraphrased).
2. **Extraction and chunking do not change.** Structural sections stay as they are; search passages stay as they are.
3. **Demo accounts are not touched.**
4. **Nothing is replaced, everything is switchable.** The new judgment comes as **pipeline v6** and **prompt v12**.
   v5 / v11 stay available, so the same clause can be run on both and compared.
5. **Checked before you pay for a run:** every change is first scored against the answer keys (task 4) for 3.3, 3.5
   and 3.6.

## 0.1 Words used in this plan

| Word | Means |
|---|---|
| Clause | The regulation text being checked (e.g. 3.5 Money Laundering). Always sent whole. |
| Requirement (point) | One thing the clause asks for, written with the clause's own words (e.g. "the timeframe ... is irrelevant to the suspicion and reporting"). 3.5 has about 8. |
| Passage | A piece (~150-300 words) of **your internal policy documents**, found by the search and sent to the AI as evidence. Not the clause. |
| Evidence | The passages that support one requirement, each with its document and page. |
| Gap check | The second search and short AI question per gap (built, pipeline v5). |

---

## 1. Task list

| # | Task | Problem it solves | Where it changes | Extra AI cost | Effort | Your answer |
|---|---|---|---|---|---|---|
| 1 | Page reference next to the right quote | Some quotes show the wrong page or none | Step 9 (saving the result), Policy extract on the gap report | none | 0.5 day | Approved |
| 2 | Keyword search understands word forms | "report / reported / reporting" treated as different words | Step 3 (keyword search) | none | 0.5 day | Approved |
| 3 | The bank's own name for itself | Clause says "financial institutions", policy says "DIFC" / "UAE" / "the Bank" | Step 1 (term expansion), dictionary page | none | 1.5 days | Approved |
| 4 | Answer keys and automatic scoring | Every change is checked by hand against the PDFs; one fix can break another clause (prompt v10) | Evals (admin), pipeline report | none | 1.5 days | Explained below, needs OK |
| 5 | Requirement list per clause, saved and reused | The AI splits the clause into points differently on every run | New step after Step 2 | about $0.02-0.05 once per clause | 2.5 days | Explained below, needs OK |
| 6 | Evidence searched per requirement | ~120 passages for the whole clause in one mixed list | Steps 3-7 | lower (about half the tokens) | 1.5 days | Explained below, needs OK |
| 7 | AI judges each requirement; code only does the counting | Status, gaps, actions and numbering can disagree | Step 8 (prompt v12), Step 9, gap check | same or lower | 2.5 days | Approved, explained below |
| 8 | Finalize writer reads the bank's own policy section | Inserted wording can read like a template | Finalize | unchanged | 0.5 day | Clarified below, needs OK |
| 9 | Inserted text placed where the evidence is | Placement in the corrected document is guessed | Finalize | none | 1 day | Needs OK |
| 10 | Corrected copy as DOCX for PDF policies | No editable corrected document for PDF sources | Finalize | none | 2 days | Needs OK |
| 11 | Re-check after finalizing | Fixed gaps stay open until a full re-run | Finalize, indexing | one clause check per finalized clause | 1.5 days | Needs OK |
| 12 | Housekeeping | Old failing tests, Azure DI price, passage count | Tests, settings | none | 1 day | - |

**Decided not to do:** duplicate-document handling. Document.pdf and the Implementation Manual are the same file under
two names, and both were selected for the analysis. Selecting it once fixes it; no code. (Selecting it twice sends
every passage twice and doubles that part of the cost, but it is not wrong.)

---

## 2. Tasks in detail

### Task 1 - Page reference next to the right quote

- **Problem today (3.5, latest run):** the Policy extract shows "Purchase of valuable commodities" under AML Manual
  **p.62** (it is on p.58-59), the crypto typology under "Implementation Manual section 32, **p.3**" (it is AML p.62 /
  Implementation p.50), and the last quote with no page.
- **Why:** the system finds the right page for every quote, then removes repeated pages from the list, and skips a
  quote whose page it cannot find. The page list becomes shorter than the quote list, and the screen pairs them by
  position, so from that point every page moves up by one. The pages themselves are real pages (that is why the text
  you checked was there); they are just next to the wrong quote.
- **Change:** keep one page reference per quote, in the same order; a quote whose page is not found shows "page not
  found" instead of shifting the others.
- **Where:** `NdRegulJudgmentPostProcessor.ApplyGroundedDocumentReference` (Step 9); the gap report reads the pairs.
- **Applies to:** all runs from now on (it is a display bug), new results only.

### Task 2 - Keyword search understands word forms

- **Problem today:** keyword search compares exact words. "report" in the clause does not match "reported" or
  "reporting" in the policy; "suspicious" does not match "suspicion".
- **Change:** both sides reduced to the word root before keyword scoring (report / reported / reporting -> report);
  words found in almost every AML passage ("shall", "bank", "policy") count less.
- **Where:** `Bm25Scorer` (Step 3), pipeline v6 only.
- **Solves:** evidence written in another grammatical form is found by keyword search too.

### Task 3 - The bank's own name for itself

- **Problem today:** the clause says "financial institutions" / "the institution"; your policies say "DIFC is
  protected ...", "UAE is obliged to report ...", "the Bank". Only a prompt rule covers this now.
- **Change:** each workspace's own names are detected from its documents (the name used as the subject of duties:
  "X shall report", "X is obliged to") and listed on the dictionary page, where an admin confirms them. Confirmed names
  are treated as equivalent to "the institution / financial institution / the bank" in the search.
- **Where:** `DictionaryExpansionService` (Step 1), dictionary page, one setting per workspace.
- **Solves:** 3.3-type evidence ("DIFC is protected from any criminal, civil or administrative liability") is found by
  the search, for any client bank, not only this one.

### Task 4 - Answer keys and automatic scoring (build this first)

- **In simple words:** an answer key is the correct answer for a test clause, written once: its status, which points
  are covered and on which page, which gaps it should have. After every run the system compares the result with the
  answer key and shows a score, the same way a teacher marks an exam.
- **Example (3.5 answer key):** status partial; covered: money laundering acts (AML p.6), independent offence (AML p.6,
  CandNM p.2), any kind of asset (typologies), amount irrelevant (p.4); gaps: "funds" definition, "proceeds"
  definition, timeframe (partly addressed p.22 / p.24).
- **Score shown after the run:** status right / wrong; gaps found 3 of 3; extra gaps 0; expected pages sent to the AI
  5 of 5.
- **Problem it solves:** today every change is checked by me reading your PDFs and by you paying for runs. Prompt v10
  passed my checks and still broke 3.5. With answer keys, a change is scored on 3.3, 3.5, 3.6 (and more clauses later)
  before it reaches you, and v5 vs v6 can be compared with numbers.
- **Where:** the Evals area (the free retrieval check already stores expected snippets per clause; this adds expected
  status, points and gaps), and a "Score" section in the pipeline report.
- **Cost:** none (no AI in the scoring).

### Task 5 - Requirement list per clause, saved and reused

- **How it works today:** each new analysis sends the whole clause to the AI, and the AI, inside the same answer,
  decides by itself how to break the clause into points. Nobody sees that list before the result, and it changes from
  run to run. On 3.5 we saw it: one run had 4 points (definitions merged into "broad meaning of funds"), the next 6,
  the next 5 with the definitions split; the gap numbers followed the AI's own numbering ([2], [4]) and broke the
  actions (T21).
- **Change:** the first time a clause is analysed, one AI call lists its requirements. Each requirement is **a copy of
  the clause's own words** (no summary), with its type (duty, defined term, scope, background). The list is saved, shown
  on the clause, and can be edited by an admin. Every later analysis of that clause (any bank, any run) uses the same
  list.
- **What does not change:** the AI still receives the whole clause text in every judgment. The list only tells it which
  points to answer, so every run answers the same points in the same order.
- **Where:** new step after Step 2 ("Step 2b - Requirements"), new table for the saved lists (per regulation document,
  workspace-scoped), shown in the pipeline panel and on the clause.
- **Solves:** the same points and numbering on every run; no merged or invented points; results can be compared between
  runs.
- **Cost:** about $0.02-0.05 once per clause; free afterwards.

### Task 6 - Evidence searched per requirement

- **How it works today:** the clause is split into parts and every part is searched, but all results end up in one list
  for the whole clause: on the last 3.5 run, **120 passages of your policy documents** (about 39k tokens) in score
  order. The AI has to work out which passage supports which point.
- **Change:** each requirement (task 5) is searched with its own clause words, and the AI receives the evidence
  **grouped under each requirement**: "Requirement 6 (timeframe irrelevant): passages E1 Implementation p.22, E2
  Implementation p.24 ...". Each passage has an id that points to its exact document and page.
- **What does not change:** the clause is still sent whole; the search itself (keywords + meaning + equivalent terms) is
  the same as now.
- **Where:** Steps 3-7 (search and context building), pipeline v6.
- **Solves:** the right evidence sits next to each point; fewer tokens (about half); pages come from the passage id, so
  the page next to a quote is always right.

### Task 7 - The AI judges each requirement; the code only does the counting

- **Your concern:** "the AI is more intelligent than our own code." Agreed, and the AI keeps **every** judgment of
  meaning. The code never decides whether a policy covers a requirement.
- **What the AI does (prompt v12):** for each requirement: covered / partly covered / not covered, the evidence id, the
  exact quote, and the draft policy wording for what is missing.
- **What the code does (only bookkeeping, where the AI made mistakes):**
  - status: all covered = compliant; some = partial; none = non-compliant (v10 once said "compliant" with points
    missing);
  - one gap per not-covered requirement and one action under it, numbered 1, 2, 3 (the AI's numbering broke the
    actions, T21);
  - page next to each quote from the evidence id (task 1);
  - materiality from the requirement type (defined term = low, core duty = high), so risk is consistent.
- **Also:** the gap check runs per requirement against that requirement's evidence, and a very long context is split
  into several calls (today only flagged).
- **Where:** Step 8 (prompt v12), Step 9 (saving), gap check; pipeline v6.
- **Solves:** status, gaps, actions and numbering always agree; each answer is traceable to a requirement and a page.

### Task 8 - Finalize writer reads the bank's own policy section

- **Clarification:** this task does **not** touch or summarise the regulation clause. It is about the text we insert into
  **your policy** when a gap is finalized.
- **Problem today:** the writer gets the gap and the action, but not the policy section it is writing into, so the
  inserted wording can sound generic.
- **Change:** the writer also receives the target section of your policy (word for word) and the bank's own terms
  ("UAE", "MLRO", "the Compliance Department"), so the new sentence fits the manual.
- **Where:** finalize (corrected copy). **Your decision:** keep or skip.

### Task 9 - Inserted text placed where the evidence is

- **Problem today:** the place where the new text goes into the corrected document is guessed from the section name.
- **Change:** use the passage the requirement's evidence came from (task 6) to place the text in that section.
- **Where:** finalize.

### Task 10 - Corrected copy as DOCX for PDF policies

- **Problem today:** for a PDF policy the corrected copy is not an editable Word file with the changes marked.
- **Change:** produce a DOCX of the policy text with the inserted wording marked.
- **Where:** finalize, download.

### Task 11 - Re-check after finalizing

- **Problem today:** after a gap is fixed in the corrected copy, the clause still shows the gap until a new full run.
- **Change:** the corrected copy is indexed as the new version of the policy and only the finalized clauses are checked
  again; fixed gaps close by themselves.
- **Where:** finalize, indexing.

### Task 12 - Housekeeping

- Fix the 21 old failing unit tests (failing before this work).
- Set the Azure Document Intelligence price per page once the invoice rate is confirmed (log only).
- Passage count: with the duplicate file selected once and tasks 5-6, the evidence per clause should drop well below
  120; re-tune the selection only if the scores in task 4 say so.

---

## 3. Order of work

| Step | Tasks | Why this order |
|---|---|---|
| 1 | Your runs: 3.5 (report), 3.3, 3.6 on the current code; dictionary clean-up; duplicate file selected once | Confirms T28-T30 and gives the starting scores |
| 2 | Task 1 (page references), Task 4 (answer keys) | Page fix is small and visible; answer keys must exist before the bigger changes |
| 3 | Tasks 2 and 3 (search) | Small, free, scored with task 4 |
| 4 | Tasks 5, 6, 7 together as pipeline v6 + prompt v12 | One feature; compared with v5 / v11 on the answer keys before you run it |
| 5 | Tasks 8-11 (finalize), after your OK | Uses the evidence ids from task 6 |
| any | Task 12 | - |

**Total:** about 12-15 working days for tasks 1-11.

---

## 4. Decisions needed from you

| Task | Question |
|---|---|
| 4 | OK to build answer keys and scoring first? |
| 5 | OK to save a requirement list per clause (clause's own words, editable, reused)? |
| 6 | OK to search and send evidence per requirement? |
| 8 | Keep or skip the finalize writer change (it never touches the regulation text)? |
| 9-11 | Which finalize tasks you want |
