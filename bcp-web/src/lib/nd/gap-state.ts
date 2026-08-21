/**
 * State for a gap raised against a clause.
 *
 * The gap text itself lives in the clause's CAP blob, but its risk and its resolve
 * state are rows of their own. That separation is what lets one gap be closed while
 * its siblings on the same clause stay open and editable.
 *
 * The chain the whole report follows is clause -> gap -> action plan -> review.
 */

import {
  actionPlanPriorityFromScore,
  actionPlanScoreFromPriority,
  clampActionPlanScore,
  normalizeActionPlanStatus,
  type ActionPlanEntry,
  type ActionPlanPriority,
} from './action-plan';

export type GapStatus = 'pending' | 'resolved';

export type GapState = {
  id: string;
  analysisRunId: string;
  analysisPointId: string;
  gapIndex: number;
  risk: ActionPlanPriority;
  riskScore: number;
  status: GapStatus;
  resolvedAt?: string | null;
  resolvedBy?: string | null;
  resolvedByName?: string | null;
  updatedAt?: string | null;
};

export function normalizeGapStatus(value?: string | null): GapStatus {
  return (value ?? '').trim().toLowerCase() === 'resolved' ? 'resolved' : 'pending';
}

export function gapStatusLabel(value?: string | null): string {
  return normalizeGapStatus(value) === 'resolved' ? 'Resolved' : 'Pending';
}

/** Index gap rows by `${pointId}:${gapIndex}` for O(1) lookup while rendering. */
export function gapStateKey(pointId: string, gapIndex: number): string {
  return `${pointId}:${gapIndex}`;
}

export function indexGapStates(rows: GapState[]): Map<string, GapState> {
  const map = new Map<string, GapState>();
  for (const row of rows) map.set(gapStateKey(row.analysisPointId, row.gapIndex), row);
  return map;
}

/**
 * What a gap's status would be given its actions: resolved once every action on it is
 * resolved. A gap with no actions has nothing to derive from, so the stored value stands.
 */
export function deriveGapStatus(
  plans: ActionPlanEntry[],
  stored?: GapState | null,
): GapStatus {
  if (!plans.length) return normalizeGapStatus(stored?.status);
  return plans.every((p) => normalizeActionPlanStatus(p.status) === 'resolved')
    ? 'resolved'
    : 'pending';
}

/** A gap cannot be closed by hand while any of its actions is still open. */
export function canResolveGapByHand(plans: ActionPlanEntry[]): boolean {
  return plans.every((p) => normalizeActionPlanStatus(p.status) === 'resolved');
}

export function gapRiskScore(state?: GapState | null, fallbackRisk?: string | null): number {
  if (state && typeof state.riskScore === 'number') {
    const score = clampActionPlanScore(state.riskScore);
    if (actionPlanPriorityFromScore(score) === state.risk) return score;
  }
  return actionPlanScoreFromPriority(state?.risk ?? fallbackRisk);
}

/** Days a team gets to close an action, set by the risk of the gap it hangs off. */
export const GAP_RISK_TARGET_DAYS: Record<ActionPlanPriority, number> = {
  high: 15,
  medium: 30,
  low: 45,
};

export function gapRiskTargetHint(risk: ActionPlanPriority): string {
  return `${risk === 'high' ? 'High' : risk === 'low' ? 'Low' : 'Medium'} risk — due within ${GAP_RISK_TARGET_DAYS[risk]} days`;
}

/** Gap/action tallies for a clause, used by the clause list and the report headers. */
export type ClauseRollup = {
  gaps: number;
  resolvedGaps: number;
  pendingGaps: number;
  actions: number;
  resolvedActions: number;
  pendingActions: number;
  reviews: number;
};

export function emptyClauseRollup(): ClauseRollup {
  return {
    gaps: 0,
    resolvedGaps: 0,
    pendingGaps: 0,
    actions: 0,
    resolvedActions: 0,
    pendingActions: 0,
    reviews: 0,
  };
}

export function addClauseRollup(a: ClauseRollup, b: ClauseRollup): ClauseRollup {
  return {
    gaps: a.gaps + b.gaps,
    resolvedGaps: a.resolvedGaps + b.resolvedGaps,
    pendingGaps: a.pendingGaps + b.pendingGaps,
    actions: a.actions + b.actions,
    resolvedActions: a.resolvedActions + b.resolvedActions,
    pendingActions: a.pendingActions + b.pendingActions,
    reviews: a.reviews + b.reviews,
  };
}

/**
 * Tally one clause. `gapIndexes` is the gap roster the report is showing, which comes
 * from the parsed CAP text rather than the database.
 */
export function rollupClause(
  pointId: string,
  gapIndexes: number[],
  plans: ActionPlanEntry[],
  states: Map<string, GapState>,
): ClauseRollup {
  const rollup = emptyClauseRollup();
  const mine = plans.filter((p) => p.analysisPointId === pointId);

  rollup.actions = mine.length;
  for (const plan of mine) {
    if (normalizeActionPlanStatus(plan.status) === 'resolved') rollup.resolvedActions++;
    else rollup.pendingActions++;
    rollup.reviews += plan.reviewCount ?? plan.reviews?.length ?? 0;
  }

  rollup.gaps = gapIndexes.length;
  for (const index of gapIndexes) {
    const forGap = mine.filter((p) => (p.gapIndex ?? 0) === index || (index === 1 && !p.gapIndex));
    const status = deriveGapStatus(forGap, states.get(gapStateKey(pointId, index)));
    if (status === 'resolved') rollup.resolvedGaps++;
    else rollup.pendingGaps++;
  }

  return rollup;
}
