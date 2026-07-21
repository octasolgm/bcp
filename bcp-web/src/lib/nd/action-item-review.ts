export type ActionItemReviewStatus = 'approve' | 'need_modify' | 'uix';

export type ActionItemReviewDraft = {
  status: ActionItemReviewStatus | '';
  comment: string;
};

export type ActionItemReviewEntry = {
  id: string;
  analysisPointId: string;
  analysisReviewId?: string | null;
  actionIndex: number;
  status: ActionItemReviewStatus;
  comment?: string | null;
  createdAt: string;
};

export const ACTION_ITEM_REVIEW_OPTIONS: { id: ActionItemReviewStatus; label: string }[] = [
  { id: 'approve', label: 'Approve' },
  { id: 'need_modify', label: 'Need modify' },
  { id: 'uix', label: 'UIX' },
];

export function actionItemReviewKey(pointId: string, actionIndex: number): string {
  return `${pointId}:${actionIndex}`;
}

export function flattenActionItemReviews(
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
): { analysisPointId: string; actionIndex: number; status: ActionItemReviewStatus; comment?: string }[] {
  const rows: { analysisPointId: string; actionIndex: number; status: ActionItemReviewStatus; comment?: string }[] =
    [];
  for (const [analysisPointId, byIndex] of Object.entries(byPoint)) {
    for (const [indexRaw, draft] of Object.entries(byIndex)) {
      if (!draft.status) continue;
      const comment = draft.comment.trim();
      rows.push({
        analysisPointId,
        actionIndex: Number(indexRaw),
        status: draft.status,
        comment: comment || undefined,
      });
    }
  }
  return rows;
}

export function pointHasActionReviewFlag(
  pointId: string,
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
): boolean {
  const byIndex = byPoint[pointId];
  if (!byIndex) return false;
  return Object.values(byIndex).some(
    (d) => d.status === 'need_modify' || d.status === 'uix' || !!d.comment.trim(),
  );
}
