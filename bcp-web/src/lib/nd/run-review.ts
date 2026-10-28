import { ACTION_PLAN_STATUS_OPTIONS, type ActionPlanStatus } from './action-plan';

export type RunReviewStatus = ActionPlanStatus | 'in_review' | 'approved' | 'needs_modification' | 'finalized';

export const RUN_REVIEW_STATUS_OPTIONS: { id: RunReviewStatus; label: string }[] = [
  ...ACTION_PLAN_STATUS_OPTIONS.map((opt) => ({ id: opt.value as RunReviewStatus, label: opt.label })),
];

export type RunReviewDraft = {
  status: RunReviewStatus;
  priority: number;
  responsibility: string;
  dueDate: string;
  comment: string;
};

export function emptyRunReviewDraft(): RunReviewDraft {
  return {
    status: 'pending',
    priority: 50,
    responsibility: '',
    dueDate: '',
    comment: '',
  };
}

export function runReviewStatusLabel(status: RunReviewStatus | string | null | undefined): string {
  return RUN_REVIEW_STATUS_OPTIONS.find((o) => o.id === status)?.label
    ?? ACTION_PLAN_STATUS_OPTIONS.find((o) => o.value === status)?.label
    ?? String(status ?? '').replace(/_/g, ' ');
}
