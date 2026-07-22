export type RunReviewStatus = 'in_review' | 'approved' | 'needs_modification' | 'finalized';

export const RUN_REVIEW_STATUS_OPTIONS: { id: RunReviewStatus; label: string }[] = [
  { id: 'in_review', label: 'In review' },
  { id: 'approved', label: 'Approved' },
  { id: 'needs_modification', label: 'Needs modification' },
  { id: 'finalized', label: 'Finalized' },
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
    status: 'in_review',
    priority: 50,
    responsibility: '',
    dueDate: '',
    comment: '',
  };
}

export function runReviewStatusLabel(status: RunReviewStatus | string | null | undefined): string {
  return RUN_REVIEW_STATUS_OPTIONS.find((o) => o.id === status)?.label ?? String(status ?? '');
}
