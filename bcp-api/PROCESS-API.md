# Process API Reference

## Step 1: Parse (regulation PDF)

**api:** `POST https://api.va.landing.ai/v1/ade/parse`  
**auth:** `Bearer {LandingAi:ApiKey}` / `VISION_AGENT_API_KEY`  
**model:** `dpt-2-latest` (`LandingAi:ParseModel`)  
**prompt:** none (multipart: `document` PDF + `model`)  
**complete:** HTTP 200 + JSON with `markdown`  
**output:** PDF markdown (page markers injected)

---

## Step 2: Extract (regulation gov points)

**api:** `POST https://api.va.landing.ai/v1/ade/extract`  
**auth:** `Bearer {LandingAi:ApiKey}`  
**model:** `extract-latest` (`LandingAi:ExtractModel`)  
**multipart fields:** `schema` + `markdown` + `model`  
**complete:** HTTP 200 + JSON `extraction.points[]`  
**output:** `{ point_id, title, text, section, page_hint, point_type }[]` → `regulation_points` table

**prompt (markdown field — full regulation text from Step 1):**

```
{regulation_pdf_markdown_from_step_1}
```

**schema (schema field — this is the extraction instruction, no separate text prompt):**

```json
{
  "type": "object",
  "properties": {
    "points": {
      "type": "array",
      "description": "Every numbered government or regulatory requirement clause extracted from the document",
      "items": {
        "type": "object",
        "properties": {
          "point_id": {
            "type": "string",
            "description": "Official numbering such as 2.6.5, Article 3, TFS-REQ-03"
          },
          "title": {
            "type": "string",
            "description": "Short title of the requirement point"
          },
          "text": {
            "type": "string",
            "description": "Full verbatim requirement text including all sub-conditions"
          },
          "section": {
            "type": "string",
            "description": "Parent section or article reference"
          },
          "page_hint": {
            "type": "integer",
            "description": "1-based PDF viewer page index where this requirement appears (not printed/footer page numbers). Use 0 if unknown."
          },
          "point_type": {
            "type": "string",
            "enum": ["mandatory", "informational", "definition"],
            "description": "mandatory = enforceable requirement to compare; informational = introduction/purpose/background (skip compare); definition = glossary term only (skip compare)"
          }
        },
        "required": ["point_id", "text"]
      }
    }
  },
  "required": ["points"]
}
```

---

## Step 3: Parse (internal PDF)

**api:** `POST https://api.va.landing.ai/v1/ade/parse`  
**auth:** `Bearer {LandingAi:ApiKey}`  
**model:** `dpt-2-latest` (`LandingAi:ParseModel`)  
**prompt:** none (multipart: `document` PDF + `model`)  
**complete:** HTTP 200 + JSON with `markdown`  
**output:** PDF markdown → `landing_ai_parse_cache`

---

## Step 4: Analysis Phase 1 — Landing AI compare (per gov point)

**api:** `POST https://api.va.landing.ai/v1/ade/extract`  
**auth:** `Bearer {LandingAi:ApiKey}`  
**model:** `extract-latest` (`LandingAi:ExtractModel`)  
**multipart fields:** `schema` + `markdown` + `model`  
**complete:** HTTP 200 + JSON `extraction`  
**output:** formatted compliance message (`comply_status`, confidence, evidence, CAP) → `analysis_points.landing_ai_*`

**prompt (markdown field — built by `LandingAiComparePromptBuilder`, single internal doc):**

```
You are an expert automated regulatory compliance auditor specializing in CBUAE and TFS frameworks. Evaluate the ENTIRE requirement point against the internal process document using semantic intent analysis (not keyword matching).

Rules:
- Compare by regulatory meaning and operational effect.
- cite evidence as: Page [X], Section [Y]: 'verbatim internal quote'
- Page [X] MUST be the 1-based PDF file page index as shown in a PDF viewer (scroll bar / page counter), NOT printed footer numbers or table-of-contents page numbers.
- If Non-Compliant, uae_response_compliance_level must be exactly: No corresponding procedure found.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status
- Return structured JSON matching the provided schema only (no markdown fences)

---
INPUT DATA:

ATTACHED INTERNAL PROCESS DOCUMENT ({internal_file_name} — parsed markdown from internal policy PDF; search this entire section):

{internal_markdown_from_step_3}

REQUIREMENT POINT TO CHECK:

{point_number} {point_title}

{point_content}
```

**prompt (markdown field — multiple internal docs variant):**

```
You are an expert automated regulatory compliance auditor specializing in CBUAE and TFS frameworks. Evaluate the ENTIRE requirement point against the internal process document using semantic intent analysis (not keyword matching).

Rules:
- Compare by regulatory meaning and operational effect.
- cite evidence as: Page [X], Section [Y]: 'verbatim internal quote'
- Page [X] MUST be the 1-based PDF file page index as shown in a PDF viewer (scroll bar / page counter), NOT printed footer numbers or table-of-contents page numbers.
- If Non-Compliant, uae_response_compliance_level must be exactly: No corresponding procedure found.
- comply_status must be one of: Compliant | Partial Compliant | Non-Compliant
- compliance_confidence_percentage: integer 0-100 aligned with status
- Return structured JSON matching the provided schema only (no markdown fences)

---
INPUT DATA:

ATTACHED INTERNAL PROCESS DOCUMENTS ({doc_count} PDFs — evaluate compliance across ALL documents; cite evidence from any document):

--- DOCUMENT 1: {file_name_1} (parsed markdown) ---

{internal_markdown_1}

--- DOCUMENT 2: {file_name_2} (parsed markdown) ---

{internal_markdown_2}

REQUIREMENT POINT TO CHECK:

{point_number} {point_title}

{point_content}
```

**schema (schema field — output structure instruction):**

```json
{
  "type": "object",
  "description": "Semantically evaluate the ENTIRE REQUIREMENT POINT TO CHECK (regulatory intent + all sub-obligations) against the ATTACHED INTERNAL PROCESS DOCUMENT markdown. Compare by operational meaning and control equivalence — NOT keyword overlap. Return one structured compliance object (or a one-element array). No markdown fences or conversational text.",
  "properties": {
    "requirement_id": {
      "type": "string",
      "description": "The exact clause number and header title being evaluated (e.g. '2.4 Internal Controls')."
    },
    "requirement_text": {
      "type": "string",
      "description": "The explicit rule or obligation text provided under REQUIREMENT POINT TO CHECK."
    },
    "uae_response_compliance_level": {
      "type": "string",
      "description": "Page [Number] is the 1-based PDF viewer page index (not printed footer page). Section [Header Code]: '[Verbatim internal document quote that operationally satisfies the regulatory intent]'. Judgment must be semantic (intent match), not literal word match to gov text. If Non-Compliant, output exactly: No corresponding procedure found."
    },
    "comply_status": {
      "type": "string",
      "enum": ["Compliant", "Partial Compliant", "Non-Compliant"],
      "description": "Based on whole-point semantic intent comparison. Compliant = all regulatory sub-intents operationally satisfied (wording may differ). Partial = some sub-intents met. Non-Compliant = no procedural equivalent."
    },
    "compliance_confidence_percentage": {
      "type": "integer",
      "description": "0–100 reflecting semantic completeness of intent coverage (not keyword overlap): Compliant 86–100 (100 only if every sub-intent fully satisfied), Partial 31–85, Non-Compliant 0–30."
    },
    "fulfilled_clauses": {
      "type": "string",
      "description": "For each sub-intent that IS semantically satisfied: bullet lines starting with • describing gov sub-obligation → internal procedure mapping (e.g. '• [sub-intent] — semantically satisfied by [procedure] (Page X, Section Y)'). Output None if nothing is covered."
    },
    "corrective_action_plan": {
      "type": "string",
      "description": "Required when Partial or Non-Compliant. Start with Gap(s): then numbered items (1) Missing: [regulatory sub-intent not operationally met], Fix: [operational action]. Name every gap by intent — never keyword-only analysis. Empty string when Compliant."
    },
    "suggested_responsibility": {
      "type": "string",
      "description": "Department or role responsible for the corrective action. Must be empty string when Compliant."
    }
  },
  "required": [
    "requirement_id",
    "requirement_text",
    "uae_response_compliance_level",
    "comply_status",
    "compliance_confidence_percentage",
    "fulfilled_clauses",
    "corrective_action_plan",
    "suggested_responsibility"
  ]
}
```

---

## Step 5: Analysis Phase 2 — Dual verify LLM (per gov point)

**provider/model:** admin setting `nd_system_settings.dual_verify_llm` (default: `google` / `gemini-2.0-flash`)

### google (default)

**api:** `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Gemini:ApiKey}`  
**model:** `gemini-2.0-flash` | `gemini-2.0-flash-lite` | `gemini-3.5-flash`  
**prompt:** `DualVerifyPromptBuilder` + internal PDF(s) as base64 inline_data

```
DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)
You are the second verifier. Landing AI (Pass 1) already analyzed this requirement.
Re-read the attached internal PDF(s) and produce your own assessment.
Attached PDF: {filename}

LANDING AI PASS 1 (reference only):
---
{landing_message}
---

REQUIREMENT POINT TO CHECK:
{point_id} {title}
{point_text}

INTERNAL DOCUMENT MARKDOWN (parsed text — use with attached PDF(s) for accuracy):
---
{internal_markdown}
---
```

**complete:** HTTP 200 + JSON `candidates[].content.parts[].text`  
**output:** LLM compliance message + agreement vs Pass 1 → `analysis_points.google_ai_*`, `dual_verify_status`

### openai

**api:** `POST https://api.openai.com/v1/chat/completions`  
**model:** `gpt-4o` | `gpt-4o-mini` | `o1-mini`  
**prompt:** same `DualVerifyPromptBuilder` text (text-only; markdown supplement included)  
**complete:** HTTP 200 + JSON `choices[].message.content`  
**output:** same as google

### anthropic

**api:** `POST https://api.anthropic.com/v1/messages`  
**model:** `claude-sonnet-4-20250514` | `claude-3-5-sonnet-latest` | `claude-3-5-haiku-latest`  
**prompt:** same `DualVerifyPromptBuilder` text + PDF document blocks (base64)  
**complete:** HTTP 200 + JSON `content[].text`  
**output:** same as google

### xai

**api:** `POST https://api.x.ai/v1/chat/completions`  
**model:** `grok-2-latest` | `grok-beta`  
**prompt:** same `DualVerifyPromptBuilder` text (text-only)  
**complete:** HTTP 200 + JSON `choices[].message.content`  
**output:** same as google
