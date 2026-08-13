import type { AnalysisPoint } from './types';

export const REGUL_PIPELINE_V3 = 'regul_pipeline';
export const REGUL_PIPELINE_FULL = 'regul_pipeline_full';

/** Regul pipeline run progress fields (API primary names for regul workflow engines). */
export type RegulRunProgress = {
  regulClauseTotal?: number;
  regulClauseCompleted?: number;
  regulClauseFailed?: number;
  regulReverseSectionTotal?: number | null;
  regulReverseSectionCompleted?: number | null;
  regulReverseSectionFailed?: number | null;
  regulLlmProvider?: string | null;
  regulLlmModel?: string | null;
};

export function isRegulWorkflow(workflowEngine?: string | null): boolean {
  const engine = (workflowEngine ?? '').trim().toLowerCase();
  return engine === REGUL_PIPELINE_V3 || engine === REGUL_PIPELINE_FULL;
}

export function isRegulFullMarkdownWorkflow(workflowEngine?: string | null): boolean {
  return (workflowEngine ?? '').trim().toLowerCase() === REGUL_PIPELINE_FULL;
}

/** Normalize API point: prefer regulForward* when present. */
export function normalizeRegulPoint<T extends AnalysisPoint>(p: T): T {
  const raw = p as T & {
    regulForwardStatus?: string | null;
    regulForwardResult?: string | null;
    regulForwardError?: string | null;
  };
  if (!raw.regulForwardStatus && !raw.regulForwardError && !raw.regulForwardResult) {
    return p;
  }
  return {
    ...p,
    landingAiStatus: raw.regulForwardStatus ?? p.landingAiStatus,
    landingAiResult: raw.regulForwardResult ?? p.landingAiResult,
    landingAiError: raw.regulForwardError ?? p.landingAiError,
    regulForwardStatus: raw.regulForwardStatus ?? p.landingAiStatus,
    regulForwardResult: raw.regulForwardResult ?? p.landingAiResult,
    regulForwardError: raw.regulForwardError ?? p.landingAiError,
  };
}

export function regulForwardError(p: AnalysisPoint): string | null | undefined {
  const ext = p as AnalysisPoint & { regulForwardError?: string | null };
  return ext.regulForwardError ?? p.landingAiError;
}

export function regulForwardStatus(p: AnalysisPoint): string {
  const ext = p as AnalysisPoint & { regulForwardStatus?: string | null };
  return ext.regulForwardStatus ?? p.landingAiStatus;
}

export function regulClauseFailedCount(run: RegulRunProgress & { dualVerifyFailedCount?: number }): number {
  return run.regulClauseFailed ?? run.dualVerifyFailedCount ?? 0;
}

export function regulClauseCompletedCount(
  run: RegulRunProgress & { landingAiCompletedCount?: number; processedPointsCount?: number },
): number {
  return run.regulClauseCompleted ?? run.landingAiCompletedCount ?? run.processedPointsCount ?? 0;
}

export function regulClauseTotalCount(
  run: RegulRunProgress & { totalPointsCount?: number },
): number {
  return run.regulClauseTotal ?? run.totalPointsCount ?? 0;
}

/** Full element-level gap assessment from demo seed interpretation (not truncated). */
export function normalizeRegulGapDisplayText(gapRaw: string): string {
  return gapRaw.trim();
}

/**
 * Split full regul/demo interpretation into CAP actions — preamble + every Element segment.
 * Does not drop covered elements or regulator preamble (matches Excel / seed JSON).
 */
export function parseRegulElementCapSegments(gapText: string): string[] {
  const text = gapText.trim();
  if (!text) return [];

  const parts = text.split(/(?=Element\s+\d+\s*\()/i).map((s) => s.trim()).filter(Boolean);
  const segments: string[] = [];

  if (parts.length > 0 && !/^Element\s+\d+/i.test(parts[0])) {
    segments.push(parts[0]);
  }

  for (const part of parts) {
    if (/^Element\s+\d+/i.test(part)) {
      segments.push(part);
    }
  }

  if (!segments.length) return [text];
  return segments;
}

/** Seed / Excel gap phrases beyond strict "NOT covered" / "partially covered". */
const REGUL_ELEMENT_GAP_PHRASE_RE =
  /\bNOT\s+covered\b|\bpartially\s+covered\b|\bnot clearly covered\b|\bnot clearly addressed\b|\bnot explicitly covered\b|\bnot explicitly addressed\b/i;

const REGUL_PROSE_GAP_PHRASE_RE =
  /\bno provision found\b|\bnot (?:clearly|explicitly) (?:covered|addressed)\b|\bdoes not (?:mention|address|cover|require|prohibit|establish)\b|\bno explicit\b/i;

function isRegulElementGapPart(part: string): boolean {
  if (!/^Element\s+\d+/i.test(part)) return false;
  return REGUL_ELEMENT_GAP_PHRASE_RE.test(part);
}

/** Narrative gap blocks (e.g. "No provision found…") with no Element N structure. */
function parseRegulProseGapSegments(text: string): string[] {
  const prose = text.trim();
  if (!prose || /\bElement\s+\d+/i.test(prose)) return [];
  if (!REGUL_PROSE_GAP_PHRASE_RE.test(prose)) return [];

  const paragraphs = prose.split(/\n\n+/).map((p) => p.trim()).filter(Boolean);
  if (paragraphs.length > 1 && /^the regulator/i.test(paragraphs[0])) {
    const body = paragraphs.slice(1).join('\n\n').trim();
    return body ? [body] : [];
  }
  return [prose];
}

/** Split demo/regul element assessments into separate gaps (NOT covered / partially covered only). */
export function parseRegulElementGapSegments(gapText: string): string[] {
  const text = stripRegulGapPreamble(gapText);
  if (!text) return [];

  const parts = text.split(/(?=Element\s+\d+\s*\()/i).map((s) => s.trim()).filter(Boolean);
  const gaps: string[] = [];

  for (const part of parts) {
    if (isRegulElementGapPart(part)) {
      gaps.push(part);
    }
  }

  if (!gaps.length && REGUL_ELEMENT_GAP_PHRASE_RE.test(text)) {
    const alt = text.split(/(?=\.\s*Element\s+\d+\s*\()/i).map((s) => s.trim()).filter(Boolean);
    for (const part of alt) {
      const normalized = part.replace(/^\.\s*/, '');
      if (isRegulElementGapPart(normalized)) {
        gaps.push(normalized);
      }
    }
  }

  if (!gaps.length) {
    gaps.push(...parseRegulProseGapSegments(text));
  }

  return gaps;
}

/** Legacy helper — strip preamble for gap-count rails (not for CAP display). */
function stripRegulGapPreamble(gapRaw: string): string {
  const t = gapRaw.trim();
  if (!t) return '';

  const paragraphs = t.split(/\n\n+/).map((p) => p.trim()).filter(Boolean);
  if (paragraphs.length > 1 && /^the regulator/i.test(paragraphs[0])) {
    return paragraphs.slice(1).join('\n\n');
  }

  const elementBy = t.match(/Element-by-element assessment:\s*/i);
  if (elementBy?.index != null && elementBy.index >= 0) {
    return t.slice(elementBy.index).trim();
  }

  const element = t.match(/\bElement\s+\d+/i);
  if (element?.index != null && element.index > 20) {
    return t.slice(element.index).trim();
  }

  return t;
}
