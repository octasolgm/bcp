import { isRegulFullMarkdownWorkflow } from './regul-fields';
import type { AnalysisRunSummary } from './types';

/** Default new-analysis route (Regul full markdown, forward-only). */
export const ND_NEW_ANALYSIS_PATH = '/nd/analyse-regul-full';

/** @deprecated Use {@link ND_NEW_ANALYSIS_PATH}. */
export const ND_DEMO_NEW_ANALYSIS_PATH = ND_NEW_ANALYSIS_PATH;

export function isDemoOwnedAnalysisRun(
  run: { createdByIsDemo?: boolean; makerName?: string },
): boolean {
  return (
    run.createdByIsDemo === true ||
    (run.makerName ?? '').toLowerCase().includes('demo')
  );
}

export function ndNewAnalysisRoute(): string[] {
  return [ND_NEW_ANALYSIS_PATH];
}

export function ndExecutionAnalysisRoute(
  run: Pick<AnalysisRunSummary, 'workflowEngine' | 'createdByIsDemo' | 'makerName'>,
  demoViewer: boolean,
): string[] {
  if (
    demoViewer ||
    isDemoOwnedAnalysisRun(run) ||
    isRegulFullMarkdownWorkflow(run.workflowEngine)
  ) {
    return [ND_NEW_ANALYSIS_PATH];
  }
  return ['/nd/analyse-v8'];
}

export type NdAnalysisRunLinkOpts = {
  demoViewer?: boolean;
};
