import { parseReferenceComplianceBlock, type ReferenceComplianceBlock } from '../ai-lab/parse-reference-response';
import type { DualVerifyAgreement } from '../landing-ai/dual-verify-merge';
import type { AnalysisPoint } from './types';

export type ComplianceSeverity = 'compliant' | 'partial_compliant' | 'non_compliant';

/** Canonical display labels — use everywhere (filters, badges, exports, cards). */
export const COMPLIANCE_SEVERITY_LABELS: Record<ComplianceSeverity, string> = {
  compliant: 'compliant',
  partial_compliant: 'partial compliant',
  non_compliant: 'non compliant',
};

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

export function parseConfidencePercent(raw: string | null | undefined): number | null {
  if (!raw?.trim()) return null;
  const m = raw.match(/(\d{1,3})/);
  if (!m) return null;
  const n = Number(m[1]);
  return Number.isFinite(n) ? n : null;
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

/** True when the point has a scored compliance status or AI output to parse. */
export function analysisPointIsScored(point: AnalysisPoint): boolean {
  const fs = (point.finalStatus ?? '').toLowerCase();
  if (fs === 'compliant' || fs === 'partial_compliant' || fs === 'non_compliant') return true;

  const landingStatus = (point.landingAiStatus ?? '').toLowerCase();
  if (
    landingStatus === 'compliant' ||
    landingStatus === 'partial_compliant' ||
    landingStatus === 'non_compliant'
  ) {
    return true;
  }

  return Boolean(extractMessage(point.landingAiResult) || extractMessage(point.googleAiResult));
}

/** Authoritative tier for all ND views — finalStatus, else Landing AI status, else parsed messages.
 *  Returns null while the point is still queued / pending (do not invent Partial). */
export function resolveAnalysisPointSeverity(point: AnalysisPoint): ComplianceSeverity | null {
  const fs = (point.finalStatus ?? '').toLowerCase();
  if (fs === 'compliant' || fs === 'partial_compliant' || fs === 'non_compliant') {
    return fs as ComplianceSeverity;
  }

  const landingStatus = (point.landingAiStatus ?? '').toLowerCase();
  if (
    landingStatus === 'compliant' ||
    landingStatus === 'partial_compliant' ||
    landingStatus === 'non_compliant'
  ) {
    return landingStatus as ComplianceSeverity;
  }

  const landingMessage = extractMessage(point.landingAiResult);
  const llmMessage = extractMessage(point.googleAiResult);
  if (!landingMessage && !llmMessage) return null;

  const agreement = extractAgreement(point.googleAiResult);
  const structured = pickStructuredBlocks(landingMessage, llmMessage);
  return severityFromAgreement(agreement, structured?.status ?? '');
}

/** Status label shown in pills, badges, and summary cards — same everywhere. */
export function resolvePointComplianceLabel(point: AnalysisPoint): string {
  const severity = resolveAnalysisPointSeverity(point);
  return severity ? complianceSeverityLabel(severity) : '';
}

/** Confidence % aligned with resolved severity (never show 100% when partial/non-compliant). */
export function resolveDisplayConfidence(point: AnalysisPoint): string {
  const severity = resolveAnalysisPointSeverity(point);
  if (!severity) return '—';
  const landingMessage = extractMessage(point.landingAiResult);
  const llmMessage = extractMessage(point.googleAiResult);
  const agreement = extractAgreement(point.googleAiResult);
  const landing = parseMaybe(landingMessage);
  const llm = parseMaybe(llmMessage);
  const structured = pickStructuredBlocks(landingMessage, llmMessage);

  const candidates = [llm?.confidence, landing?.confidence, structured?.confidence].filter(Boolean) as string[];
  let conf = candidates[0]?.trim() ?? '';
  const firstLine = conf.split('\n').map((l) => l.trim()).find(Boolean) ?? conf;
  const pct = parseConfidencePercent(firstLine);
  if (pct != null && !firstLine.toLowerCase().includes('gap analysis')) {
    return `${pct}%`;
  }
  conf = firstLine;

  if (severity === 'partial_compliant') {
    for (const c of candidates) {
      const p = parseConfidencePercent(c);
      if (p != null && p < 100) return c.trim();
    }
    if (pct === 100 || agreement?.status === 'status_mismatch' || agreement?.status === 'confidence_gap') {
      return '< 100%';
    }
  }

  if (severity === 'non_compliant') {
    for (const c of candidates) {
      if (/\bnon/i.test(parseMaybe(llmMessage)?.status ?? '') || parseConfidencePercent(c)! <= 50) {
        return c.trim();
      }
    }
    if (pct != null && pct > 69) return '≤ 69%';
  }

  return conf || '—';
}

export function complianceSeverityLabel(severity: ComplianceSeverity): string {
  return COMPLIANCE_SEVERITY_LABELS[severity];
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
