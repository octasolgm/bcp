export type RunHistoryEvent = {
  id: string;
  kind: 'created' | 'review' | 'status';
  title: string;
  actorId?: string | null;
  actorName?: string | null;
  actorRole?: string | null;
  timestamp: string;
  targetRole?: string | null;
  reviewAction?: string | null;
  overallComment?: string | null;
  reviewStatus?: string | null;
  priority?: number | null;
  responsibility?: string | null;
  dueDate?: string | null;
  fromStatus?: string | null;
  toStatus?: string | null;
  gapCount?: number | null;
  actionReviewCount?: number | null;
  reviewedActionsAtEvent?: number | null;
  pointCount?: number | null;
  meta?: string[];
};

export type RunHistoryTimeline = {
  runId: string;
  runName: string;
  currentStatus: string;
  totalGaps: number;
  totalActionReviews: number;
  reviewedActions?: number;
  events: RunHistoryEvent[];
};

export function runHistoryRoleLabel(role: string | null | undefined): string {
  if (!role) return '';
  const key = role.trim().toLowerCase();
  const labels: Record<string, string> = {
    super_admin: 'Super Admin',
    maker: 'Maker',
    checker: 'Checker',
    reviewer: 'Reviewer',
  };
  return labels[key] ?? role.replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

export function runHistoryRolePillClass(role: string | null | undefined): string {
  const key = role?.trim().toLowerCase() ?? '';
  switch (key) {
    case 'super_admin':
      return 'role-pill-super';
    case 'checker':
      return 'role-pill-checker';
    case 'reviewer':
      return 'role-pill-reviewer';
    case 'maker':
      return 'role-pill-maker';
    default:
      return 'role-pill-default';
  }
}

export function actorInitials(name: string | null | undefined): string {
  const t = name?.trim() ?? '';
  if (!t) return '?';
  const parts = t.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return `${parts[0][0]}${parts[1][0]}`.toUpperCase();
  return t.slice(0, 2).toUpperCase();
}

export function runHistoryTargetLabel(target: string | null | undefined): string {
  switch (target) {
    case 'maker':
      return 'With maker';
    case 'checker':
      return 'With checker';
    case 'reviewer':
      return 'With reviewer';
    case 'complete':
      return 'Complete';
    default:
      return target ? runHistoryRoleLabel(target) : '';
  }
}
