import type { GapRiskCounts } from './risk-priority-score';
import type { RiskTier } from './risk-priority-score';
import type { AnalysisRunSummary } from './types';

/** Dashboard card metrics that drill into a per-analysis breakdown. */
export type DashboardMetricId =
  | 'critical'
  | 'medium'
  | 'low'
  | 'findings'
  | 'compliant'
  | 'partial'
  | 'non_compliant';

export type DashboardMetricDef = {
  id: DashboardMetricId;
  label: string;
  hint: string;
  /** Column shown on the breakdown table. */
  countLabel: string;
};

export const DASHBOARD_METRICS: Record<DashboardMetricId, DashboardMetricDef> = {
  critical: {
    id: 'critical',
    label: 'Critical gaps',
    hint: 'Score 67–100',
    countLabel: 'Critical gaps',
  },
  medium: {
    id: 'medium',
    label: 'Medium risk gaps',
    hint: 'Score 34–66',
    countLabel: 'Medium gaps',
  },
  low: {
    id: 'low',
    label: 'Low risk gaps',
    hint: 'Score 0–33',
    countLabel: 'Low gaps',
  },
  findings: {
    id: 'findings',
    label: 'Total findings',
    hint: 'Scored compliance points',
    countLabel: 'Findings',
  },
  compliant: {
    id: 'compliant',
    label: 'Compliant items',
    hint: 'Fully addressed',
    countLabel: 'Compliant',
  },
  partial: {
    id: 'partial',
    label: 'Partial items',
    hint: 'Partially addressed',
    countLabel: 'Partial',
  },
  non_compliant: {
    id: 'non_compliant',
    label: 'Non-compliant items',
    hint: 'Not addressed',
    countLabel: 'Non-compliant',
  },
};

export function parseDashboardMetricId(raw: string | null | undefined): DashboardMetricId | null {
  if (!raw) return null;
  return raw in DASHBOARD_METRICS ? (raw as DashboardMetricId) : null;
}

export function isRiskTierMetric(id: DashboardMetricId): id is RiskTier {
  return id === 'critical' || id === 'medium' || id === 'low';
}

export function complianceCountForRun(run: AnalysisRunSummary, metric: DashboardMetricId): number {
  const c = run.compliant ?? 0;
  const p = run.partial ?? 0;
  const n = run.nonCompliant ?? 0;
  switch (metric) {
    case 'compliant':
      return c;
    case 'partial':
      return p;
    case 'non_compliant':
      return n;
    case 'findings':
      return c + p + n;
    default:
      return 0;
  }
}

export function riskCountFromGaps(gaps: GapRiskCounts, metric: RiskTier): number {
  return gaps[metric] ?? 0;
}

/** Query params to open gap analysis filtered for this metric. */
export function gapAnalysisQueryForMetric(
  runId: string,
  metric: DashboardMetricId,
): Record<string, string> {
  const q: Record<string, string> = { run: runId };
  switch (metric) {
    case 'compliant':
      q['filter'] = 'compliant';
      break;
    case 'partial':
      q['filter'] = 'partial_compliant';
      break;
    case 'non_compliant':
      q['filter'] = 'non_compliant';
      break;
    case 'critical':
    case 'medium':
    case 'low':
      q['filter'] = 'with_gaps';
      q['riskTier'] = metric;
      break;
    case 'findings':
    default:
      break;
  }
  return q;
}
