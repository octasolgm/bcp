import {
  parseCapGaps,
  parseReferenceComplianceBlock,
  type CapGap,
} from '../ai-lab/parse-reference-response';
import type { DualVerifyReportItem } from '../dual-verify-report';
import type { AnalysisPoint } from './types';
import { resolveAnalysisPointSeverity } from './point-compliance-status';

function extractAiMessage(raw?: string | null): string {
  if (!raw?.trim()) return '';
  try {
    const parsed = JSON.parse(raw) as { message?: string };
    return parsed.message?.trim() ?? raw;
  } catch {
    return raw;
  }
}

/** Same cap resolution as nd-gap-point-detail (saved plan → AI corrective action). */
export function resolveCapSourceForAnalysisPoint(p: AnalysisPoint): string {
  const landingMessage = extractAiMessage(p.landingAiResult);
  const llmMessage = extractAiMessage(p.googleAiResult);
  const primaryMsg = (llmMessage || landingMessage).trim();
  const primaryBlock = parseReferenceComplianceBlock(primaryMsg);
  const aiCap =
    primaryBlock.correctiveAction && primaryBlock.correctiveAction !== 'N/A'
      ? primaryBlock.correctiveAction.trim()
      : '';
  const originalPlan = p.originalAiActionPlan?.trim() ?? '';
  const currentPlan = p.finalActionPlan?.trim() ?? originalPlan;
  return currentPlan || originalPlan || aiCap;
}

/** Placeholder CAP rows (e.g. "** N/A **") must not count as real gaps. */
export function isPlaceholderGapText(text: string): boolean {
  const t = text.trim().toLowerCase().replace(/\*+/g, '').trim();
  return !t || t === 'n/a' || t === '—' || t === '-' || t === 'none' || t === 'not applicable';
}

export function isMeaningfulCapGap(gap: CapGap): boolean {
  const missingOk = !isPlaceholderGapText(gap.missing);
  const fixOk = Boolean(gap.fix?.trim()) && !isPlaceholderGapText(gap.fix);
  return missingOk || fixOk;
}

export function meaningfulCapGaps(source: string): CapGap[] {
  if (!source.trim()) return [];
  return parseCapGaps(source).filter(isMeaningfulCapGap);
}

export function countCapGapsForAnalysisPoint(p: AnalysisPoint): number {
  return countDisplayGapsForAnalysisPoint(p);
}

/** Gap badges: compliant points show 0 unless manually uploaded evidence exists. */
export function countDisplayGapsForAnalysisPoint(
  p: AnalysisPoint,
  manualEvidenceCount = 0,
): number {
  const severity = resolveAnalysisPointSeverity(p);
  if (severity === 'compliant') return manualEvidenceCount > 0 ? manualEvidenceCount : 0;
  const count = meaningfulCapGaps(resolveCapSourceForAnalysisPoint(p)).length;
  return Math.max(count, manualEvidenceCount);
}

export function countCapGapsForReportItem(item: DualVerifyReportItem): number {
  const landingStatus = item.agreement?.landingStatus?.toLowerCase() ?? '';
  if (landingStatus === 'compliant') return 0;
  for (const msg of [item.llmMessage, item.landingMessage]) {
    if (!msg?.trim()) continue;
    const block = parseReferenceComplianceBlock(msg);
    const cap = block.correctiveAction?.trim();
    if (cap && cap !== 'N/A') {
      const count = meaningfulCapGaps(cap).length;
      if (count > 0) return count;
    }
  }
  return 0;
}
