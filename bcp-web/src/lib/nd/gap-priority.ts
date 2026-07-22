export type GapPriority = 'low' | 'medium' | 'higher';

export const GAP_PRIORITY_OPTIONS: { id: GapPriority; label: string; score: number }[] = [
  { id: 'low', label: 'Low', score: 25 },
  { id: 'medium', label: 'Medium', score: 50 },
  { id: 'higher', label: 'Critical', score: 85 },
];

export function gapPriorityLabel(priority: GapPriority | '' | null | undefined): string {
  if (!priority) return '';
  return GAP_PRIORITY_OPTIONS.find((o) => o.id === priority)?.label ?? priority;
}

export function normalizeGapPriority(raw: string | null | undefined): GapPriority | '' {
  const t = raw?.trim().toLowerCase() ?? '';
  if (t === 'low') return 'low';
  if (t === 'medium') return 'medium';
  if (t === 'higher' || t === 'high' || t === 'critical') return 'higher';
  return '';
}

export function gapPriorityClass(priority: GapPriority | '' | null | undefined): string {
  if (priority === 'higher') return 'gap-priority-higher';
  if (priority === 'medium') return 'gap-priority-medium';
  if (priority === 'low') return 'gap-priority-low';
  return '';
}

export function gapPriorityScore(priority: GapPriority | '' | null | undefined): number {
  if (!priority) return 50;
  return GAP_PRIORITY_OPTIONS.find((o) => o.id === priority)?.score ?? 50;
}
