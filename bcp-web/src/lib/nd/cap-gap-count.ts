import {
  parseCapGaps,
  parseReferenceComplianceBlock,
  type CapGap,
} from '../ai-lab/parse-reference-response';
import type { DualVerifyReportItem } from '../dual-verify-report';
import type { AnalysisPoint } from './types';
import { resolveAnalysisPointSeverity } from './point-compliance-status';
import { normalizeRegulGapDisplayText, parseRegulElementCapSegments, parseRegulElementGapSegments } from './regul-fields';

function extractAiMessage(raw?: string | null): string {
  if (!raw?.trim()) return '';
  try {
    const parsed = JSON.parse(raw) as { message?: string };
    return parsed.message?.trim() ?? raw;
  } catch {
    return raw;
  }
}

/** Phase 2 verification prose — not a user-facing corrective action. */
export function isVerificationMetaCapText(text: string): boolean {
  const t = text.trim().toLowerCase();
  if (!t || t === 'n/a' || t === '—' || t === '-') return true;
  if (/^n\/a\s*[—–-]/.test(t)) return true;
  if (t.includes('semantically satisfied')) return true;
  if (t.includes('independently confirm')) return true;
  if (/\bpass\s*[12]\b/.test(t) && (t.includes('confirm') || t.includes('assessment') || t.includes('verif'))) {
    return true;
  }
  return false;
}

function capFromMessage(message: string): string {
  if (!message.trim()) return '';
  const block = parseReferenceComplianceBlock(message);
  const cap = block.correctiveAction?.trim() ?? '';
  if (!cap || isVerificationMetaCapText(cap)) return '';
  return cap;
}

/** Prefer Landing AI CAP; Phase 2 is verification and often returns N/A + meta narrative. */
export function resolveAiCorrectiveActionForPoint(p: AnalysisPoint): string {
  const landingMessage = extractAiMessage(p.landingAiResult);
  const llmMessage = extractAiMessage(p.googleAiResult);
  const landingCap = capFromMessage(landingMessage);
  if (landingCap) return landingCap;
  return capFromMessage(llmMessage);
}

/** Same cap resolution as nd-gap-point-detail (saved plan → AI corrective action). */
export function resolveCapSourceForAnalysisPoint(p: AnalysisPoint): string {
  const originalPlan = p.originalAiActionPlan?.trim() ?? '';
  const currentPlan = p.finalActionPlan?.trim() ?? originalPlan;
  if (looksLikeRegulElementAssessment(currentPlan) || looksLikeRegulElementAssessment(originalPlan)) {
    return '';
  }
  const aiCap = resolveAiCorrectiveActionForPoint(p);
  const capSource = currentPlan || originalPlan || aiCap;
  if (!capSource || isVerificationMetaCapText(capSource) || isWeakCorrectivePlan(capSource)) return '';
  return capSource;
}

/** Placeholder CAP rows (e.g. "** N/A **") must not count as real gaps. */
export function isPlaceholderGapText(text: string): boolean {
  const t = text.trim().toLowerCase().replace(/\*+/g, '').trim();
  if (isVerificationMetaCapText(text)) return true;
  return (
    !t ||
    t === 'n/a' ||
    t === '—' ||
    t === '-' ||
    t === 'none' ||
    t === 'not applicable' ||
    t === 'missing' ||
    t.includes('re-run comparison') ||
    t.includes('verify internal document')
  );
}

export function isMeaningfulCapGap(gap: CapGap): boolean {
  const missingOk = !isPlaceholderGapText(gap.missing);
  const fixOk = Boolean(gap.fix?.trim()) && !isPlaceholderGapText(gap.fix);
  return missingOk || fixOk;
}

/** True when CAP is the old generic placeholder with no real gap/action. */
export function isWeakCorrectivePlan(source: string): boolean {
  const t = source.trim();
  if (!t || t === 'N/A' || t === '—') return true;
  if (isVerificationMetaCapText(t)) return true;
  const lower = t.toLowerCase();
  if (lower.includes('re-run comparison') || lower.includes('verify internal document')) {
    // Structured plan that only wraps the placeholder is still weak.
    if (!/missing:\s*(?!missing\b).{12,}/i.test(t)) return true;
  }
  if (/missing:\s*missing\b/i.test(t) && /re-run comparison|verify internal/i.test(t)) return true;
  return false;
}

/** CAP stored as element-level assessment prose (demo seed) — not a real corrective action plan. */
export function looksLikeRegulElementAssessment(text: string): boolean {
  const t = text.trim();
  if (!t) return false;
  return (
    /^Element\s+\d+\s*\(/i.test(t) ||
    /\bElement\s+\d+[\s(].*(?:partially\s+covered|NOT\s+covered|not clearly covered)/i.test(t) ||
    /^The regulator expects\b/i.test(t) ||
    /\bno provision found\b/i.test(t)
  );
}

/** Build a readable Gap/Fix when Landing left only a placeholder CAP. */
export function buildFallbackCapGaps(requirementText: string, severity?: string | null): CapGap[] {
  const intent = summarizeRequirementIntent(requirementText);
  const priority =
    severity === 'partial_compliant' || severity === 'partial' ? 'medium' : 'higher';
  return [
    {
      index: 1,
      missing: `No equivalent internal procedure covers — ${intent}`,
      fix: 'Draft, approve, and implement an internal control/procedure that addresses this requirement; assign an owner and retain evidence of implementation.',
      priority,
    },
  ];
}

function summarizeRequirementIntent(requirementText: string): string {
  let text = requirementText.replace(/\s+/g, ' ').trim();
  if (!text) return 'the stated regulatory obligation';
  const cut = text.search(/[.;\n]/);
  if (cut > 40 && cut < 220) text = text.slice(0, cut).trim();
  if (text.length > 220) text = text.slice(0, 217).trimEnd() + '…';
  return text;
}

export function meaningfulCapGaps(source: string): CapGap[] {
  if (!source.trim()) return [];
  return parseCapGaps(source).filter(isMeaningfulCapGap);
}

/** Element-level regul/demo gaps from interpretation or gap analysis prose. */
export function parseRegulElementCapGaps(gapText: string): CapGap[] {
  const segments = parseRegulElementCapSegments(gapText);
  return segments.map((missing, i) => ({
    index: i + 1,
    missing,
    fix: '',
    priority: capPriorityForRegulCapSegment(missing),
  }));
}

function capPriorityForRegulCapSegment(segment: string): string {
  const s = segment.trim();
  if (/^The regulator/i.test(s)) return '';
  if (/\bNOT\s+covered\b/i.test(s) || /\bno provision found\b/i.test(s)) return 'higher';
  if (/\bpartially\s+covered\b/i.test(s) || /\bnot clearly covered\b/i.test(s)) return 'medium';
  if (/\bcovered\b/i.test(s)) return 'low';
  return 'medium';
}

function regulGapAnalysisTextFromPoint(p: AnalysisPoint): string {
  const landing = extractAiMessage(p.landingAiResult);
  if (!landing.trim()) return '';
  const block = parseReferenceComplianceBlock(landing);
  return normalizeRegulGapDisplayText(block.gapAnalysis ?? '');
}

export function countRegulElementGapsForPoint(p: AnalysisPoint): number {
  const gapText = regulGapAnalysisTextFromPoint(p);
  if (!gapText) return 0;
  return parseRegulElementGapSegments(gapText).length;
}

export function countCapGapsForAnalysisPoint(p: AnalysisPoint): number {
  return countDisplayGapsForAnalysisPoint(p);
}

/** Gap badges: compliant / unscored points show 0 unless manually uploaded evidence exists. */
export function countDisplayGapsForAnalysisPoint(
  p: AnalysisPoint,
  manualEvidenceCount = 0,
): number {
  const severity = resolveAnalysisPointSeverity(p);
  // Queued / pending — never invent "1 gap" before Landing has scored the point.
  if (!severity) return manualEvidenceCount > 0 ? manualEvidenceCount : 0;
  if (severity === 'compliant') return manualEvidenceCount > 0 ? manualEvidenceCount : 0;

  const elementGapCount = countRegulElementGapsForPoint(p);
  if (elementGapCount > 0) {
    return Math.max(elementGapCount, manualEvidenceCount);
  }

  const source = resolveCapSourceForAnalysisPoint(p);
  let count = meaningfulCapGaps(source).length;
  if (count === 0 && (isWeakCorrectivePlan(source) || !source.trim())) {
    count = 1;
  }
  return Math.max(count, manualEvidenceCount);
}

export function countCapGapsForReportItem(item: DualVerifyReportItem): number {
  const landingStatus = item.agreement?.landingStatus?.toLowerCase() ?? '';
  if (landingStatus === 'compliant') return 0;
  for (const msg of [item.landingMessage, item.llmMessage]) {
    if (!msg?.trim()) continue;
    const block = parseReferenceComplianceBlock(msg);
    const cap = block.correctiveAction?.trim();
    if (cap && !isVerificationMetaCapText(cap)) {
      const count = meaningfulCapGaps(cap).length;
      if (count > 0) return count;
    }
  }
  return 0;
}
