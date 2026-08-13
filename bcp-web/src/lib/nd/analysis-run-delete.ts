import { isDemoOwnedAnalysisRun } from './demo-analysis-routes';
import type { AnalysisRunSummary } from './types';

export function isPermanentDemoAnalysisDelete(
  run: AnalysisRunSummary,
  isDemoViewer: boolean,
): boolean {
  return isDemoOwnedAnalysisRun(run) && !isDemoViewer;
}

export function wasAnalysisRunPermanentlyDeleted(
  res: {
    permanentlyDeleted?: boolean;
    data?: unknown;
  },
): boolean {
  if (res.permanentlyDeleted === true) return true;
  const data = res.data as { permanentlyDeleted?: boolean } | null | undefined;
  return data?.permanentlyDeleted === true;
}
