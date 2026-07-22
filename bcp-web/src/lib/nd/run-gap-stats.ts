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
