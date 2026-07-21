import type { AnalysisPoint } from './types';
import { countDisplayGapsForAnalysisPoint } from './cap-gap-count';
import type { GapPriority } from './gap-priority';

export type ActionItemReviewStatus = 'approve' | 'need_modify';

export type ActionItemReviewDraft = {
  status: ActionItemReviewStatus | '';
  comment: string;
  responsibility: string;
  dueDate: string;
  priority: GapPriority | '';
};

export type ActionItemReviewEntry = {
  id: string;
  analysisPointId: string;
  analysisReviewId?: string | null;
  actionIndex: number;
  status: ActionItemReviewStatus;
  comment?: string | null;
  responsibility?: string | null;
  dueDate?: string | null;
  priority?: GapPriority | string | null;
  createdAt: string;
};

export const ACTION_ITEM_REVIEW_OPTIONS: { id: ActionItemReviewStatus; label: string }[] = [
  { id: 'approve', label: 'Approve' },
  { id: 'need_modify', label: 'Need modify' },
];

export function emptyActionItemReviewDraft(): ActionItemReviewDraft {
  return { status: '', comment: '', responsibility: '', dueDate: '', priority: '' };
}

export function actionItemReviewKey(pointId: string, actionIndex: number): string {
  return `${pointId}:${actionIndex}`;
}

export function flattenActionItemReviews(
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
): {
  analysisPointId: string;
  actionIndex: number;
  status: ActionItemReviewStatus;
  comment?: string;
  responsibility?: string;
  dueDate?: string;
  priority?: string;
}[] {
  const rows: {
    analysisPointId: string;
    actionIndex: number;
    status: ActionItemReviewStatus;
    comment?: string;
    responsibility?: string;
    dueDate?: string;
    priority?: string;
  }[] = [];
  for (const [analysisPointId, byIndex] of Object.entries(byPoint)) {
    for (const [indexRaw, draft] of Object.entries(byIndex)) {
      if (!draft.status) continue;
      const comment = draft.comment.trim();
      const responsibility = draft.responsibility.trim();
      const dueDate = draft.dueDate.trim();
      const priority = draft.priority.trim();
      rows.push({
        analysisPointId,
        actionIndex: Number(indexRaw),
        status: draft.status,
        comment: comment || undefined,
        responsibility: responsibility || undefined,
        dueDate: dueDate || undefined,
        priority: priority || undefined,
      });
    }
  }
  return rows;
}

export function latestActionReviewsByPoint(
  entries: ActionItemReviewEntry[] | undefined,
): Record<string, Record<number, ActionItemReviewEntry>> {
  const byPoint: Record<string, Record<number, ActionItemReviewEntry>> = {};
  if (!entries?.length) return byPoint;

  const sorted = [...entries].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  );
  for (const entry of sorted) {
    const pointMap = byPoint[entry.analysisPointId] ?? {};
    if (pointMap[entry.actionIndex]) continue;
    pointMap[entry.actionIndex] = entry;
    byPoint[entry.analysisPointId] = pointMap;
  }
  return byPoint;
}

export function actionItemReviewsToDrafts(
  entries: ActionItemReviewEntry[] | undefined,
): Record<string, Record<number, ActionItemReviewDraft>> {
  const latest = latestActionReviewsByPoint(entries);
  const drafts: Record<string, Record<number, ActionItemReviewDraft>> = {};
  for (const [pointId, byIndex] of Object.entries(latest)) {
    drafts[pointId] = {};
    for (const [indexRaw, entry] of Object.entries(byIndex)) {
      drafts[pointId][Number(indexRaw)] = {
        status: entry.status,
        comment: entry.comment?.trim() ?? '',
        responsibility: entry.responsibility?.trim() ?? '',
        dueDate: entry.dueDate?.trim() ?? '',
        priority: (entry.priority as GapPriority) ?? '',
      };
    }
  }
  return drafts;
}

export function pointHasActionReviewFlag(
  pointId: string,
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
): boolean {
  const byIndex = byPoint[pointId];
  if (!byIndex) return false;
  return Object.values(byIndex).some(
    (d) =>
      d.status === 'need_modify' ||
      !!d.comment.trim() ||
      !!d.responsibility.trim() ||
      !!d.dueDate.trim() ||
      !!d.priority,
  );
}

export function actionReviewStatusLabel(status: ActionItemReviewStatus | ''): string {
  return ACTION_ITEM_REVIEW_OPTIONS.find((o) => o.id === status)?.label ?? status;
}

export type ActionReviewProgress = { total: number; reviewed: number };

export function countActionReviewProgress(
  points: AnalysisPoint[],
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
  attachmentCounts?: Record<string, number>,
): ActionReviewProgress {
  let total = 0;
  let reviewed = 0;
  for (const point of points) {
    const manualCount = attachmentCounts?.[point.id] ?? 0;
    const gapCount = countDisplayGapsForAnalysisPoint(point, manualCount);
    total += gapCount;
    const byIndex = byPoint[point.id] ?? {};
    for (let i = 1; i <= gapCount; i++) {
      if (byIndex[i]?.status) reviewed++;
    }
  }
  return { total, reviewed };
}

export function reviewsForAction(
  entries: ActionItemReviewEntry[] | undefined,
  actionIndex: number,
): ActionItemReviewEntry[] {
  if (!entries?.length) return [];
  return entries
    .filter((e) => e.actionIndex === actionIndex)
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
}

export function validateSavedActionReviewsComplete(
  points: AnalysisPoint[],
  entries: ActionItemReviewEntry[] | undefined,
  attachmentCounts?: Record<string, number>,
): { ok: boolean; message?: string } {
  let total = 0;
  let reviewed = 0;
  const reviewedKeys = new Set<string>();
  for (const e of entries ?? []) {
    if (e.status) reviewedKeys.add(`${e.analysisPointId}:${e.actionIndex}`);
  }
  for (const point of points) {
    const manualCount = attachmentCounts?.[point.id] ?? 0;
    const gapCount = countDisplayGapsForAnalysisPoint(point, manualCount);
    total += gapCount;
    for (let i = 1; i <= gapCount; i++) {
      if (reviewedKeys.has(`${point.id}:${i}`)) reviewed++;
    }
  }
  if (total === 0) return { ok: true };
  if (reviewed >= total) return { ok: true };
  return {
    ok: false,
    message: `Each action item needs at least one saved review (${reviewed}/${total} done).`,
  };
}

export function countSavedReviewProgress(
  points: AnalysisPoint[],
  entries: ActionItemReviewEntry[] | undefined,
  attachmentCounts?: Record<string, number>,
): ActionReviewProgress {
  const reviewedKeys = new Set<string>();
  for (const e of entries ?? []) {
    if (e.status) reviewedKeys.add(`${e.analysisPointId}:${e.actionIndex}`);
  }
  let total = 0;
  let reviewed = 0;
  for (const point of points) {
    const manualCount = attachmentCounts?.[point.id] ?? 0;
    const gapCount = countDisplayGapsForAnalysisPoint(point, manualCount);
    total += gapCount;
    for (let i = 1; i <= gapCount; i++) {
      if (reviewedKeys.has(`${point.id}:${i}`)) reviewed++;
    }
  }
  return { total, reviewed };
}

export function pointHasSavedReviews(pointId: string, entries: ActionItemReviewEntry[] | undefined): boolean {
  return (entries ?? []).some((e) => e.analysisPointId === pointId);
}

export function reviewsForPoint(
  entries: ActionItemReviewEntry[] | undefined,
  pointId: string,
): ActionItemReviewEntry[] {
  if (!entries?.length) return [];
  return entries
    .filter((e) => e.analysisPointId === pointId)
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
}

export function validateActionReviewsComplete(
  points: AnalysisPoint[],
  byPoint: Record<string, Record<number, ActionItemReviewDraft>>,
  attachmentCounts?: Record<string, number>,
): { ok: boolean; message?: string } {
  const { total, reviewed } = countActionReviewProgress(points, byPoint, attachmentCounts);
  if (total === 0) return { ok: true };
  if (reviewed >= total) return { ok: true };
  const remaining = total - reviewed;
  return {
    ok: false,
    message: `Review all action items before submitting (${reviewed}/${total} done, ${remaining} remaining).`,
  };
}
