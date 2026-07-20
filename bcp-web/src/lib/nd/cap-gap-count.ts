import {
  parseCapGaps,
  parseReferenceComplianceBlock,
} from '../ai-lab/parse-reference-response';
import type { DualVerifyReportItem } from '../dual-verify-report';
import type { AnalysisPoint } from './types';

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

export function countCapGapsForAnalysisPoint(p: AnalysisPoint): number {
  const source = resolveCapSourceForAnalysisPoint(p);
  if (!source) return 0;
  return parseCapGaps(source).length;
}

export function countCapGapsForReportItem(item: DualVerifyReportItem): number {
  for (const msg of [item.llmMessage, item.landingMessage]) {
    if (!msg?.trim()) continue;
    const block = parseReferenceComplianceBlock(msg);
    const cap = block.correctiveAction?.trim();
    if (cap && cap !== 'N/A') {
      const count = parseCapGaps(cap).length;
      if (count > 0) return count;
    }
  }
  return 0;
}
