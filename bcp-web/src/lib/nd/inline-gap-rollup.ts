import { emptyClauseRollup, type ClauseRollup } from './gap-state';

/** Rough gap/action tallies for pre-save inline gap reports (session / dual-verify draft). */
export function rollupFromInlineGapItems(
  items: { severity: string; gapCount?: number; gaps?: string }[],
): ClauseRollup {
  const out = emptyClauseRollup();
  for (const item of items) {
    const raw = item.gapCount ?? (item.gaps?.trim() ? 1 : 0);
    const n = Math.max(0, raw);
    if (n === 0) continue;
    if (item.severity === 'compliant') continue;
    out.gaps += n;
    out.pendingGaps += n;
    out.actions += n;
    out.pendingActions += n;
  }
  return out;
}
