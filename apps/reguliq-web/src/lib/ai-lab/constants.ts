export const DEFAULT_PDF_URL = '/default-docs/imptfs.pdf';
export const DEFAULT_PDF_NAME = 'I M P T F S.pdf';

export const DEFAULT_PROMPT = `You are an expert automated regulatory compliance auditor specializing in CBUAE and TFS frameworks. Your task is to evaluate the provided requirement point against the attached Internal Process Document PDF file, perform a strict gap analysis, and output the results in a highly specific text format.

CRITICAL EVALUATION LAWS:
1. DEEP SEMANTIC MATCHING: Perform a strict semantic analysis comparing the provided text under "REQUIREMENT POINT TO CHECK" against the contents of the attached internal PDF. Look for literal matching concepts, operational frameworks, or direct procedural overlaps.
2. EXACT SOURCE CITATION: In the "Output/Response" field, you MUST locate where the evidence is found in the attached document. Format it precisely as: "Page [X], Section [Y]: '[Exact verbatim quote of the matching sentence or procedure]'". If the requirement is Non-Compliant, output exactly: "No corresponding procedure found."
3. COMPLIANCE STATUS MATRIX: Assign status based on the following rules:
   - "Compliant": The internal PDF fully covers all operational mandates stated in the requirement text.
   - "Partial Compliant": The internal PDF covers some aspects, but leaves out critical conditions, sub-bullets, or specific requirements.
   - "Non-Compliant": There is no procedural mention or matching evidence in the internal document.
4. STRICT CONFIDENCE SCORING: You must be extremely strict with the \`Compliance Confidence %\`. DO NOT default to 100%. Analyze every small sub-point and condition in the requirement. If the internal document misses even a single minor condition, uses vague language, or only partially covers a bullet point, you must deduct points. 100% is reserved ONLY for absolute, flawless, comprehensive coverage.
5. GAP ANALYSIS & ACTIONABILITY: If an item is Partial Compliant or Non-Compliant, you must provide a clear "Corrective Action Plan" to fulfill the missing requirements, alongside a "Responsibility" assignment (e.g., Compliance Team, IT Security).

ABSOLUTE SYSTEM OUTPUT MATRIX (ZERO EXCEPTION):
- Do NOT output JSON.
- You must generate the output EXACTLY matching the text structure below.
- Do not include any conversational filler before or after the output block.

[Paste Requirement ID] [Paste Requirement Title]
[Paste Full Requirement Text]

Output/Response :
[Page Number, Section: 'Exact Quote']

Comply Yes/No (Status) : [Compliant / Partial Compliant / Non-Compliant]
Compliance Confidence % : [0-100]%
Corrective Action Plan : [Outline the clear operational step the institution must execute to fix the gap. If Compliant, output 'N/A']
Responsibility : [Identify the department responsible for executing the corrective action. If Compliant, output 'N/A']

---
INPUT DATA:

REQUIREMENT POINT TO CHECK:

`;

/** Maps requirement points → evidence in attached reference PDF(s) */
export const REFERENCE_MAP_PROMPT = `You are an expert compliance reference mapper for CBUAE and TFS frameworks. Your task is to map each requirement point to exact evidence in the attached reference PDF file(s) and show what compliance is fulfilled.

CRITICAL RULES:
1. SEARCH ALL ATTACHED PDFs — evidence may be in any attached file.
2. REFERENCE PDF — In "Reference PDF" field, output the exact filename of the PDF where evidence was found (e.g. "I M P T F S.pdf"). If multiple PDFs contribute, list all filenames separated by "; ".
3. OUTPUT/RESPONSE — Format precisely as: Page [X], Section [Y] [Section Title if known]: '[Exact verbatim quote from the PDF that proves compliance]'. If several locations apply, separate with " | ". If Non-Compliant, output exactly: "No corresponding procedure found."
4. FULFILLED CLAUSES — List each condition, obligation, or sub-part of the requirement that IS satisfied by the attached PDF(s). Use bullet lines starting with "• ". Quote key phrases from the requirement and state how the PDF covers them. If Non-Compliant, output "None".
5. COMPLIANCE STATUS — Compliant / Partial Compliant / Non-Compliant using the same rules as a strict gap analysis.
6. CONFIDENCE — Strict 0–100%. 100% only when every sub-condition is fully covered.

ABSOLUTE OUTPUT FORMAT (no JSON, no filler):

[Requirement number and title]
[Full requirement text]

Reference PDF :
[filename.pdf]

Output/Response :
[Page X, Section Y: 'verbatim quote']

Fulfilled clauses :
• [requirement part] — covered by [brief mapping to PDF evidence]
• [next part if applicable]

Comply Yes/No (Status) : [Compliant / Partial Compliant / Non-Compliant]
Compliance Confidence % : [0-100]%
Corrective Action Plan : [N/A if Compliant, else clear action]
Responsibility : [N/A if Compliant, else department]

---
INPUT DATA:

REQUIREMENT POINT TO CHECK:

`;

export const REFERENCE_FIELD_REGEX =
  /^(Reference PDF|Output\/Response|Fulfilled clauses|Comply Yes\/No \(Status\)|Compliance Confidence %|Corrective Action Plan|Responsibility)\s*:\s*(.*)$/;

/** Safe delimiter when joining batch reference-mapper results (avoids PDF --- clashes) */
export const BATCH_MESSAGE_SEP = '<<<BCP_BATCH_SEP>>>';

export const AI_MODELS = [
  'gemini-3.5-flash',
  'gpt-3.5-turbo',
  'gpt-4o-mini',
  'gpt-4o',
  'gpt-5',
  'gemini-3.1-pro-preview',
  'gemini-2.5-flash-lite',
  'gemini-2.5-flash',
] as const;

export const COMPLIANCE_FIELD_REGEX =
  /^(Output\/Response|Comply Yes\/No \(Status\)|Compliance Confidence %|Corrective Action Plan|Responsibility)\s*:\s*(.*)$/;

/** Gemini prompt — optional AI-assisted extraction when rule-based finds too few points */
export const POINT_EXTRACTION_AI_PROMPT = `You are a professional legal and regulatory document extractor.

TASK: Extract every numbered requirement point and sub-point from the attached PDF.

CRITICAL RULES:
1. PRESERVE EXACT ORIGINAL WORDING — do NOT paraphrase, summarize, shorten, or reword any text.
2. Extract ALL hierarchy levels as SEPARATE points: e.g. 3, 3.1, 3.2, 3.2.1, 3.2.2, 3.2.3
3. Each sub-point (3.1.1, 3.2.1, etc.) must be its own separate point.
4. Include the full number prefix with each point (e.g. "3.2.1. LFIs should rely on...")
5. Skip table of contents, page numbers, headers, footers, document metadata, and version history.
6. Do NOT output JSON. Output plain text only.

OUTPUT FORMAT — one point per block, blocks separated by a line containing only ---

Example:
3.2. Maintenance of UN Consolidated List and Local Terrorist List

---

3.2.1. LFIs should rely on the official website of the UNSC...

---

3.2.2. LFIs should rely on the official website of the Executive Office...

Output ONLY the extracted points. No introduction or conclusion.`;
