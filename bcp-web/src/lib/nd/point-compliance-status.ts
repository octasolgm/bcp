import { parseReferenceComplianceBlock, type ReferenceComplianceBlock } from '../ai-lab/parse-reference-response';
import type { DualVerifyAgreement } from '../landing-ai/dual-verify-merge';
import type { AnalysisPoint } from './types';

export type ComplianceSeverity = 'compliant' | 'partial_compliant' | 'non_compliant';

function looksStructured(block: ReferenceComplianceBlock | null): boolean {
  if (!block) return false;
  return Boolean(
    block.status?.trim() ||
      block.outputResponse?.trim() ||
      block.fulfilledClauses?.trim() ||
      block.referencePdf?.trim(),
  );
}

function parseMaybe(message?: string): ReferenceComplianceBlock | null {
  const t = message?.trim() ?? '';
  if (!t) return null;
  return parseReferenceComplianceBlock(t);
}

function normalizeStatusPhrase(raw: string): string {
  return raw
    .replace(/\*+/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

/** Same rules as gap-analysis report cards (dual-verify agreement + structured status). */
export function severityFromAgreement(
  agreement: DualVerifyAgreement | undefined,
  structuredStatus: string,
): ComplianceSeverity {
  const llm = normalizeStatusPhrase(agreement?.llmStatus ?? '');
  const landing = normalizeStatusPhrase(agreement?.landingStatus ?? '');
  const structured = normalizeStatusPhrase(structuredStatus);
  const agree = (agreement?.status ?? '').toLowerCase();

  const anyStatus = `${llm} ${landing} ${structured}`;
  const isNon =
    /\bnon[- ]?compliant\b/.test(anyStatus) || (/\bnon\b/.test(llm) && /compliant/.test(llm));
  const isPartial = /\bpartial\b/.test(anyStatus);
  const isCompliant = /\bcompliant\b/.test(anyStatus) && !isNon && !isPartial;

  if (isNon || agree === 'both_non_compliant') return 'non_compliant';
  if (isPartial || agree === 'status_mismatch' || agree === 'confidence_gap') return 'partial_compliant';
  if (isCompliant || agree === 'aligned') return 'compliant';
  return 'partial_compliant';
}

function extractMessage(raw?: string | null): string {
  if (!raw?.trim()) return '';
  try {
    const parsed = JSON.parse(raw) as { message?: string };
    return parsed.message?.trim() ?? raw;
  } catch {
    return raw;
  }
}

function extractAgreement(raw?: string | null): DualVerifyAgreement | undefined {
  if (!raw?.trim()) return undefined;
  try {
    const parsed = JSON.parse(raw) as { agreement?: DualVerifyAgreement };
    return parsed.agreement;
  } catch {
    return undefined;
  }
}

function pickStructuredBlocks(landingMessage: string, llmMessage: string): ReferenceComplianceBlock | null {
  const landing = parseMaybe(landingMessage);
  const llm = parseMaybe(llmMessage);
  return (
    (looksStructured(landing) ? landing : null) ??
    (looksStructured(llm) ? llm : null) ??
    landing ??
    llm
  );
}

/** Resolve the compliance tier shown in gap analysis — prefer AI agreement over stale DB finalStatus. */
export function resolveAnalysisPointSeverity(point: AnalysisPoint): ComplianceSeverity {
  const landingMessage = extractMessage(point.landingAiResult);
  const llmMessage = extractMessage(point.googleAiResult);
  const agreement = extractAgreement(point.googleAiResult);
  const structured = pickStructuredBlocks(landingMessage, llmMessage);
  const fromAi = severityFromAgreement(agreement, structured?.status ?? '');

  if (agreement || structured?.status?.trim()) return fromAi;

  const fs = (point.finalStatus ?? '').toLowerCase();
  if (fs === 'compliant' || fs === 'partial_compliant' || fs === 'non_compliant') {
    return fs as ComplianceSeverity;
  }
  return fromAi;
}

export function complianceSeverityLabel(severity: ComplianceSeverity): string {
  if (severity === 'compliant') return 'Fully Compliant';
  if (severity === 'partial_compliant') return 'Partial Compliant';
  return 'Non-Compliant';
}

export function pointHasCapContent(point: AnalysisPoint): boolean {
  if (point.finalActionPlan?.trim() || point.originalAiActionPlan?.trim()) return true;

  const landingMessage = extractMessage(point.landingAiResult);
  const llmMessage = extractMessage(point.googleAiResult);
  const structured = pickStructuredBlocks(landingMessage, llmMessage);
  const cap = structured?.correctiveAction?.trim();
  return Boolean(cap && cap !== 'N/A' && cap !== '—');
}

export function pointNeedsDetailPanel(point: AnalysisPoint): boolean {
  const severity = resolveAnalysisPointSeverity(point);
  return (
    pointHasCapContent(point) ||
    severity === 'partial_compliant' ||
    severity === 'non_compliant' ||
    Boolean(extractMessage(point.landingAiResult) || extractMessage(point.googleAiResult))
  );
}
