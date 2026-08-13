import {
  parseReferenceComplianceBlock,
  resolvePolicyExtractText,
} from '../ai-lab/parse-reference-response';
import type { AnalysisPoint } from './types';
import { resolveDisplayConfidence } from './point-compliance-status';

function extractAiMessage(raw?: string | null): string {
  if (!raw?.trim()) return '';
  try {
    const parsed = JSON.parse(raw) as { message?: string };
    return parsed.message?.trim() ?? raw.trim();
  } catch {
    return raw.trim();
  }
}

/** Policy snippet for analysis point rail cards (completed or preview). */
export function policySnippetFromAnalysisPoint(
  point: AnalysisPoint,
  snapshotContent?: string | null,
  maxLen = 140,
): string {
  for (const raw of [point.landingAiResult, point.googleAiResult]) {
    const msg = extractAiMessage(raw);
    if (!msg) continue;
    const block = parseReferenceComplianceBlock(msg);
    const extract = resolvePolicyExtractText(block);
    if (extract) {
      return extract.length > maxLen ? `${extract.slice(0, maxLen)}…` : extract;
    }
    const out = block.outputResponse?.trim();
    if (out && !/^n\/a$/i.test(out) && out !== '—') {
      return out.length > maxLen ? `${out.slice(0, maxLen)}…` : out;
    }
  }

  const snap = snapshotContent?.trim();
  if (snap) {
    return snap.length > maxLen ? `${snap.slice(0, maxLen)}…` : snap;
  }
  return '—';
}

/** Confidence label for rail — in process while queued/running, failed on error, % when done. */
export function railConfidenceForCoverageStatus(
  status: string,
  point?: AnalysisPoint | null,
): string {
  const s = (status ?? '').toLowerCase();
  if (s === 'failed' || s === 'cancelled') return 'Failed';
  if (s === 'completed') {
    return point ? resolveDisplayConfidence(point) : '—';
  }
  if (s === 'running' || s === 'processing' || s === 'queued' || s === 'pending') {
    return 'In process';
  }
  return '—';
}
