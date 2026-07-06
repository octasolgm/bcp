export type DualVerifyAgreement = {
  status: string;
  label: string;
  landingStatus: string;
  llmStatus: string;
  landingConfidence?: number;
  llmConfidence?: number;
  confidenceDelta?: number;
  summary: string;
};

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

export function mergeReportItems(
  existing: Map<string, DualVerifyReportItem>,
  incoming: DualVerifyReportItem[],
): Map<string, DualVerifyReportItem> {
  const next = new Map(existing);
  for (const item of incoming) {
    const prev = next.get(item.pointId);
    const incomingDone = item.status === 'completed' && Boolean(item.landingMessage && item.llmMessage);
    const prevDone = prev?.status === 'completed' && Boolean(prev.landingMessage && prev.llmMessage);
    if (!prev) {
      next.set(item.pointId, item);
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

export function reportItemsToSortedArray(map: Map<string, DualVerifyReportItem>): DualVerifyReportItem[] {
  return [...map.values()].sort((a, b) => comparePointIds(a.pointId, b.pointId));
}

function comparePointIds(a: string, b: string): number {
  const pa = a.split('.').map((x) => (/^\d+$/.test(x) ? Number(x) : x));
  const pb = b.split('.').map((x) => (/^\d+$/.test(x) ? Number(x) : x));
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const va = pa[i] ?? '';
    const vb = pb[i] ?? '';
    if (va < vb) return -1;
    if (va > vb) return 1;
  }
  return 0;
}

export function buildReportSummary(items: DualVerifyReportItem[]): DualVerifyReportSummary {
  const completed = items.filter((i) => i.landingMessage && i.llmMessage && i.agreement);
  return {
    total: items.length,
    completed: completed.length,
    aligned: completed.filter((i) => i.agreement?.status === 'aligned').length,
    needsReview: completed.filter((i) => i.agreement && i.agreement.status !== 'aligned').length,
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
  if (!landingMessage || !llmMessage) return null;
  return {
    pointId: r.point_id,
    pointTitle: r.title,
    govText: r.text,
    landingMessage,
    llmMessage,
    agreement: r.agreementJson,
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
  return {
    pointId: pt.pointId,
    pointTitle: pt.pointTitle,
    landingMessage: pt.landingMessage,
    llmMessage: pt.llmMessage,
    agreement: pt.agreementJson,
    errorMessage: pt.errorMessage,
    status:
      pt.status === 'completed' || pt.status === 'failed'
        ? pt.status
        : pt.status === 'running'
          ? 'running'
          : 'queued',
  };
}

export function buildDualVerifyExecutiveSummary(
  items: DualVerifyReportItem[],
  summary: DualVerifyReportSummary,
): string {
  const lines = [
    `Dual verify session report — ${summary.total} point(s) in combined report.`,
    `Pipeline agreement: ${summary.aligned} aligned · ${summary.needsReview} need review · ${summary.failed} failed · ${summary.inProgress} in progress.`,
  ];
  const review = items.filter((i) => i.agreement && i.agreement.status !== 'aligned');
  if (review.length > 0) {
    lines.push('', 'Points needing review:');
    for (const item of review.slice(0, 15)) {
      lines.push(`- ${item.pointId} — ${item.agreement!.label}: ${item.agreement!.summary}`);
    }
  }
  return lines.join('\n');
}
