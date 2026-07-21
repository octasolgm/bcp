import { parsedResultsFromReport, type DualVerifyReportItem } from './dual-verify-report';
import type { ComplianceStatusFilter } from './dual-verify-workflow';

export function pass2ComplianceBucket(
  item: DualVerifyReportItem,
): ComplianceStatusFilter | null {
  const raw =
    item.agreement?.llmStatus ?? parsedResultsFromReport([item], 'llm')[0]?.status;
  if (!raw) return null;
  if (raw === 'Compliant') return 'compliant';
  if (raw === 'Partial Compliant') return 'partial';
  if (raw === 'Non-Compliant') return 'non-compliant';
  return null;
}

export function filterReportByCompliance(
  items: DualVerifyReportItem[],
  filter: ComplianceStatusFilter | null,
): DualVerifyReportItem[] {
  if (!filter) return items;
  return items.filter((item) => pass2ComplianceBucket(item) === filter);
}
