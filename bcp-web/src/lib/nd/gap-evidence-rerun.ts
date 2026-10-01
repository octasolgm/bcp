import type { CapGap } from '../ai-lab/parse-reference-response';
import { isPlaceholderGapText } from './cap-gap-count';

export type GapEvidenceOutcome = 'fulfilled' | 'partially_fulfilled' | 'not_fulfilled';
export type GapEvidenceJobStatus = 'queued' | 'running' | 'completed' | 'failed';
export type GapEvidencePhase = 'queued' | 'parsing' | 'retrieval' | 'forward' | 'done';

export type GapEvidenceDocRef = { id: string; name: string };

export type GapEvidenceRerunItem = {
  reviewId: string;
  analysisPointId: string;
  gapIndexFilter?: number | null;
  clauseNo?: string | null;
  status: GapEvidenceJobStatus;
  clauseOutcome?: GapEvidenceOutcome | null;
  error?: string | null;
  /** Retrieval output (Steps 1-6) for this clause — same shape the pipeline panel shows on New Analysis. */
  retrieval?: unknown;
};

/** One "rerun gaps against uploaded evidence" job, as the report page polls it. */
export type GapEvidenceRerun = {
  id: string;
  analysisRunId: string;
  scope: 'report' | 'clause';
  status: GapEvidenceJobStatus;
  phase: GapEvidencePhase;
  phaseDetail?: string | null;
  evidenceDocuments: GapEvidenceDocRef[];
  totalPoints: number;
  completedPoints: number;
  failedPoints: number;
  fulfilledGaps: number;
  partialGaps: number;
  openGaps: number;
  resolvedActions: number;
  splitActions: number;
  error?: string | null;
  llmProvider?: string | null;
  llmModel?: string | null;
  createdByName?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  createdAt: string;
  updatedAt: string;
  items: GapEvidenceRerunItem[];
};

export type GapEvidenceQuote = {
  text: string;
  verified: boolean;
  documentId?: string | null;
  documentName?: string | null;
  sectionRef?: string | null;
  page?: number | null;
};

export type GapEvidenceGapResult = {
  index: number;
  text?: string | null;
  outcome?: GapEvidenceOutcome | null;
  covered?: string | null;
  remaining?: string | null;
  quotes?: GapEvidenceQuote[];
  addedActionPlanId?: string | null;
};

export type GapEvidenceActionResult = {
  actionPlanId: string;
  gapIndex: number;
  originalText: string;
  outcome: GapEvidenceOutcome;
  fulfilledPart?: string | null;
  remainingPart?: string | null;
  reason?: string | null;
  applied: 'resolved' | 'split' | 'unchanged';
  newActionPlanId?: string | null;
};

/** The verdict for one clause from one rerun — kept as history next to the clause's original gaps. */
export type GapEvidenceReview = {
  id: string;
  rerunId: string;
  analysisPointId: string;
  gapIndexFilter?: number | null;
  scope?: 'report' | 'clause' | null;
  status: 'completed' | 'failed';
  clauseNo?: string | null;
  clauseOutcome?: GapEvidenceOutcome | null;
  priorFinalStatus?: string | null;
  newFinalStatus?: string | null;
  summary?: string | null;
  evidenceDocuments: GapEvidenceDocRef[];
  gaps: GapEvidenceGapResult[];
  actions: GapEvidenceActionResult[];
  /** The clause judged again by the New Analysis pipeline with the evidence included. */
  reanalysis?: {
    overallStatus?: string | null;
    confidence?: number | null;
    interpretation?: string | null;
    gapDescription?: string | null;
    suggestedAction?: string | null;
    documentReference?: string | null;
    policyExtract?: string[] | null;
  } | null;
  error?: string | null;
  llmProvider?: string | null;
  llmModel?: string | null;
  createdByName?: string | null;
  createdAt: string;
  completedAt?: string | null;
};

export type GapEvidenceRerunRequest = {
  scope?: 'report' | 'clause';
  points: Array<{
    pointId: string;
    gapIndex?: number | null;
    gaps: Array<{ index: number; text: string }>;
  }>;
};

export function isGapEvidenceRerunActive(rerun: Pick<GapEvidenceRerun, 'status'> | null | undefined): boolean {
  return rerun?.status === 'queued' || rerun?.status === 'running';
}

/** Gap wording sent to the re-check — what is missing, falling back to the expected fix. */
export function gapRosterText(gap: CapGap): string {
  const missing = gap.missing?.trim() ?? '';
  if (missing && !isPlaceholderGapText(missing)) return missing;
  return gap.fix?.trim() ?? missing;
}

export function gapEvidenceOutcomeLabel(outcome: GapEvidenceOutcome | null | undefined): string {
  switch (outcome) {
    case 'fulfilled':
      return 'Fulfilled';
    case 'partially_fulfilled':
      return 'Partly fulfilled';
    case 'not_fulfilled':
      return 'Not fulfilled';
    default:
      return 'Not checked';
  }
}

export function gapEvidencePhaseLabel(phase: GapEvidencePhase | string | null | undefined): string {
  switch ((phase ?? '').toLowerCase()) {
    case 'parsing':
      return 'Preparing documents';
    case 'retrieval':
      return 'Retrieving relevant evidence sections';
    case 'forward':
      return 'Judging open gaps against the evidence';
    case 'done':
      return 'Complete';
    default:
      return 'Starting re-check';
  }
}

export type GapEvidenceRerunCounts = {
  all: number;
  running: number;
  queued: number;
  completed: number;
  failed: number;
};

export function gapEvidenceRerunCounts(rerun: Pick<GapEvidenceRerun, 'items'>): GapEvidenceRerunCounts {
  const counts: GapEvidenceRerunCounts = { all: rerun.items.length, running: 0, queued: 0, completed: 0, failed: 0 };
  for (const item of rerun.items) counts[item.status]++;
  return counts;
}

/** Same shape as the new-analysis ticker: phase, then done / running / queued. */
export function gapEvidenceRerunMarquee(rerun: GapEvidenceRerun): string {
  const counts = gapEvidenceRerunCounts(rerun);
  const done = counts.completed + counts.failed;
  const phase = gapEvidencePhaseLabel(rerun.phase);
  const progress =
    counts.running > 0 || counts.queued > 0
      ? `${done}/${counts.all} done · ${counts.running} running · ${counts.queued} queued`
      : `${done}/${counts.all}`;
  const detail = rerun.phaseDetail?.trim();
  return detail ? `${phase} (${progress}) · ${detail}` : `${phase} (${progress})`;
}

/** One-line result, e.g. "2 gaps fulfilled · 1 partly fulfilled · 3 actions resolved · 1 action split". */
export function gapEvidenceRerunSummary(rerun: GapEvidenceRerun): string {
  const parts: string[] = [];
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
  if (rerun.fulfilledGaps) parts.push(`${plural(rerun.fulfilledGaps, 'gap')} fulfilled`);
  if (rerun.partialGaps) parts.push(`${plural(rerun.partialGaps, 'gap')} partly fulfilled`);
  if (rerun.openGaps) parts.push(`${plural(rerun.openGaps, 'gap')} still open`);
  if (rerun.resolvedActions) parts.push(`${plural(rerun.resolvedActions, 'action')} resolved`);
  if (rerun.splitActions) parts.push(`${plural(rerun.splitActions, 'action')} split`);
  if (rerun.failedPoints) parts.push(`${plural(rerun.failedPoints, 'clause')} failed`);
  return parts.length ? parts.join(' · ') : 'No changes from the uploaded evidence';
}

/** Newest completed verdict for one gap of a clause (reviews arrive newest first). */
export function latestGapVerdict(
  reviews: readonly GapEvidenceReview[],
  gapIndex: number,
): { review: GapEvidenceReview; gap: GapEvidenceGapResult } | null {
  for (const review of reviews) {
    if (review.status !== 'completed') continue;
    const gap = review.gaps.find((g) => g.index === gapIndex);
    if (gap) return { review, gap };
  }
  return null;
}

export function evidenceReviewsForPoint(
  reviews: readonly GapEvidenceReview[] | null | undefined,
  pointId: string,
): GapEvidenceReview[] {
  return (reviews ?? []).filter((r) => r.analysisPointId === pointId);
}

export function evidenceQuoteRefLabel(quote: GapEvidenceQuote): string {
  const parts = [quote.documentName?.trim() || 'Uploaded evidence'];
  if (quote.sectionRef?.trim()) parts.push(`§${quote.sectionRef.trim().replace(/^§/, '')}`);
  if (quote.page != null) parts.push(`p. ${quote.page}`);
  return parts.join(' · ');
}

export function evidenceDocNames(docs: readonly GapEvidenceDocRef[]): string {
  return docs.map((d) => d.name).join(', ');
}

/** One "Rerun all gaps" (or single-clause) re-check, with the verdict for each clause it checked. */
export type GapEvidenceRunHistoryEntry = {
  rerunId: string;
  createdAt: string;
  createdByName?: string | null;
  llmProvider?: string | null;
  llmModel?: string | null;
  scope?: 'report' | 'clause' | null;
  evidenceDocuments: GapEvidenceDocRef[];
  reviews: GapEvidenceReview[];
};

/** Groups clause verdicts into their re-check runs, newest run first. */
export function groupReviewsByRerun(reviews: readonly GapEvidenceReview[]): GapEvidenceRunHistoryEntry[] {
  const byRun = new Map<string, GapEvidenceRunHistoryEntry>();
  for (const review of reviews) {
    let entry = byRun.get(review.rerunId);
    if (!entry) {
      entry = {
        rerunId: review.rerunId,
        createdAt: review.createdAt,
        createdByName: review.createdByName,
        llmProvider: review.llmProvider,
        llmModel: review.llmModel,
        scope: review.scope,
        evidenceDocuments: [],
        reviews: [],
      };
      byRun.set(review.rerunId, entry);
    }
    if (review.createdAt < entry.createdAt) entry.createdAt = review.createdAt;
    for (const doc of review.evidenceDocuments) {
      if (!entry.evidenceDocuments.some((d) => d.id === doc.id)) entry.evidenceDocuments.push(doc);
    }
    entry.reviews.push(review);
  }
  return [...byRun.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

export function reviewsUsingDocument(
  reviews: readonly GapEvidenceReview[],
  storedDocumentId: string,
): GapEvidenceReview[] {
  return reviews.filter((r) => r.evidenceDocuments.some((d) => d.id === storedDocumentId));
}

/** How many separate re-checks a set of verdicts came from. */
export function rerunCount(reviews: readonly GapEvidenceReview[]): number {
  return new Set(reviews.map((r) => r.rerunId)).size;
}

/** True when the evidence changed something for the clause: a gap covered in whole or part, or an action moved. */
export function reviewHasNewFindings(review: GapEvidenceReview): boolean {
  if (review.status !== 'completed') return false;
  return (
    review.gaps.some((g) => g.outcome === 'fulfilled' || g.outcome === 'partially_fulfilled') ||
    review.actions.some((a) => a.applied !== 'unchanged')
  );
}

/** Regul judgment status wording ("partial", "non_compliant", ...) as a label. */
export function judgmentStatusLabel(status: string | null | undefined): string {
  const s = (status ?? '').trim().toLowerCase();
  if (s === 'compliant') return 'Compliant';
  if (s.includes('partial')) return 'Partially compliant';
  if (s.includes('non')) return 'Non-compliant';
  return s ? s.replace(/_/g, ' ') : 'Not judged';
}
