import {
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  type ReferenceComplianceBlock,
} from '../../lib/ai-lab/parse-reference-response';
import { countCapGapsForReportItem } from '../../lib/nd/cap-gap-count';
import type { DualVerifyAgreement } from '../../lib/landing-ai/dual-verify-merge';
import type { DualVerifyReportItem } from '../../lib/dual-verify-report';
import type { GapDraftOverlay, GapItemData, GapSeverity } from './reguliq-store';

export type { GapDraftOverlay };

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

/** Prefer Landing AI structured fields; Pass-2 LLM is often free-form markdown. */
function pickStructuredBlocks(item: DualVerifyReportItem): {
  landing: ReferenceComplianceBlock | null;
  llm: ReferenceComplianceBlock | null;
  structured: ReferenceComplianceBlock | null;
} {
  const landing = parseMaybe(item.landingMessage);
  const llm = parseMaybe(item.llmMessage);
  const structured =
    (looksStructured(landing) ? landing : null) ??
    (looksStructured(llm) ? llm : null) ??
    landing ??
    llm;
  return { landing, llm, structured };
}

function normalizeStatusPhrase(raw: string): string {
  return raw
    .replace(/\*+/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

function severityFromAgreement(
  agreement: DualVerifyAgreement | undefined,
  structuredStatus: string,
): GapSeverity {
  const llm = normalizeStatusPhrase(agreement?.llmStatus ?? '');
  const landing = normalizeStatusPhrase(agreement?.landingStatus ?? '');
  const structured = normalizeStatusPhrase(structuredStatus);
  const agree = (agreement?.status ?? '').toLowerCase();

  const anyStatus = `${llm} ${landing} ${structured}`;
  const isNon =
    /\bnon[- ]?compliant\b/.test(anyStatus) || (/\bnon\b/.test(llm) && /compliant/.test(llm));
  const isPartial = /\bpartial\b/.test(anyStatus);
  const isCompliant =
    /\bcompliant\b/.test(anyStatus) && !isNon && !isPartial;

  if (isNon || agree === 'both_non_compliant') return 'non_compliant';
  if (isPartial || agree === 'status_mismatch' || agree === 'confidence_gap') return 'partial_compliant';
  if (isCompliant || agree === 'aligned') return 'compliant';
  return 'partial_compliant';
}

function pageLabel(citationPage: string | null, fallbackLabel: string): string {
  if (citationPage) return `p.${citationPage}`;
  return fallbackLabel;
}

function extractPolicyExcerpt(block: ReferenceComplianceBlock | null, llmRaw?: string): string {
  if (block?.outputResponse?.trim()) return block.outputResponse.trim();
  if (block?.fulfilledClauses?.trim()) return block.fulfilledClauses.trim();
  // Free-form Pass-2: keep a short readable excerpt, not the whole essay
  const raw = llmRaw?.trim() ?? '';
  if (raw) {
    const clipped = raw.length > 900 ? `${raw.slice(0, 900).trim()}…` : raw;
    return clipped;
  }
  return '(No policy extract in this result)';
}

function buildGapsDraft(
  agreement: DualVerifyAgreement | undefined,
  block: ReferenceComplianceBlock | null,
): string {
  const parts: string[] = [];
  if (agreement?.summary?.trim()) parts.push(agreement.summary.trim());
  if (block?.correctiveAction?.trim() && !/^n\/?a$/i.test(block.correctiveAction.trim())) {
    parts.push(`Corrective action (AI): ${block.correctiveAction.trim()}`);
  }
  if (
    block?.fulfilledClauses?.trim() &&
    /no corresponding|not found|gap/i.test(block.fulfilledClauses)
  ) {
    parts.push(block.fulfilledClauses.trim());
  }
  return parts.join('\n\n');
}

/** Map a completed dual-verify point into a working-document row. */
export function reportItemToGapItem(
  item: DualVerifyReportItem,
  index: number,
  overlay?: GapDraftOverlay,
): GapItemData {
  const { landing, structured } = pickStructuredBlocks(item);
  const policyCite = parseReferenceCitation(structured?.outputResponse ?? '');
  const regCite = parseReferenceCitation(structured?.referencePdf ?? '');

  const regulatoryText =
    item.govText?.trim() ||
    [item.pointTitle, landing?.body || structured?.body].filter(Boolean).join('\n\n').trim() ||
    item.pointTitle ||
    item.pointId;

  const policyText = extractPolicyExcerpt(structured ?? landing, item.llmMessage);

  const id = String(index + 1).padStart(2, '0');
  const section = item.pointId.startsWith('§') ? item.pointId : `§${item.pointId}`;
  const title =
    item.pointTitle?.trim() ||
    landing?.title?.trim() ||
    structured?.title?.trim() ||
    item.pointId;

  const severity = severityFromAgreement(item.agreement, structured?.status ?? landing?.status ?? '');
  const aiGaps = buildGapsDraft(item.agreement, structured ?? landing);
  const gapCount = countCapGapsForReportItem(item);

  return {
    id,
    section,
    title,
    severity,
    gapCount,
    signedOff: overlay?.signedOff ?? false,
    expanded: overlay?.expanded ?? false,
    regulatoryText,
    policyText,
    regPage: pageLabel(regCite.page, 'source'),
    policyPage: pageLabel(policyCite.page ?? regCite.page, 'source'),
    gaps: overlay?.gaps ?? aiGaps,
    managementResponse: overlay?.managementResponse ?? '',
    designEffectiveness: overlay?.designEffectiveness ?? '',
    operatingEffectiveness: overlay?.operatingEffectiveness ?? '',
    overallEffectiveness: overlay?.overallEffectiveness ?? '',
    documentReference: overlay?.documentReference ?? '',
    evidence: overlay?.evidence ?? '',
  };
}

export function reportItemsToGapItems(
  items: DualVerifyReportItem[],
  overlays: Record<string, GapDraftOverlay> = {},
): GapItemData[] {
  const completed = items.filter((i) => {
    const hasBoth = Boolean(i.landingMessage?.trim() && i.llmMessage?.trim());
    const hasLandingOnly = Boolean(i.landingMessage?.trim());
    const okStatus = i.status === 'completed' || i.status === 'loaded' || !i.status;
    return okStatus && (hasBoth || hasLandingOnly);
  });
  return completed.map((item, index) =>
    reportItemToGapItem(item, index, overlays[item.pointId]),
  );
}
