/** Labels shared by the platform AI usage page and a workspace's own usage page. */

export function aiFeatureLabel(feature: string | null | undefined): string {
  switch (feature) {
    case 'analysis':
      return 'Analysis';
    case 'rerun_forward':
      return 'Forward rerun';
    case 'rerun_reverse':
      return 'Reverse rerun';
    case 'rerun_point':
      return 'Clause rerun';
    case 'gap_evidence_rerun':
      return 'Gap evidence re-check';
    case 'finalize_embed':
      return 'Finalize - policy text';
    case 'document_parsing':
      return 'Document parsing';
    case 'request':
      return 'Other';
    default:
      return feature ?? '-';
  }
}

export function aiKindLabel(kind: string): string {
  if (kind === 'topup') return 'Credits added';
  if (kind === 'adjustment') return 'Adjustment';
  return 'AI call';
}
