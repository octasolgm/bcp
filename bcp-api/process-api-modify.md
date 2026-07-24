# process-api-modify — Prompt change suggestions (AML/CFT sample docs)

Based on:
- Gov: `amlcft cb uae decision.pdf`, `AMLCFT LAW.pdf`
- Internal: `Internal A M L M a n u a l 290626.pdf`, `internal -Implementation of AML CFTPF Manual.docx`
- Manual benchmark: `manul gap analysis document.xlsx`

---

## Why results differ from manual (root causes)

| Manual gap analysis does | Current system does |
|---|---|
| Compares **CB UAE Guidelines clauses** (`3.2`, `3.4`, `3.6`…) | Extracts points from uploaded PDF(s) — numbering may be `Article 15`, `2.1.1`, or mixed |
| Uses **2 internal manuals** (DIFC Implementation Manual + UAE Branch Manual) | Usually 1 internal PDF in one analysis run |
| **Document Reference** = short compliance narrative + doc name + page/section | One short `Output/Response` quote only |
| **Policy Extract** = long verbatim internal text | `fulfilled_clauses` bullets (often short / None) |
| Maps gov clause → internal section (e.g. `3.2` → `7.28`) | No crosswalk instruction in prompt |
| Section-level comparison (whole `3.2` block) | Leaf-point filter may compare `3.2.1` sub-clauses or skip parent `3.2` |
| Cites printed manual pages (`Page 10`, `section 7.28`) | Prompt requires PDF viewer page index only |

---

## Step 2: Extract gov points

### Existing prompt (schema instruction)

```json
"point_id": "Official numbering such as 2.6.5, Article 3, TFS-REQ-03"
"description": "Every numbered government or regulatory requirement clause extracted from the document"
```

No free-text prompt. Input = full regulation markdown only.

### Problem vs manual

- Manual uses **Guidelines section IDs** (`3.2 Confidentiality and Data Protection`) with law refs in header.
- Extract may produce **wrong IDs** from LAW vs Decision vs Guidelines.
- Sub-clauses split too deep (`3.2.1`, `3.2.2`) while manual compares **whole section 3.2**.
- `point_type` not used downstream — intro/definition points may enter analysis.

### Suggested changes

**Change 1 — Primary regulation source**

```
ADD to schema description (points.items.properties.point_id):
"Use CB UAE AML Guidelines section numbering as primary ID when present (e.g. 3.2, 3.4, 3.10).
If document is AML-CFT Law, prefix with 'Law Article X'. If AML-CFT Decision, prefix with 'Decision Art X'.
Do not invent alternate numbering."
```

**Change 2 — Section-level leaf points (match manual granularity)**

```
ADD to schema description (points array):
"Create one point per main Guidelines section (e.g. 3.2, 3.4), not per bullet sub-item,
unless the sub-item is a standalone enforceable obligation with its own number (e.g. 3.2.1 only if explicitly numbered in source)."
```

**Change 3 — Title + legal cross-reference**

```
ADD required-ish fields in schema:
"title": include section heading (e.g. 'Confidentiality and Data Protection')
"section": include legal cross-refs from header (e.g. 'AML-CFT Law Article 15; AML-CFT Decision Articles 17.2, 21.2')
```

**Change 4 — Which gov file to extract from**

```
PROCESS (not prompt): extract from `amlcft cb uae decision.pdf` (Guidelines) as primary gov source for analysis.
Use AMLCFT LAW.pdf only for cross-reference, not as separate competing point list.
```

---

## Step 4: Analysis Phase 1 — Landing AI compare

### Existing prompt

```
You are an expert automated regulatory compliance auditor specializing in CBUAE and TFS frameworks...
- Compare by regulatory meaning and operational effect.
- cite evidence as: Page [X], Section [Y]: 'verbatim internal quote'
- Page [X] MUST be the 1-based PDF file page index...
- If Non-Compliant, uae_response_compliance_level must be exactly: No corresponding procedure found.
...
ATTACHED INTERNAL PROCESS DOCUMENT ({filename} — parsed markdown...)
REQUIREMENT POINT TO CHECK:
{point_number} {point_title}
{point_content}
```

### Existing schema (output)

```
uae_response_compliance_level: Page [Number]... Section [Header Code]: '[Verbatim quote]'
fulfilled_clauses: bullet mapping
corrective_action_plan: Gap(s): ...
```

### Problem vs manual

- Prompt says **TFS** — your manual is **AML/CFT CB UAE**, not TFS.
- Only **one internal markdown** — manual searches **DIFC Implementation Manual (7.28)** AND **UAE Branch Manual (page 25–26)**.
- Manual **Document Reference** column has no equivalent field in schema.
- Manual expects **long Policy Extract** — schema allows short quote in `uae_response_compliance_level` only.
- Internal section numbers (`7.28`) not requested in output.
- PDF viewer page vs **printed page 10** — manual uses printed pages; model gets confused.
- DOCX implementation manual **not in parse pipeline** if only PDF uploaded.

### Suggested changes

**Change 1 — Framework wording**

```
REPLACE:
"specializing in CBUAE and TFS frameworks"
WITH:
"specializing in UAE AML/CFT — CBUAE AML Guidelines, AML-CFT Law, and AML-CFT Decision"
```

**Change 2 — Multi-manual search instruction**

```
ADD after Rules:
"- Search ALL attached internal documents (DIFC Implementation Manual, UAE Branch AML Manual, Group AML Manual) before concluding Non-Compliant.
- A requirement is Compliant if ANY attached document fully addresses it.
- Cite which document name matched (e.g. 'Implementation of AML CFTPF Manual' or 'AML/CTF/PF UAE Branch Manual')."
```

**Change 3 — Match manual column structure in output**

```
ADD to schema properties:

"document_reference": {
  "type": "string",
  "description": "Short compliance narrative like manual 'Document Reference' column. Example: 'Bank complies; covered in Implementation of AML CFTPF Manual section 7.28, Page 10.'"
}

"policy_extract": {
  "type": "string",
  "description": "Long verbatim internal policy text addressing the requirement (manual 'Policy Extract' column). Include full relevant paragraph(s), not one sentence."
}

RENAME usage:
- uae_response_compliance_level → keep as short citation line OR merge into document_reference
- fulfilled_clauses → require mapping each gov sub-obligation to internal control
```

**Change 4 — Section crosswalk**

```
ADD to prompt INPUT section:
"For each requirement, first locate internal section numbers that mirror or address the gov clause (e.g. gov 3.2 ↔ internal 7.28). Search markdown for section headers matching those numbers."
```

**Change 5 — Page citation rule**

```
REPLACE page rule WITH:
"- Cite BOTH when available: (1) internal document section number (e.g. Section 7.28), (2) PDF page — prefer printed page number shown in document text if present, else PDF viewer page index.
- Format: '[Document Name], Section [X.Y], Page [N]: \"verbatim quote\"'"
```

**Change 6 — Partial vs Compliant threshold (align manual)**

```
ADD:
"- Manual benchmark treats explicit internal prohibition/policy list covering all gov bullets as Compliant.
- Partial Compliant only when some but not all sub-obligations in the gov clause are addressed.
- Do not mark Non-Compliant if a long internal policy extract clearly covers the same obligations with different wording."
```

**Change 7 — Compare prompt template (full suggested replacement)**

```
You are an expert UAE AML/CFT compliance auditor. Compare the ENTIRE regulatory requirement (all sub-obligations in the clause) against ALL attached internal policy documents.

Rules:
- Framework: CBUAE AML Guidelines + AML-CFT Law + AML-CFT Decision (not TFS).
- Search every attached internal document before concluding Non-Compliant.
- Match by regulatory intent and operational control — not keyword overlap.
- For each sub-obligation in the gov clause, find internal text that satisfies it.
- Output document_reference (short narrative), policy_extract (long verbatim internal text), and comply_status.
- Citation format: [Document Name], Section [number], Page [N]: "verbatim quote"
- If no internal text addresses any sub-obligation after searching all documents: Non-Compliant + "No corresponding procedure found."

---
INPUT DATA:
[internal documents markdown — all manuals]
REQUIREMENT POINT TO CHECK:
[clause id + title + full clause text + law cross-refs]
```

---

## Step 5: Dual verify Pass 2

### Existing prompt

```
DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)
You are the second verifier. Landing AI (Pass 1) already analyzed this requirement.
Re-read the attached internal PDF(s) and produce your own assessment.
LANDING AI PASS 1 (reference only): ...
REQUIREMENT POINT TO CHECK: ...
INTERNAL DOCUMENT MARKDOWN: ...
```

### Problem vs manual

- Pass 2 has **no output schema** — free text may not match Pass 1 structure → agreement failures.
- Pass 2 may **contradict** Pass 1 when Pass 1 found correct multi-doc evidence.
- No instruction to validate **policy_extract length/quality** like manual.
- Pass 2 does not know manual uses **Document Reference + Policy Extract** pattern.

### Suggested changes

**Change 1 — Same schema as Pass 1**

```
ADD:
"Return the SAME JSON schema as Pass 1 (document_reference, policy_extract, comply_status, fulfilled_clauses, corrective_action_plan).
Do not output prose only."
```

**Change 2 — Verification focus**

```
REPLACE opening WITH:
"You are Pass 2 verifier. Independently search all attached internal PDFs for evidence addressing EVERY sub-obligation in the gov clause.
Your job is to confirm or correct Pass 1 — not to re-score with different standards.
If Pass 1 policy_extract is accurate and complete, align with Compliant."
```

**Change 3 — Multi-doc**

```
ADD:
"Search Implementation Manual, Branch Manual, and Group AML Manual. Compliant if any document satisfies the clause."
```

---

## Step 1 / Step 3: Parse (no prompt today)

### Problem vs manual

- `Internal A M L M a n u a l 290626.pdf` — spaced letters may reduce parse quality.
- `internal -Implementation of AML CFTPF Manual.docx` — **not parsed** unless converted to PDF and uploaded.
- Manual relies heavily on **DIFC Implementation Manual section 7.x** — if that content is only in DOCX, analysis will miss it.

### Suggested process (not prompt)

```
- Upload ALL internal sources as PDF before analysis:
  1) Internal AML Manual
  2) Implementation of AML CFTPF Manual (convert DOCX → PDF)
  3) UAE Branch Manual (if separate from Internal AML Manual)
- Attach all 3 internal docs in Analysis v8 (multi-doc mode)
- Use `amlcft cb uae decision.pdf` as sole gov extract source for Guidelines clauses 3.x
```

---

## Schema: compliance-comparison-v2 — suggested field changes

| Field | Existing | Change to |
|---|---|---|
| `requirement_id` | clause number | include title: `3.2 Confidentiality and Data Protection` |
| `uae_response_compliance_level` | short quote | short citation OR merge into `document_reference` |
| *(new)* `document_reference` | missing | manual column 4 — compliance narrative + doc + section |
| *(new)* `policy_extract` | missing | manual column 5 — long verbatim internal text |
| `fulfilled_clauses` | optional bullets | require one bullet per gov sub-obligation |
| `comply_status` | Compliant / Partial / Non-Compliant | keep — align rules with Change 6 above |
| `corrective_action_plan` | Gap(s): ... | only when Partial/Non-Compliant; reference missing sub-obligations by number |

---

## Priority order (what to change first)

1. **Upload/process**: all internal manuals + correct gov PDF (`cb uae decision`) + convert DOCX to PDF  
2. **Step 4 prompt**: remove TFS, add multi-doc search, add section crosswalk, match manual columns  
3. **Step 4 schema**: add `document_reference` + `policy_extract`  
4. **Step 2 schema**: Guidelines section IDs (`3.2`) at section level, not over-split  
5. **Step 5 prompt**: same JSON schema + multi-doc + confirm Pass 1 when evidence is complete  

---

## Example — manual row 1 vs what system should produce

**Manual gov clause:** `3.2 Confidentiality and Data Protection` (+ law refs)

**Manual Document Reference:**  
`Bank UAE now complies... Implementation of AML CFTPF Manual (page 30 section 7.28), PAGE 10...`

**Manual Policy Extract:**  
Full `7.28 Confidentiality and Prohibition against Tipping Off` text (multi-paragraph)

**System should output (after prompt change):**

```
document_reference: Bank complies; STR confidentiality covered in Implementation of AML CFTPF Manual section 7.28, Page 10.
policy_extract: [full 7.28 text verbatim]
comply_status: Compliant
fulfilled_clauses: • STR confidentiality ... • tipping off prohibition ... • group sharing exception ...
```

**Current system often outputs instead:**

```
Output/Response: Page 45, Section X: one short quote
Fulfilled clauses: None
Status: Non-Compliant or Partial (wrong — manual says Compliant)
```

---

## Files to edit (when you approve changes later)

| Step | File |
|---|---|
| Gov extract schema | `bcp-api/Schemas/gov-requirement-points.schema.json` |
| Compare prompt | `bcp-api/Services/LandingAi/LandingAiComparePromptBuilder.cs` |
| Compare output schema | `bcp-api/Schemas/compliance-comparison-v2.schema.json` |
| Compare formatter | `bcp-api/Services/LandingAi/LandingAiComparisonFormatter.cs` |
| Pass 2 prompt | `bcp-api/Services/GovPointsService.cs` (`DualVerifyPromptBuilder`) |
| Normalizer | `bcp-api/Services/LandingAi/LandingAiComparisonNormalizer.cs` |

No code changed in this document — suggestions only.
