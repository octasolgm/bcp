import { REFERENCE_MAP_PROMPT } from './reference-map-prompt';

export type GovPointPayload = {
  point_id: string;
  title?: string;
  text: string;
};

export function formatGovPointForPrompt(point: GovPointPayload): string {
  const title = point.title?.trim();
  const head = title ? `${point.point_id} ${title}` : point.point_id;
  return `${head}\n${point.text}`.trim();
}

/** Phase 2 Gemini/GPT prompt — independent verification after Landing AI Phase 1 */
export function buildDualVerifyPrompt(
  point: GovPointPayload,
  landingMessage: string,
  docxSupplementMarkdown?: string,
): string {
  let prompt = `${REFERENCE_MAP_PROMPT}
DUAL VERIFICATION PIPELINE — PASS 2 (INDEPENDENT)
You are the second verifier. Landing AI (Pass 1) already analyzed this requirement. Re-read the attached internal PDF(s) yourself and produce your own assessment.

Rules:
1. Do NOT copy Pass 1 blindly — independently find evidence and assign status/confidence.
2. Use the same output format as Pass 1 (Reference PDF, Output/Response, Fulfilled clauses, Status, Confidence, CAP, Responsibility).
3. If you disagree with Pass 1, explain the difference in your Output/Response or CAP text.

LANDING AI PASS 1 (reference only):
---
${landingMessage.trim()}
---

REQUIREMENT POINT TO CHECK:

${formatGovPointForPrompt(point)}
`;

  if (docxSupplementMarkdown?.trim()) {
    prompt += `\n\nADDITIONAL INTERNAL DOCUMENT (Implementation Manual — search this for Pass 2 evidence):\n---\n${docxSupplementMarkdown.trim()}\n---\n`;
  }

  return prompt;
}
