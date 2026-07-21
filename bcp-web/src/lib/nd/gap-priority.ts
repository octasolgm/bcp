export type GapPriority = 'medium' | 'higher';

export const GAP_PRIORITY_OPTIONS: { id: GapPriority; label: string }[] = [
  { id: 'medium', label: 'Medium' },
  { id: 'higher', label: 'Higher' },
];

export function gapPriorityLabel(priority: GapPriority | '' | null | undefined): string {
  if (!priority) return '';
  return GAP_PRIORITY_OPTIONS.find((o) => o.id === priority)?.label ?? priority;
}

export function normalizeGapPriority(raw: string | null | undefined): GapPriority | '' {
  const t = raw?.trim().toLowerCase() ?? '';
  if (t === 'medium') return 'medium';
  if (t === 'higher' || t === 'high') return 'higher';
  return '';
}

export function gapPriorityClass(priority: GapPriority | '' | null | undefined): string {
  if (priority === 'higher') return 'gap-priority-higher';
  if (priority === 'medium') return 'gap-priority-medium';
  return '';
}
