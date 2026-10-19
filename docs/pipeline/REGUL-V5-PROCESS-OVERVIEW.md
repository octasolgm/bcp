# How the Compliance Analysis Works

From uploading a document to the final gap report, step by step.

This document describes the regulatory compliance analysis as it works today (pipeline v6). It explains each step,
what it does, and why it is there. The analysis compares each clause of a regulation with the bank's own internal
policy documents, and reports what is covered, what is missing, and what to do about it.

---

## Overview

```
STAGE A  Document preparation (once per document)
  A1 Upload  ->  A2 Read (parse)  ->  A3 Extract sections  ->  A4 Index for search
                                                         \->  A5 Dictionary of terms
  Regulation only: the sections become the list of clauses to analyse

STAGE B  Analysis (for each selected clause)
  Step 1  Expand the clause wording (abbreviations, synonyms, equivalent terms)
  Step 2  Split the clause into its separate parts
  Step 3  Keyword search for each part
  Step 4  Meaning search for each part
  Step 5  Combine both searches
  Step 6  Select the relevant passages
  Step 7  Build the context for the AI
  Step 8  AI judgment
  Step 9  Checks, gap check and save

STAGE C  Review and remediation
  Gap analysis page  ->  Actions and review  ->  Export (Excel / PDF)  ->  Finalize
```

What uses AI and what does not:

| Part | Uses an AI model? |
|---|---|
| Reading documents (A2) | Azure Document Intelligence (document reading service) |
| Sections, dictionary, keyword search, selection, checks | No, rule-based and on our server |
| Meaning search (A4, Step 4) | Embedding model (Azure OpenAI by default) |
| Judgment (Step 8) and gap check (Step 9) | Yes, the AI model set by the administrator |
| Finalize (policy text for resolved actions) | Yes |

---

## Stage A - Document preparation

This runs once for each uploaded document. A document is never read again for each analysis.

### A1. Upload

- The user uploads the regulation (for example CBUAE AML/CFT Guidelines) and the bank's internal policy documents
  (PDF or Word).
- Every document belongs to the bank's own workspace. One bank never sees another bank's documents or results.

### A2. Read the document (parse)

- **What:** Azure Document Intelligence (layout model) reads the file and returns its text, tables and page numbers.
  Scanned pages are read with OCR.
- **Why:** every later step needs clean text with the correct page for each line, so that every quote in the report
  can point to its page.

### A3. Extract sections

- **What:** the text is split into sections by their numbered headings ("7.5 Customer Risk Assessment", "Article 12",
  "Annex 1"). Text before the first heading becomes "Introduction".
- **Clean-up:** page headers and footers that repeat on many pages (such as the guideline title) are removed, so they
  do not appear in the middle of a clause or a policy section.
- **For the regulation:** each section becomes a clause that can be selected for analysis, with its number and title
  exactly as the regulator wrote them.
- **For internal documents:** the sections are kept for page and section references, and for the finalize step.

### A4. Index for search

- **Search passages:** each internal section is also cut into passages of about 150 to 300 words, along its own
  sub-headings and paragraphs. Each passage keeps its heading path, for example
  "AML Manual > Annex 1 > B.18 Other payment technologies".
  - **Why:** a long section (some are over 3,600 words) cannot be searched well as one block; the meaning search only
    reads about the first 380 words. Short passages make every part of a long section findable.
  - The sections themselves do not change. The passages are an extra search index on top of them.
- **Meaning vectors:** each passage gets a "meaning vector" (embedding) so that it can be found by meaning, not only
  by exact words. The default model is Azure OpenAI text-embedding-3-small; a free local model is available as an
  admin option.
- **Background preparation:** passages and vectors are prepared in the background after upload, so an analysis does
  not have to wait for them.

### A5. Dictionary of terms

- **What:** a list of abbreviations (AML = Anti-Money Laundering), synonyms and equivalent terms
  ("timeframe" = "time period" = "duration").
- **Control:** abbreviations found in uploaded documents are inactive until an administrator approves them. An
  abbreviation is used only when its letters match the initials of its full form.

---

## Stage B - Analysis of each clause

The user selects the regulation clauses and the internal documents to compare them with (each document once). The
analysis then runs these steps for each clause. The regulation clause is always used word for word; it is never
summarised or reworded.

### Step 1. Expand the clause wording

- **What:** the clause wording is also searched in alternative forms:
  - abbreviations and their full forms (CDD / customer due diligence);
  - synonyms and equivalent terms (timeframe / time period / duration);
  - the bank's own name: when the regulation says "financial institution", the search also uses the name the bank uses
    for itself in its policies (for example "DIFC" or "UAE"). These names are detected automatically from the selected
    documents. Report abbreviations such as SAR, STR or KYC are never taken as the bank's name.
- **Why:** policies rarely use the regulator's exact words.

### Step 2. Split the clause into parts

- **What:** the clause is split into its separate parts: every paragraph and every list item becomes its own part.
  The sentence that introduces a list ("... defines money laundering as any of the following:") is kept in front of
  each item, so each item keeps its meaning. There is no limit on the number of parts and no text is dropped.
- **Why:** each part is searched on its own. A clause with many requirements searched as one block would blur them
  together. Example: clause 3.5 gives 15 parts.

### Step 3. Keyword search

- **What:** each part is searched by its words across every passage of the selected documents (BM25 scoring).
  Different forms of the same word match ("report", "reported", "reporting"). Very common words are ignored.
- **Why:** finds policy text that uses the same words as the regulation.

### Step 4. Meaning search

- **What:** each part is turned into a meaning vector and compared with every passage's vector.
- **Why:** finds policy text that says the same thing in other words.

### Step 5. Combine both searches

- **What:** each passage gets a keyword score and a meaning score for each part of the clause. The two result lists
  are combined by rank.
- **Why:** a passage found strongly by either search must not be lost.

### Step 6. Select the relevant passages

- **What:** a passage is selected when it stands out clearly from the rest of the documents (at least 2.5 standard
  deviations above the average) for any part of the clause. The best match is always kept. There is no fixed minimum
  or maximum number of passages; relevance decides.
- **Why:** sends the AI all the relevant evidence, and only the relevant evidence.
- **Speed:** Steps 1 to 6 take about 1 to 4 seconds per clause and cost almost nothing.

### Step 7. Build the context for the AI

The AI receives:
- the full regulation clause, word for word;
- the clause's place in the regulation: its parent, neighbouring and sub-clause headings;
- the selected policy passages, each labelled with its document, heading path and page.

### Step 8. AI judgment

- **What:** the AI model reads the clause and the evidence and returns:
  - the status: compliant, partially compliant or non-compliant;
  - the confidence;
  - the covered points, each with a word-for-word quote from the policy;
  - the gaps: what is missing, with the clause words it comes from and a materiality (low, medium, high);
  - a suggested action for each gap.
- **Rules the AI follows (prompt v11):**
  - judge only what the clause states, not more and not less;
  - a term the clause formally defines ("X means ...") is covered only when the policy states that definition or
    adopts the law's definition;
  - a requirement with several conditions (for example size, timeframe and nature of funds) needs evidence for each
    condition;
  - a list of examples counts as one requirement;
  - wording that applies to the bank as a whole covers its staff and representatives.

### Step 9. Checks, gap check and save

Before anything is saved:
- **Quote check:** every quote must be found word for word in the policy document; a quote that is not found is
  removed.
- **Page check:** each quote gets its own page reference, or "page not found".
- **Consistency:** the status must agree with the gaps (a partial result must have a gap and an action).
- **Gap numbering:** gaps are numbered 1, 2, 3 in order and each action stays with its own gap.
- **Gap check (second look at each gap):**
  1. the missing point is searched again, more widely, across every passage of every selected document;
  2. the AI is asked one short question: does any of these passages cover it? Quote it;
  3. the answer counts only if the quote is word for word in that passage;
  4. covered: the gap becomes a covered point with its quote and page. Partly covered: the gap stays, with the
     related policy text shown next to it. Not covered: the gap stays.
  - If a check fails (for example the AI service is unavailable), the gap stays exactly as it was.
  - Admin setting: low-risk gaps can skip this second look to save cost (on by default). Medium and high risk gaps
    are always checked.
- **Save:** the result is saved, together with a step-by-step record of the run (see "Transparency" below).

---

## Stage C - Review and remediation

### Gap analysis page

For each clause the reviewer sees:
- the regulation clause next to the policy extract, with a page link for each quote;
- what the policy covers ("What this reference fulfills");
- the status and confidence;
- each gap with its risk (from the AI's materiality, changeable by the reviewer) and its due date
  (high 15 days, medium 30 days, low 45 days);
- the actions for each gap, each with an owner, a target date and a status.

The reviewer can edit gaps and actions, add actions, and mark them resolved. A gap is resolved when all its actions are
resolved; when every gap of a clause is resolved, the clause becomes compliant automatically.

**Rerun this gap:** when the bank uploads new evidence for a gap, that gap alone can be re-checked against the new
document, without re-running the whole clause.

### Review workflow

The analysis moves through maker and checker stages (prepared, pending review, pending final review, finalized), so
each result is reviewed before it is final.

### Export

- **Excel:** a Gap Analysis sheet (regulation, clause, gaps with risk and status, document references, policy extract,
  status, confidence), an Actions sheet (each action with its gap number, status, priority, target date and owner) and
  a Reviews sheet.
- **PDF:** the same content as a report.
- The export shows the same results as the gap analysis page, including the reviewer's edits and resolutions.

### Finalize

For resolved actions, the AI writes the policy text that closes each gap, and the text is inserted into a corrected
copy of the policy document (a new paragraph in Word documents; a new page after the related page in PDF documents).

---

## Transparency and control

- **Step-by-step report:** each analysis can be downloaded as a report that shows the search time and model, the
  terms used, every part searched, the passages selected, what the AI was given and what it answered, and each gap
  check.
- **Versions:** the analysis pipeline and the AI instructions (prompts) are versioned. Each analysis records which
  versions and which AI model produced it, and an administrator can switch back to an earlier version.

| Pipeline version | What it added |
|---|---|
| v1 | Original analysis |
| v2 | Search also with abbreviations and synonyms swapped in |
| v3 | Complete clause split (nothing dropped) and relevance-based selection (no fixed limits) |
| v4 | Search passages with heading paths, and equivalent terms |
| v5 | Gap check: a second look at every gap before it is reported |
| v6 | Word forms match, and the bank's own name is searched (current) |

- **Search check:** expected evidence can be saved for a clause, and the search can be tested on its own (no AI
  cost) to confirm it finds that evidence.
- **Data separation:** every document, result and setting belongs to one bank's workspace.

---

## Limits to keep in mind

- Each document should be selected once. If the same document is uploaded twice under two names, quotes may cite
  either copy.
- The result is a draft for the compliance team. Every quote links to its source so it can be confirmed.
- A gap check that cannot run (for example the AI service limit is reached) keeps the gap unchanged; the clause can be
  analysed again later.
