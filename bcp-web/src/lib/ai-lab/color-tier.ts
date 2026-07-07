import type { ParsedComplianceResult } from './parse-compliance-results';

export type ColorTier = 'green' | 'yellow' | 'red' | 'neutral';

export const COLOR_LEGEND: {
  tier: ColorTier;
  label: string;
  description: string;
}[] = [
  {
    tier: 'green',
    label: 'Green',
    description:
      '100% confidence and Compliant — requirement fully covered, no gap',
  },
  {
    tier: 'yellow',
    label: 'Yellow',
    description:
      'Partial Compliant, or confidence 70–99% — gaps or minor issues, review needed',
  },
  {
    tier: 'red',
    label: 'Red',
    description:
      'Non-Compliant, or confidence below 70% — significant gap, corrective action required',
  },
];

export function getComplianceColorTier(
  item: ParsedComplianceResult,
): ColorTier {
  if (item.status === 'Non-Compliant') return 'red';
  if (item.confidence !== null && item.confidence < 70) return 'red';
  if (item.status === 'Partial Compliant') return 'yellow';
  if (item.confidence !== null && item.confidence < 100) return 'yellow';
  if (item.status === 'Compliant' && item.confidence === 100) return 'green';
  if (item.status === 'Compliant') return 'green';
  return 'neutral';
}

export function buildTierCounts(results: ParsedComplianceResult[]) {
  return {
    green: results.filter((r) => getComplianceColorTier(r) === 'green').length,
    yellow: results.filter((r) => getComplianceColorTier(r) === 'yellow')
      .length,
    red: results.filter((r) => getComplianceColorTier(r) === 'red').length,
  };
}

export const TIER_UI = {
  green: {
    card: 'border-2 border-emerald-400 bg-emerald-50 shadow-md shadow-emerald-100',
    title: 'text-emerald-800',
    badge:
      'border border-emerald-400 bg-white text-emerald-700 tracking-wider',
    badgeLabel: 'FULLY COMPLIANT',
    label: 'Fully compliant',
    swatch: 'bg-emerald-500',
    confidence: 'font-bold text-emerald-700',
    status: 'font-bold text-emerald-700',
  },
  yellow: {
    card: 'border-2 border-amber-400 bg-amber-50 shadow-md shadow-amber-100',
    title: 'text-amber-800',
    badge: 'border border-amber-400 bg-white text-amber-700 tracking-wider',
    badgeLabel: 'REVIEW NEEDED',
    label: 'Review needed',
    swatch: 'bg-amber-500',
    confidence: 'font-bold text-amber-700',
    status: 'font-bold text-amber-700',
  },
  red: {
    card: 'border-2 border-red-400 bg-red-50 shadow-md shadow-red-100',
    title: 'text-red-800',
    badge: 'border border-red-400 bg-white text-red-700 tracking-wider',
    badgeLabel: 'NON-COMPLIANT',
    label: 'Action required',
    swatch: 'bg-red-500',
    confidence: 'font-bold text-red-700',
    status: 'font-bold text-red-700',
  },
  neutral: {
    card: 'border-2 border-slate-300 bg-white shadow-sm',
    title: 'text-violet-900',
    badge: 'border border-slate-300 bg-white text-slate-600 tracking-wider',
    badgeLabel: 'UNRATED',
    label: 'Unrated',
    swatch: 'bg-slate-400',
    confidence: 'font-semibold text-slate-700',
    status: 'font-semibold text-slate-700',
  },
} as const;

export const TIER_EXPORT = {
  green: {
    wrap: 'border:2px solid #34d399;background:#ecfdf5;padding:20px;margin-bottom:28px;border-radius:10px;box-shadow:0 2px 8px rgba(52,211,153,0.15);',
    title: '#047857',
    badge: '#047857',
    badgeBorder: '#34d399',
    badgeLabel: 'FULLY COMPLIANT',
    label: 'Fully compliant — 100%',
    value: '#047857',
  },
  yellow: {
    wrap: 'border:2px solid #fbbf24;background:#fffbeb;padding:20px;margin-bottom:28px;border-radius:10px;box-shadow:0 2px 8px rgba(251,191,36,0.15);',
    title: '#b45309',
    badge: '#b45309',
    badgeBorder: '#fbbf24',
    badgeLabel: 'REVIEW NEEDED',
    label: 'Review needed — 70–99% or Partial',
    value: '#b45309',
  },
  red: {
    wrap: 'border:2px solid #f87171;background:#fef2f2;padding:20px;margin-bottom:28px;border-radius:10px;box-shadow:0 2px 8px rgba(248,113,113,0.15);',
    title: '#b91c1c',
    badge: '#b91c1c',
    badgeBorder: '#f87171',
    badgeLabel: 'NON-COMPLIANT',
    label: 'Action required — &lt;70% or Non-Compliant',
    value: '#b91c1c',
  },
  neutral: {
    wrap: 'border:2px solid #cbd5e1;background:#fff;padding:20px;margin-bottom:28px;border-radius:10px;',
    title: '#4c1d95',
    badge: '#475569',
    badgeBorder: '#cbd5e1',
    badgeLabel: 'UNRATED',
    label: 'Unrated',
    value: '#334155',
  },
} as const;
