import { isNdRunProcessing } from './nd-run-activity';
import type { AnalysisRunSummary } from './types';

export type NdNavBadgeBumps = {
  analysisRunsAll?: number;
  analysisRunsInProgress?: number;
  analysisRunsCorrection?: number;
  adminDeletedRuns?: number;
};

/** Optimistic sidebar deltas when a run is soft-deleted from the workspace. */
export function bumpsForAnalysisRunSoftDelete(
  run: AnalysisRunSummary,
  isSuperAdmin: boolean,
  bumpDeletedRuns = true,
): NdNavBadgeBumps {
  const st = (run.status ?? '').toLowerCase();
  return {
    analysisRunsAll: -1,
    analysisRunsInProgress: isNdRunProcessing(run) ? -1 : undefined,
    analysisRunsCorrection: st === 'pulled_back' ? -1 : undefined,
    adminDeletedRuns: isSuperAdmin && bumpDeletedRuns ? 1 : undefined,
  };
}

/** Reverse soft-delete bumps (e.g. restore from admin). */
export function bumpsForAnalysisRunRestore(
  run: AnalysisRunSummary,
  isSuperAdmin: boolean,
): NdNavBadgeBumps {
  const st = (run.statusBeforeDelete ?? run.status ?? '').toLowerCase();
  return {
    analysisRunsAll: 1,
    analysisRunsInProgress: isNdRunProcessing(run) ? 1 : undefined,
    analysisRunsCorrection: st === 'pulled_back' ? 1 : undefined,
    adminDeletedRuns: isSuperAdmin ? -1 : undefined,
  };
}
