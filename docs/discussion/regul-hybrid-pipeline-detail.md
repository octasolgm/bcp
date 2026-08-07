# Regul Hybrid — Analysis Run (step by step)

**Proposed** forward-only pipeline. Not shipped yet.

**Main idea:** For each **selected gov clause**, find the best **internal policy sections** (not full markdown), then ask the LLM to judge compliance.

![Pipeline overview](regul-hybrid-pipeline-detail.png)

---

## What you select in the UI

| You pick | Stored as | Used for |
|----------|-----------|----------|
| Regulation document + **checked clauses** | `regulation_points` | **Query side** — one clause at a time |
| **Internal policy file(s)** | `nd_internal_document_sections` | **Search side** — extracted sections |

**Hybrid uses section extract only** — not full markdown (that is V4).

---

## Quick map

| Step | Gov clause | Internal sections | Cost |
|------|------------|-------------------|------|
| 0. Load indexes | — | All sections loaded | $0 |
| 1. Query expansion | **Only input** | **No** | FREE |
| 2. Sub-obligation split | **Only input** | **No** | FREE |
| 3. BM25 retrieve | Expanded query | **Scores all** sections | FREE |
| 4. Embedding retrieve | Clause meaning | **Scores all** sections | ~$0.00003/clause |
| 5. Hybrid fusion | — | Merges both top-100 lists | FREE |
| 6. Adaptive select | — | Picks 15–56 sections | FREE |
| 7. Build context | Clause text | Selected excerpts | FREE |
| 8. LLM judgment | Clause text | Selected sections | ~$0.05/clause |
| 9. Save | Finding | — | FREE |

**80 clauses ≈ $4.50** · Prep once ≈ **$1.85**/file (cached on re-run)

---

## BM25 and Embedding — parallel, not sequential

**Important:** Embedding does **not** re-rank BM25 results. They are **two independent retrieval paths** that run **at the same time**, then merge.

```
                    Gov clause (after steps 1–2)
                              │
              ┌───────────────┴───────────────┐
              ▼                               ▼
     ┌─────────────────┐             ┌─────────────────┐
     │ 3. BM25         │             │ 4. Embedding    │
     │ keyword path    │             │ semantic path   │
     │ scores ALL      │             │ scores ALL      │
     │ sections        │             │ sections        │
     │ → top ~100      │             │ → top ~100      │
     └────────┬────────┘             └────────┬────────┘
              │                               │
              └───────────────┬───────────────┘
                              ▼
                    5. Hybrid fusion
                    (merge + combine scores)
```

| Path | How it finds sections | Good at | Weak at |
|------|----------------------|---------|---------|
| **BM25** | Exact / rare **words** in text | *CDD*, *audit*, *Rule 9.4* | *independent audit* vs *internal audit* |
| **Embedding** | **Meaning** similarity | synonym / different wording | rare exact tokens if meaning is vague |

**Fusion** takes the **union** of both top-100 lists and combines scores so a section strong on only one path can still win (e.g. 9.4.1 via embedding even if BM25 ranked it #40).

---

## Two sides — who feeds what

```
GOV SIDE (blue)                    INTERNAL SIDE (green)
Selected gov clause                Extracted sections (all, once)

Steps 1–2: gov ONLY ──►            Step 0: load all sections
Steps 3–4: BOTH paths score ──────► all sections (parallel)
Steps 5–9: merge + pick + LLM ────► selected sections only
```

**Query expansion does NOT read internal sections.**

---

## Step 0 — Load indexes

| | |
|--|--|
| **Input** | Selected internal doc(s) — all extracted sections |
| **How** | Load section text + BM25 index + section embedding vectors from DB (built at prep) |
| **Output** | In-memory corpus ready for steps 3–4 |
| **Cost** | $0 |

---

## Step 1 — Query expansion (FREE)

| | |
|--|--|
| **Input** | **Gov clause text only** |
| **How** | Lookup table (not AI). Scan clause for known terms; add related search words |
| **Output** | Expanded term list for BM25 path |

**Three expansion types:**

| Type | Example |
|------|---------|
| Synonym | *independent audit* → *internal audit*, *audit function* |
| Acronym expand | *CDD* → *customer due diligence* |
| Acronym collapse | *customer due diligence* → *CDD* |

**§8.4 example:** adds *internal audit*, *AML*→*anti-money laundering*, *CFT*→*counter financing of terrorism*

**Does not use:** internal sections, full markdown

---

## Step 2 — Sub-obligation split (FREE)

| | |
|--|--|
| **Input** | Gov clause text (+ expanded terms) |
| **How** | Rule-based split on numbered items, *shall* bullets, or conjunctions for dense clauses |
| **Output** | 1–5 sub-queries (e.g. audit frequency, scope, subsidiaries) |
| **Why** | Each sub-query runs BM25 + embedding; results merged before fusion |

**§8.4 example:** 3 sub-obligations → 3 BM25 searches → scores merged → one top ~100 list

---

## How expansion + sub-obligations work together (BM25)

One gov clause can produce **many expanded terms** and **many sub-obligations**. BM25 uses **both**, like this:

```
Step 1 — expand FULL clause once
  → global terms: internal audit, AML, anti-money laundering, …

Step 2 — split into sub-obligations
  → A: audit frequency
  → B: audit scope
  → C: subsidiary coverage

Step 3 — BM25 runs ONCE PER sub-obligation
  → Query A = sub-obligation A text + global expanded terms
  → Query B = sub-obligation B text + global expanded terms
  → Query C = sub-obligation C text + global expanded terms
  → Each query scores ALL internal sections
  → Merge: per section keep MAX score (or sum) across A, B, C
  → Sort merged list → top ~100
```

| Case | Sub-obligations | BM25 runs |
|------|-----------------|-----------|
| Simple clause (short) | **1** (= whole clause) | **1** search |
| Dense §8.4 | **3–5** | **3–5** searches, then merge |

**Does BM25 use expansion or sub-obligations?** → **Both.**  
Each BM25 query = **one sub-obligation** + **clause-level expanded terms** (synonyms + acronyms from step 1).

Optional: run acronym/synonym lookup again **per sub-obligation** if that sub-text has extra terms not in the full clause.

**Embedding path (step 4)** is different: usually **one embed of the full clause** (whole meaning), not per sub-obligation — then parallel with merged BM25 at fusion.

---

| | |
|--|--|
| **Input** | **Per sub-obligation:** sub-obligation text + clause expanded terms · **all** internal sections |
| **How** | 1. For each sub-obligation: build query (sub-text + global expansion) 2. BM25 score all sections 3. **Merge** scores (max per section) 4. Sort → **top ~100** |
| **Output** | `bm25_top_100` — ranked list with BM25 scores |
| **Independent of** | Embedding path — runs in **parallel** |

**How scoring works (simple):**

- Section mentions more query terms → higher score  
- Rare terms (e.g. *subsidiary*, *9.4.1*) weigh more than common words (*the*, *shall*)  
- Section ref / label matches count extra  

**§8.4 example:**

| Section | BM25 rank | Why |
|---------|-----------|-----|
| Rule 9.4.1 | #2 | *internal audit*, *AML programme* |
| Rule 9.4.2 | #8 | related audit text |
| Rule 1.0 intro | #45 | weak — only *audit* once |

---

## Step 4 — Embedding retrieve (~$0.00003/clause) — semantic path

| | |
|--|--|
| **Input** | Gov clause text (original meaning) · **all** internal sections |
| **How** | 1. Embed gov clause → vector 2. Compare to **every** precomputed section vector (cosine similarity) 3. Sort desc 4. Keep **top ~100** |
| **Output** | `embed_top_100` — ranked list with similarity scores |
| **Independent of** | BM25 path — does **not** wait for BM25 results |

**Why parallel:** BM25 might rank 9.4.1 at #40 (few shared keywords) but embedding ranks it **#1** (same meaning). Fusion catches that.

**§8.4 example:**

| Section | Embed rank | Why |
|---------|------------|-----|
| Rule 9.4.1 | #1 | *independent audit function* ≈ *internal audit testing* |
| Rule 1.0 intro | #60 | generic — demoted |

**Cost:** one small embedding API call per clause (section vectors precomputed at prep).

---

## Step 5 — Hybrid fusion (FREE)

| | |
|--|--|
| **Input** | `bm25_top_100` + `embed_top_100` |
| **How** | 1. **Union** both lists (up to ~150 unique sections) 2. Normalize BM25 and embed scores 0–1 3. `hybrid = 0.4 × bm25 + 0.6 × embed` 4. Re-sort by hybrid score |
| **Output** | Single merged ranked list |

**Example — Rule 9.4.1:**

| Path | Rank | Normalized score |
|------|------|------------------|
| BM25 | #2 | 0.85 |
| Embed | #1 | 0.92 |
| **Hybrid** | **#1** | 0.4×0.85 + 0.6×0.92 = **0.89** |

Section only in one list gets 0 for the missing path (or min score) — still can place if the other path is very strong.

---

## Step 6 — Adaptive selection (FREE)

| | |
|--|--|
| **Input** | Merged hybrid-ranked list |
| **How** | Walk list top-down; add sections until score &lt; threshold **or** token budget (~28k) hit |
| **Output** | **15–56 sections** for this clause |

| Clause type | Typical count |
|-------------|---------------|
| Simple | 8–15 |
| Normal | 20–35 |
| Dense §8.4 | 35–56 |

Not fixed top-25 — adapts to clause density.

---

## Step 7 — Build context (FREE)

| | |
|--|--|
| **Input** | Selected sections from step 6 |
| **How** | Format each as `[Manual — Rule X p.N]\n<text>` |
| **Output** | `policy_context` block for LLM prompt |

---

## Step 8 — LLM judgment (~$0.05/clause)

| | |
|--|--|
| **Input** | Gov clause + `policy_context` (15–56 sections) |
| **How** | Admin prompt `regul_judgment_*` → design/operating/overall status, quotes, gap |
| **Output** | JSON judgment result |

Main cost of the pipeline.

---

## Step 9 — Post-process & save (FREE)

| | |
|--|--|
| **Input** | LLM output + full section source text |
| **How** | Verify quotes · retry gap if needed · write `regul_forward_findings` |
| **Output** | Saved finding → **next clause** (loop to step 1) |

---

## §8.4 full walk-through

| Step | Result |
|------|--------|
| 1 Expansion | + *internal audit*, *AML*→*anti-money laundering* |
| 2 Sub-split | 3 sub-obligations |
| 3 BM25 (parallel) | top: 9.4.1 #2, 9.4.2 #8, intro 1.0 #45 |
| 4 Embed (parallel) | top: 9.4.1 #1, intro 1.0 #60 |
| 5 Fusion | 9.4.1 wins #1 |
| 6 Select | ~25 sections incl. 9.4.1 |
| 8 LLM | **Compliant** — cites Rule 9.4.1 |

---

## Hybrid vs V3 vs V4

| | Retrieval | LLM gets |
|--|-----------|----------|
| **Hybrid** | BM25 ∥ Embedding → fusion | 15–56 sections |
| **V3** | Keyword only | Top 20 sections |
| **V4** | None | Full markdown |

---

*See also: [V2 precompute](regul-hybrid-pipeline-v2.md) · [Overview](REGUL-HYBRID-RETRIEVAL-WORKFLOW.md)*
