import { comparePointOrder } from './ai-lab/excel-write';
import {
  parseReferenceComplianceBlock,
  type ReferenceComplianceBlock,
} from './ai-lab/parse-reference-response';
import type { ParsedComplianceResult } from './ai-lab/parse-compliance-results';
import {
  compareDualVerifyResults,
  type DualVerifyAgreement,
} from './landing-ai/dual-verify-merge';

export type { DualVerifyAgreement };

export type DualVerifyReportItem = {
  pointId: string;
  pointTitle?: string;
  govText?: string;
  landingMessage?: string;
  llmMessage?: string;
  agreement?: DualVerifyAgreement;
  errorMessage?: string;
  status: 'completed' | 'failed' | 'running' | 'queued' | 'loaded';
};

export type DualVerifyReportSummary = {
  total: number;
  completed: number;
  aligned: number;
  needsReview: number;
  failed: number;
  inProgress: number;
};

function comparePointIds(a: string, b: string): number {
  return comparePointOrder(
    { title: a } as ReferenceComplianceBlock,
    { title: b } as ReferenceComplianceBlock,
  );
}

function isInFlightStatus(status: DualVerifyReportItem['status']): boolean {
  return status === 'running' || status === 'queued';
}

export function mergeReportItems(
  existing: Map<string, DualVerifyReportItem>,
  incoming: DualVerifyReportItem[],
): Map<string, DualVerifyReportItem> {
  const next = new Map(existing);
  for (const item of incoming) {
    const prev = next.get(item.pointId);
    const incomingDone =
      item.status === 'completed' && Boolean(item.landingMessage && item.llmMessage);
    const prevDone =
      prev?.status === 'completed' && Boolean(prev.landingMessage && prev.llmMessage);

    if (!prev) {
      next.set(item.pointId, item);
      continue;
    }
    if (isInFlightStatus(item.status)) {
      next.set(item.pointId, { ...prev, ...item });
      continue;
    }
    if (item.status === 'failed') {
      next.set(item.pointId, { ...prev, ...item });
      continue;
    }
    if (incomingDone && !prevDone) {
      next.set(item.pointId, item);
      continue;
    }
    if (incomingDone && prevDone) {
      next.set(item.pointId, { ...prev, ...item });
      continue;
    }
    if (!prevDone) {
      next.set(item.pointId, { ...prev, ...item });
    }
  }
  return next;
}

/** Remove one point from the combined report bag (returns a new map). */
export function removeReportItemFromBag(
  bag: Map<string, DualVerifyReportItem>,
  pointId: string,
): Map<string, DualVerifyReportItem> {
  const next = new Map(bag);
  next.delete(pointId);
  return next;
}

export function reportItemsToSortedArray(
  map: Map<string, DualVerifyReportItem>,
): DualVerifyReportItem[] {
  return [...map.values()].sort((a, b) => comparePointIds(a.pointId, b.pointId));
}

/** Points with both passes complete — used for export and DB save. */
export function exportableReportItems(
  items: DualVerifyReportItem[],
): DualVerifyReportItem[] {
  return items.filter(
    (i) => Boolean(i.landingMessage?.trim() && i.llmMessage?.trim()),
  );
}

export function buildReportSummary(items: DualVerifyReportItem[]): DualVerifyReportSummary {
  const completed = items.filter(
    (i) => i.landingMessage && i.llmMessage && i.agreement,
  );
  return {
    total: items.length,
    completed: completed.length,
    aligned: completed.filter((i) => i.agreement?.status === 'aligned').length,
    needsReview: completed.filter(
      (i) => i.agreement && i.agreement.status !== 'aligned',
    ).length,
    failed: items.filter((i) => i.status === 'failed').length,
    inProgress: items.filter((i) => i.status === 'running' || i.status === 'queued').length,
  };
}

export function savedResultToReportItem(r: {
  point_id: string;
  title?: string;
  text?: string;
  message?: string;
  landingMessage?: string;
  llmMessage?: string;
  agreementJson?: DualVerifyAgreement;
}): DualVerifyReportItem | null {
  const landingMessage = r.landingMessage ?? r.message ?? '';
  const llmMessage = r.llmMessage ?? '';
  // Landing-only results still count (Pass 2 may be narrative markdown)
  if (!landingMessage && !llmMessage) return null;

  return {
    pointId: r.point_id,
    pointTitle: r.title,
    govText: r.text,
    landingMessage: landingMessage || undefined,
    llmMessage: llmMessage || undefined,
    agreement:
      r.agreementJson ??
      (landingMessage && llmMessage
        ? compareDualVerifyResults(landingMessage, llmMessage)
        : undefined),
    status: 'loaded',
  };
}

export function progressPointToReportItem(pt: {
  pointId: string;
  pointTitle?: string;
  status: string;
  landingMessage?: string;
  llmMessage?: string;
  agreementJson?: DualVerifyAgreement;
  errorMessage?: string;
}): DualVerifyReportItem {
  const landingMessage = pt.landingMessage ?? '';
  const llmMessage = pt.llmMessage ?? '';
  const agreement =
    pt.agreementJson ??
    (landingMessage && llmMessage
      ? compareDualVerifyResults(landingMessage, llmMessage)
      : undefined);

  return {
    pointId: pt.pointId,
    pointTitle: pt.pointTitle,
    landingMessage: landingMessage || undefined,
    llmMessage: llmMessage || undefined,
    agreement,
    errorMessage: pt.errorMessage,
    status:
      pt.status === 'completed' || pt.status === 'failed'
        ? pt.status
        : pt.status === 'running'
          ? 'running'
          : 'queued',
  };
}

export function llmBlocksFromReport(items: DualVerifyReportItem[]): ReferenceComplianceBlock[] {
  return blocksFromReport(items, 'llm');
}

export function landingBlocksFromReport(items: DualVerifyReportItem[]): ReferenceComplianceBlock[] {
  return blocksFromReport(items, 'landing');
}

function blocksFromReport(
  items: DualVerifyReportItem[],
  pass: 'landing' | 'llm',
): ReferenceComplianceBlock[] {
  const key = pass === 'landing' ? 'landingMessage' : 'llmMessage';
  return reportItemsToSortedArray(new Map(items.map((i) => [i.pointId, i])))
    .filter((i) => i[key])
    .map((i) => parseReferenceComplianceBlock(i[key]!.trim()));
}

export function buildDualVerifyExecutiveSummary(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
): string {
  const lines = [
    `Dual verify session report — ${summary.total} point(s) in combined report.`,
    `Pipeline agreement: ${summary.aligned} aligned · ${summary.needsReview} need manual review · ${summary.failed} failed · ${summary.inProgress} in progress.`,
    'Pass 2 (LLM verify) compliance breakdown is shown in statistics below.',
  ];
  const review = items.filter((i) => i.agreement && i.agreement.status !== 'aligned');
  if (review.length > 0) {
    lines.push('', 'Points needing review:');
    for (const item of review.slice(0, 15)) {
      lines.push(`- ${item.pointId} — ${item.agreement!.label}: ${item.agreement!.summary}`);
    }
    if (review.length > 15) {
      lines.push(`…and ${review.length - 15} more`);
    }
  }
  return lines.join('\n');
}

export function parsedResultsFromReport(
  items: DualVerifyReportItem[],
  pass: 'landing' | 'llm' = 'llm',
): ParsedComplianceResult[] {
  const blocks =
    pass === 'landing' ? landingBlocksFromReport(items) : llmBlocksFromReport(items);
  return blocks.map((block, index) => {
    const confMatch = block.confidence.match(/(\d+)/);
    const confidence = confMatch ? Number(confMatch[1]) : null;
    const status = block.status || 'Unknown';
    return {
      index,
      title: block.title,
      body: block.body,
      fields: block.fields,
      status,
      confidence,
      needsAttention: status !== 'Compliant' || (confidence !== null && confidence < 100),
    };
  });
}
