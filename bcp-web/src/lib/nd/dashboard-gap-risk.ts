import { meaningfulCapGaps, resolveCapSourceForAnalysisPoint } from './cap-gap-count';
import {
  addGapRiskCount,
  emptyGapRiskCounts,
  riskScoreFromRaw,
  type GapRiskCounts,
} from './risk-priority-score';
import { resolveAnalysisPointSeverity } from './point-compliance-status';
import type { AnalysisPoint } from './types';

export type ActionItemReviewRow = {
  analysisPointId: string;
  actionIndex: number;
  priority?: string | number | null;
};

/** Count gaps across analysis points using CAP priority + latest action review priority. */
export function aggregateGapRiskCounts(
  points: AnalysisPoint[],
  actionItemReviews: ActionItemReviewRow[],
): GapRiskCounts {
  let counts = emptyGapRiskCounts();

  const reviewPriority = new Map<string, string | number>();
  for (const rev of actionItemReviews) {
    const key = `${rev.analysisPointId}:${rev.actionIndex}`;
    if (rev.priority != null && rev.priority !== '') {
      reviewPriority.set(key, rev.priority);
    }
  }

  for (const point of points) {
    const severity = resolveAnalysisPointSeverity(point);
    if (severity === 'compliant') continue;
    const gaps = meaningfulCapGaps(resolveCapSourceForAnalysisPoint(point));
    if (gaps.length) {
      for (const gap of gaps) {
        const key = `${point.id}:${gap.index}`;
        const fromReview = reviewPriority.get(key);
        const score = riskScoreFromRaw(fromReview ?? gap.priority ?? '');
        counts = addGapRiskCount(counts, score);
      }
      continue;
    }
    // In-progress / unscored CAP: still count the finding by compliance severity.
    const fallbackScore = severity === 'non_compliant' ? 85 : 50;
    counts = addGapRiskCount(counts, fallbackScore);
  }

  return counts;
}

export function mergeGapRiskCounts(a: GapRiskCounts, b: GapRiskCounts): GapRiskCounts {
  return {
    critical: a.critical + b.critical,
    medium: a.medium + b.medium,
    low: a.low + b.low,
    total: a.total + b.total,
  };
}
