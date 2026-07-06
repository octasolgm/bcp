export const DUAL_VERIFY_COPY = {
  loadTitle: 'Load saved analysis',
  loadHint: 'Saved session or Kafka UUID → Load into Combined Report.',
  runTitle: 'Run new dual verify',
  runHint: 'Select gov points (left panel) + attach internal policy PDF, then Run.',
  internalPolicyLabel: 'Internal policy PDF',
  internalPolicyAttach: 'Attach internal policy PDF',
  internalPolicyHint:
    'Required for Pass 2 (Gemini). Compare selected gov points against your bank internal TFS/AML policy document.',
  internalPolicyMissing: 'Attach internal policy PDF for Pass 2 (Gemini).',
  govPointsTitle: 'Gov points to verify',
  govPointsHint:
    'Select regulatory requirement points (SNB TFS guidelines). Each selected point is compared to your internal policy.',
  govPointsMissing: 'Select at least one gov point in the left panel.',
  persistenceMissing: 'Persistence not ready — configure database connection.',
  defaultInternalPdfName: 'No internal PDF attached',
} as const;

export function getRunBlockedReason(opts: {
  persistenceOk: boolean;
  hasInternalFile: boolean;
  selectedCount: number;
}): string | null {
  if (!opts.persistenceOk) return DUAL_VERIFY_COPY.persistenceMissing;
  if (!opts.hasInternalFile) return DUAL_VERIFY_COPY.internalPolicyMissing;
  if (opts.selectedCount === 0) return DUAL_VERIFY_COPY.govPointsMissing;
  return null;
}

export type ComplianceStatusFilter = 'compliant' | 'partial' | 'non-compliant';

export function parseComplianceFilter(raw?: string | null): ComplianceStatusFilter | null {
  const v = (raw ?? '').toLowerCase();
  if (v === 'compliant') return 'compliant';
  if (v === 'partial') return 'partial';
  if (v === 'non-compliant' || v === 'noncompliant') return 'non-compliant';
  return null;
}

export function complianceKeyFromBreakdownName(name: string): ComplianceStatusFilter | null {
  const n = name.toLowerCase();
  if (n.includes('non')) return 'non-compliant';
  if (n.includes('partial')) return 'partial';
  if (n.includes('compliant')) return 'compliant';
  return null;
}

export function complianceFilterLabel(filter: ComplianceStatusFilter | null): string {
  if (filter === 'compliant') return 'Compliant';
  if (filter === 'partial') return 'Partial';
  if (filter === 'non-compliant') return 'Non-compliant';
  return '';
}
