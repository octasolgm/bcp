import type { GovRequirementPoint } from '../landing-ai/types/landing-ai.types';

/** Build Gemini prompt for one gov point → Reguliq gap item JSON */
export function buildBcpwebGapItemPrompt(
  point: GovRequirementPoint,
  regulationTitle: string,
  internalDocName: string,
): string {
  return `You are a CBUAE/TFS regulatory compliance auditor. Analyze ONE requirement point against the attached documents.

ATTACHED FILES:
1. Internal compliance document: "${internalDocName}" (bank policy/manual)
2. Regulation / guideline: "${regulationTitle}" (benchmark)

REQUIREMENT POINT TO ANALYZE:
${point.point_id}${point.title ? ` — ${point.title}` : ''}
${point.text}

RULES:
- Compare by regulatory INTENT and operational effect — not keyword matching.
- regulatoryText = verbatim quote from the REGULATION document for this obligation.
- policyText = best matching verbatim quote from the INTERNAL document, or "No corresponding procedure found."
- regulatoryPdfPage / policyPdfPage = integer page numbers where evidence appears (estimate from PDF if needed).
- gapsIdentified = numbered list of missing sub-intents (for Partial/Non-Compliant). "No gap identified." if Compliant.
- severity: critical | high | medium | low | compliant
  - non-compliant with major regulatory risk → critical
  - non-compliant or partial with significant gap → high
  - partial with moderate gap → medium
  - minor gap → low
  - fully compliant → compliant
- conclusion, observation, actionPlan = professional auditor prose (actionPlan = numbered steps if not compliant, else "No remediation required.")

Return ONLY valid JSON (no markdown fences):
{
  "clauseNo": "§2.1",
  "sectionRef": "2.1",
  "title": "Short title",
  "severity": "high",
  "regulatoryText": "...",
  "regulatoryPdfPage": 6,
  "policyText": "...",
  "policyPdfPage": 14,
  "gapsIdentified": "1. ...\\n2. ...",
  "conclusion": "...",
  "observation": "...",
  "actionPlan": "..."
}`;
}

/** Extract gov points from regulation PDF when Landing AI unavailable */
export function buildExtractGovPointsPrompt(regulationTitle: string): string {
  return `You are analyzing the attached regulation document: "${regulationTitle}".

Extract ALL mandatory compliance requirement points (sections/clauses that banks must implement). Skip introductions, definitions-only, and purely informational text.

Return ONLY valid JSON array (no markdown):
[
  {
    "point_id": "2.1",
    "title": "Senior Management Commitment",
    "text": "Full requirement text...",
    "section": "2.1",
    "page_hint": 6
  }
]

Extract 8–15 substantive points. Use point_id like section numbers (2.1, 2.3, 3.7, 4).`;
}

export interface GeminiGapItemJson {
  clauseNo?: string;
  sectionRef?: string;
  title?: string;
  severity?: string;
  regulatoryText?: string;
  regulatoryPdfPage?: number;
  policyText?: string;
  policyPdfPage?: number;
  gapsIdentified?: string;
  conclusion?: string;
  observation?: string;
  actionPlan?: string;
}

export interface GeminiGovPointJson {
  point_id: string;
  title?: string;
  text: string;
  section?: string;
  page_hint?: number;
}

/** Parse JSON from Gemini response (strips code fences) */
export function parseGeminiJson<T>(raw: string): T | null {
  const trimmed = raw.trim();
  const fence = trimmed.match(/```(?:json)?\s*([\s\S]*?)```/);
  const candidate = fence ? fence[1].trim() : trimmed;
  const start = candidate.indexOf('[') >= 0 ? candidate.indexOf('[') : candidate.indexOf('{');
  const end =
    candidate.lastIndexOf(']') > candidate.lastIndexOf('}')
      ? candidate.lastIndexOf(']') + 1
      : candidate.lastIndexOf('}') + 1;
  if (start < 0 || end <= start) return null;
  try {
    return JSON.parse(candidate.slice(start, end)) as T;
  } catch {
    return null;
  }
}
