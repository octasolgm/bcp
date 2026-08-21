import { countSavedReviewProgress, type ActionItemReviewEntry } from './action-item-review';
import type { AnalysisPoint } from './types';

export type RunGapStatsSummary = {
  totalGaps: number;
  reviewedActions: number;
  totalReviews: number;
};

export function computeRunGapStats(
  points: AnalysisPoint[],
  actionItemReviews: ActionItemReviewEntry[] | undefined,
  attachmentCounts?: Record<string, number>,
): RunGapStatsSummary {
  const { total, reviewed } = countSavedReviewProgress(points, actionItemReviews, attachmentCounts);
  return {
    totalGaps: total,
    reviewedActions: reviewed,
    totalReviews: actionItemReviews?.length ?? 0,
  };
}

export function runGapStatsFromSummary(run: {
  totalGaps?: number;
  reviewedGaps?: number;
  totalReviews?: number;
}): RunGapStatsSummary | null {
  if (run.totalGaps == null || run.totalGaps <= 0) return null;
  return {
    totalGaps: run.totalGaps,
    reviewedActions: run.reviewedGaps ?? 0,
    totalReviews: run.totalReviews ?? 0,
  };
}

/** Gap and action tallies a run carries, for the chips on the analysis lists. */
export type RunWorkCounts = {
  gaps: number;
  resolvedGaps: number;
  pendingGaps: number;
  actions: number;
  resolvedActions: number;
  pendingActions: number;
};

/**
 * Gap and action tallies for a run summary, or null when there is nothing to show.
 * Gap rows are registered when a report is first opened, so a run nobody has viewed
 * falls back to the gap count the pipeline recorded.
 */
export function runWorkCounts(run: {
  gapCount?: number;
  resolvedGapCount?: number;
  actionPlanCount?: number;
  resolvedActionPlanCount?: number;
  totalGaps?: number;
}): RunWorkCounts | null {
  const gaps = run.gapCount || run.totalGaps || 0;
  const actions = run.actionPlanCount ?? 0;
  if (!gaps && !actions) return null;

  const resolvedGaps = Math.min(run.resolvedGapCount ?? 0, gaps);
  const resolvedActions = Math.min(run.resolvedActionPlanCount ?? 0, actions);
  return {
    gaps,
    resolvedGaps,
    pendingGaps: gaps - resolvedGaps,
    actions,
    resolvedActions,
    pendingActions: actions - resolvedActions,
  };
}

export function normalizeHistoryMetaChip(chip: string, totalGaps: number, reviewedActions?: number): string {
  const trimmed = chip.trim();
  if (/^\d+\s+gaps?$/i.test(trimmed)) {
    return `${totalGaps} gap${totalGaps === 1 ? '' : 's'}`;
  }
  if (/^\d+\/\d+\s+reviewed$/i.test(trimmed) && reviewedActions != null && totalGaps > 0) {
    return `${reviewedActions}/${totalGaps} reviewed`;
  }
  return chip;
}
