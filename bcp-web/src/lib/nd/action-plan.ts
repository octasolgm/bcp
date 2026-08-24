/**
 * Corrective action plans attached to a gap. A gap can carry many action plans, and a
 * checker/reviewer can leave comment-only reviews against each plan.
 */

export type ActionPlanStatus = 'pending' | 'resolved';
export type ActionPlanPriority = 'low' | 'medium' | 'high';
export type ActionPlanResponsibilityType = 'department' | 'user';

export type ActionPlanReviewEntry = {
  id: string;
  actionPlanId: string;
  analysisPointId: string;
  comment: string;
  reviewerId?: string | null;
  reviewerName?: string | null;
  reviewerRole?: string | null;
  /** Department or person the review was addressed to; absent for a plain note. */
  assigneeType?: ActionPlanResponsibilityType | null;
  assigneeDepartmentId?: string | null;
  assigneeUserId?: string | null;
  assigneeLabel?: string | null;
  createdAt: string;
  updatedAt?: string | null;
};

/**
 * One owner of an action. An action can be shared by several departments and/or people;
 * each owner sees it in their inbox. An entry with no id is free text the user typed.
 */
export type ActionPlanAssignee = {
  id?: string;
  type: ActionPlanResponsibilityType;
  departmentId?: string | null;
  userId?: string | null;
  label: string;
};

export type ActionPlanEntry = {
  id: string;
  analysisRunId: string;
  analysisPointId: string;
  /** 1-based CAP gap this action belongs to; 0 = attached to the point as a whole. */
  gapIndex?: number | null;
  actionPlan: string;
  status: ActionPlanStatus;
  priority: ActionPlanPriority;
  /** 0–100 slider score; `priority` is the tier it falls into. */
  priorityScore?: number | null;
  targetDate?: string | null;
  responsibilityType: ActionPlanResponsibilityType;
  responsibilityDepartmentId?: string | null;
  responsibilityUserId?: string | null;
  responsibilityName?: string | null;
  assignees?: ActionPlanAssignee[];
  comment?: string | null;
  sortOrder?: number | null;
  resolvedAt?: string | null;
  resolvedBy?: string | null;
  resolvedByName?: string | null;
  createdBy?: string | null;
  createdByName?: string | null;
  updatedByName?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  reviews: ActionPlanReviewEntry[];
  reviewCount: number;
};

export type ActionPlanTargetDateChange = {
  id: string;
  previousTargetDate?: string | null;
  newTargetDate?: string | null;
  reason?: string | null;
  changedBy?: string | null;
  changedByName?: string | null;
  createdAt: string;
};

export type ActionPlanResponsibilityOptions = {
  departments: { id: string; name: string }[];
  users: { id: string; fullName: string; email?: string | null; role?: string | null }[];
};

export type ActionPlanDraft = {
  actionPlan: string;
  status: ActionPlanStatus;
  priorityScore: number;
  targetDate: string;
  /** Which tab the owner picker is showing; owners of both kinds can coexist. */
  responsibilityType: ActionPlanResponsibilityType;
  assignees: ActionPlanAssignee[];
  comment: string;
  targetDateChangeReason: string;
};

/** Owners as stored on a plan, falling back to the single legacy responsibility field. */
export function assigneesForPlan(plan: ActionPlanEntry): ActionPlanAssignee[] {
  if (plan.assignees?.length) return plan.assignees;
  if (plan.responsibilityUserId || plan.responsibilityDepartmentId || plan.responsibilityName) {
    return [
      {
        type: plan.responsibilityType === 'user' ? 'user' : 'department',
        departmentId: plan.responsibilityDepartmentId ?? null,
        userId: plan.responsibilityUserId ?? null,
        label: plan.responsibilityName ?? '',
      },
    ];
  }
  return [];
}

export function sameAssignee(a: ActionPlanAssignee, b: ActionPlanAssignee): boolean {
  if (a.type !== b.type) return false;
  if (a.userId || b.userId) return a.userId === b.userId;
  if (a.departmentId || b.departmentId) return a.departmentId === b.departmentId;
  return a.label.trim().toLowerCase() === b.label.trim().toLowerCase();
}

export const ACTION_PLAN_PRIORITY_OPTIONS: { value: ActionPlanPriority; label: string }[] = [
  { value: 'low', label: 'Low' },
  { value: 'medium', label: 'Medium' },
  { value: 'high', label: 'High' },
];

export const ACTION_PLAN_STATUS_OPTIONS: { value: ActionPlanStatus; label: string }[] = [
  { value: 'pending', label: 'Pending' },
  { value: 'resolved', label: 'Resolved' },
];

export function emptyActionPlanDraft(): ActionPlanDraft {
  return {
    actionPlan: '',
    status: 'pending',
    priorityScore: DEFAULT_ACTION_PLAN_PRIORITY_SCORE,
    targetDate: '',
    responsibilityType: 'department',
    assignees: [],
    comment: '',
    targetDateChangeReason: '',
  };
}

export function draftFromActionPlan(plan: ActionPlanEntry): ActionPlanDraft {
  return {
    actionPlan: plan.actionPlan ?? '',
    status: normalizeActionPlanStatus(plan.status),
    priorityScore: actionPlanPriorityScore(plan),
    targetDate: toDateInputValue(plan.targetDate),
    responsibilityType: plan.responsibilityType === 'user' ? 'user' : 'department',
    assignees: assigneesForPlan(plan).map((a) => ({ ...a })),
    comment: plan.comment ?? '',
    targetDateChangeReason: '',
  };
}

/** Score bands mirror the shared risk standard: 0–33 low, 34–66 medium, 67–100 high. */
export const ACTION_PLAN_PRIORITY_BANDS = {
  low: { max: 33, label: 'Low' },
  medium: { max: 66, label: 'Medium' },
  high: { max: 100, label: 'High' },
} as const;

export const ACTION_PLAN_PRIORITY_SCALE = 'Priority score 0–100 · 0–33 Low · 34–66 Medium · 67–100 High';

export const DEFAULT_ACTION_PLAN_PRIORITY_SCORE = 50;

export function clampActionPlanScore(score: number): number {
  if (!Number.isFinite(score)) return DEFAULT_ACTION_PLAN_PRIORITY_SCORE;
  return Math.min(100, Math.max(0, Math.round(score)));
}

export function actionPlanPriorityFromScore(score: number): ActionPlanPriority {
  const s = clampActionPlanScore(score);
  if (s <= ACTION_PLAN_PRIORITY_BANDS.low.max) return 'low';
  if (s <= ACTION_PLAN_PRIORITY_BANDS.medium.max) return 'medium';
  return 'high';
}

/** Midpoint score for a tier, for rows saved before the slider existed. */
export function actionPlanScoreFromPriority(value?: string | null): number {
  const tier = normalizeActionPlanPriority(value);
  return tier === 'low' ? 20 : tier === 'high' ? 85 : DEFAULT_ACTION_PLAN_PRIORITY_SCORE;
}

/** A plan's slider position — falls back to its tier when the API sent no score. */
export function actionPlanPriorityScore(plan: ActionPlanEntry): number {
  if (typeof plan.priorityScore === 'number') {
    const score = clampActionPlanScore(plan.priorityScore);
    if (actionPlanPriorityFromScore(score) === normalizeActionPlanPriority(plan.priority)) return score;
  }
  return actionPlanScoreFromPriority(plan.priority);
}

export function actionPlanScoreLabel(score: number): string {
  const s = clampActionPlanScore(score);
  return `${s} · ${ACTION_PLAN_PRIORITY_BANDS[actionPlanPriorityFromScore(s)].label}`;
}

export function normalizeActionPlanPriority(value?: string | null): ActionPlanPriority {
  const v = (value ?? '').trim().toLowerCase();
  if (v === 'low') return 'low';
  if (v === 'high' || v === 'higher' || v === 'critical') return 'high';
  return 'medium';
}

export function normalizeActionPlanStatus(value?: string | null): ActionPlanStatus {
  return (value ?? '').trim().toLowerCase() === 'resolved' ? 'resolved' : 'pending';
}

export function actionPlanPriorityLabel(value?: string | null): string {
  const p = normalizeActionPlanPriority(value);
  return p === 'low' ? 'Low' : p === 'high' ? 'High' : 'Medium';
}

export function actionPlanPriorityClass(value?: string | null): string {
  return `ap-priority-${normalizeActionPlanPriority(value)}`;
}

export function actionPlanStatusLabel(value?: string | null): string {
  return normalizeActionPlanStatus(value) === 'resolved' ? 'Resolved' : 'Pending';
}

/** ISO timestamp → yyyy-MM-dd for <input type="date">. */
export function toDateInputValue(iso?: string | null): string {
  if (!iso?.trim()) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** Human-readable date (no time — target dates and audit stamps are day-level). */
export function formatActionPlanDate(iso?: string | null): string {
  if (!iso?.trim()) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' });
}

/** Calendar-aware year/month/day breakdown between two dates, larger unit first. */
function calendarSpan(from: Date, to: Date): { years: number; months: number; days: number } {
  let years = to.getFullYear() - from.getFullYear();
  let months = to.getMonth() - from.getMonth();
  let days = to.getDate() - from.getDate();

  if (days < 0) {
    months -= 1;
    days += new Date(to.getFullYear(), to.getMonth(), 0).getDate();
  }
  if (months < 0) {
    years -= 1;
    months += 12;
  }
  return { years, months, days };
}

/** "1 year 2 months 3 days" — drops leading zero units so short spans read as just "3 days". */
function calendarSpanLabel(span: { years: number; months: number; days: number }): string {
  const { years, months, days } = span;
  const parts: string[] = [];
  if (years > 0) parts.push(`${years} year${years === 1 ? '' : 's'}`);
  if (months > 0) parts.push(`${months} month${months === 1 ? '' : 's'}`);
  if (days > 0 || parts.length === 0) parts.push(`${days} day${days === 1 ? '' : 's'}`);
  return parts.join(' ');
}

/**
 * Calendar-aware countdown to a target date — "1 year 2 months 3 days left", dropping
 * whichever leading units are zero (months+days only, or just days, when the date is
 * close), so the inbox doesn't force everyone to do the year-month-day math themselves.
 */
export function formatActionPlanRemaining(iso?: string | null): string {
  if (!iso?.trim()) return '';
  const target = new Date(iso);
  if (Number.isNaN(target.getTime())) return '';

  const now = new Date();
  const overdue = target.getTime() < now.getTime();
  const span = overdue ? calendarSpan(target, now) : calendarSpan(now, target);
  const label = calendarSpanLabel(span);

  if (overdue) return `${label} overdue`;
  if (span.years === 0 && span.months === 0 && span.days === 0) return 'Due today';
  return `${label} left`;
}

/** Same calendar-aware breakdown, phrased backwards — "3 days ago" for a past timestamp. */
export function formatRelativeTimeAgo(iso?: string | null): string {
  if (!iso?.trim()) return '';
  const past = new Date(iso);
  if (Number.isNaN(past.getTime())) return '';

  const now = new Date();
  const span = calendarSpan(past, now);
  if (span.years === 0 && span.months === 0 && span.days === 0) return 'Today';
  return `${calendarSpanLabel(span)} ago`;
}

export function isActionPlanOverdue(plan: ActionPlanEntry): boolean {
  if (normalizeActionPlanStatus(plan.status) === 'resolved') return false;
  if (!plan.targetDate) return false;
  const d = new Date(plan.targetDate);
  return !Number.isNaN(d.getTime()) && d.getTime() < Date.now();
}

export function actionPlansForPoint(
  plans: ActionPlanEntry[],
  analysisPointId: string,
): ActionPlanEntry[] {
  return plans
    .filter((p) => p.analysisPointId === analysisPointId)
    .sort((a, b) => (a.gapIndex ?? 0) - (b.gapIndex ?? 0) || (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
}

/**
 * Actions belonging to one CAP gap. Rows written before gaps were scoped carry index 0,
 * so the first gap adopts them rather than letting them disappear from the UI.
 */
export function actionPlansForGap(
  plans: ActionPlanEntry[],
  gapIndex: number,
): ActionPlanEntry[] {
  return plans
    .filter((p) => (p.gapIndex ?? 0) === gapIndex || (gapIndex === 1 && !p.gapIndex))
    .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
}

export function countActionPlanReviews(plans: ActionPlanEntry[]): number {
  return plans.reduce((sum, p) => sum + (p.reviewCount ?? p.reviews?.length ?? 0), 0);
}
