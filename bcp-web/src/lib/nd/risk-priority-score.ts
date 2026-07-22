/** 0–100 risk score bands used on dashboard and review priority slider. */
export type RiskTier = 'low' | 'medium' | 'critical';

export const RISK_PRIORITY_STANDARD = {
  low: { min: 0, max: 33, label: 'Low risk', hint: 'Score 0–33 · monitor' },
  medium: { min: 34, max: 66, label: 'Medium risk', hint: 'Score 34–66 · address soon' },
  critical: { min: 67, max: 100, label: 'Critical risk', hint: 'Score 67–100 · immediate action' },
} as const;

export const RISK_STANDARD_SUMMARY =
  'Risk standard: 0–33 Low · 34–66 Medium · 67–100 Critical (priority score 0–100)';

export function clampRiskScore(score: number): number {
  return Math.min(100, Math.max(0, Math.round(score)));
}

export function riskTierFromScore(score: number): RiskTier {
  const s = clampRiskScore(score);
  if (s <= RISK_PRIORITY_STANDARD.low.max) return 'low';
  if (s <= RISK_PRIORITY_STANDARD.medium.max) return 'medium';
  return 'critical';
}

export function riskTierLabel(tier: RiskTier): string {
  return RISK_PRIORITY_STANDARD[tier].label;
}

export function riskTierHint(tier: RiskTier): string {
  return RISK_PRIORITY_STANDARD[tier].hint;
}

export function riskScoreLabel(score: number): string {
  const tier = riskTierFromScore(score);
  return `${clampRiskScore(score)} · ${riskTierLabel(tier)}`;
}

/** Map legacy gap priority text / numeric review values to 0–100. */
export function riskScoreFromRaw(raw: string | number | null | undefined): number {
  if (raw == null || raw === '') return 50;
  if (typeof raw === 'number' && Number.isFinite(raw)) return clampRiskScore(raw);
  const t = String(raw).trim().toLowerCase();
  if (/^\d+$/.test(t)) return clampRiskScore(Number(t));
  if (t === 'low') return 25;
  if (t === 'medium') return 50;
  if (t === 'higher' || t === 'high' || t === 'critical') return 85;
  return 50;
}

export type GapRiskCounts = {
  critical: number;
  medium: number;
  low: number;
  total: number;
};

export function emptyGapRiskCounts(): GapRiskCounts {
  return { critical: 0, medium: 0, low: 0, total: 0 };
}

export function addGapRiskCount(counts: GapRiskCounts, score: number): GapRiskCounts {
  const tier = riskTierFromScore(score);
  return {
    ...counts,
    [tier]: counts[tier] + 1,
    total: counts.total + 1,
  };
}
