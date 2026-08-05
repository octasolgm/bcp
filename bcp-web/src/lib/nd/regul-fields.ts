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
