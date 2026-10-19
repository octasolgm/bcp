import type { ExcelSheetSpec } from '../../ai-lab/excel-write';
import { normalizeMultiline } from '../../ai-lab/excel-write';
import { formatActionPlanDate } from '../action-plan';
import {
  evidenceDocNames,
  evidenceQuoteRefLabel,
  gapEvidenceOutcomeLabel,
  judgmentStatusLabel,
  type GapEvidenceActionResult,
  type GapEvidenceQuote,
  type GapEvidenceReview,
} from '../gap-evidence-rerun';

/**
 * "Rerun gaps against uploaded evidence" results for the exports: what each re-check found in the newly uploaded
 * evidence for every gap (with the quotes it found), and what it did to every action. Every completed re-check is
 * kept, newest first per clause, so the export is the same audit trail the page's re-check history shows.
 */

const REGULATION_DOC_HEADER = 'Name of Regulatory Document';

export const EVIDENCE_GAP_HEADERS = [
  REGULATION_DOC_HEADER,
  'Clause No.',
  'Gap #',
  'Gap',
  'Re-check result',
  'Covered by the evidence',
  'Still missing',
  'Evidence found',
  'Evidence documents',
  'Clause status before',
  'Clause status after',
  'Re-checked on',
  'Re-checked by',
] as const;

export const EVIDENCE_ACTION_HEADERS = [
  REGULATION_DOC_HEADER,
  'Clause No.',
  'Gap #',
  'Action',
  'Re-check result',
  'Done by the evidence',
  'Still to do',
  'Change made',
  'Reason',
  'Re-checked on',
] as const;

const GAP_COL_WIDTHS = [32, 12, 8, 50, 16, 45, 45, 70, 36, 20, 20, 16, 22];
const ACTION_COL_WIDTHS = [32, 12, 8, 60, 16, 45, 45, 34, 50, 16];

/** Completed re-checks, ordered by clause (3.9 before 3.10) and newest first within a clause. */
export function completedEvidenceReviews(
  reviews: readonly GapEvidenceReview[] | null | undefined,
  clauseByPointId: Map<string, string>,
): { clause: string; review: GapEvidenceReview }[] {
  return (reviews ?? [])
    .filter((r) => r.status === 'completed')
    .map((review) => ({ clause: evidenceReviewClause(review, clauseByPointId), review }))
    .sort(
      (a, b) =>
        a.clause.localeCompare(b.clause, undefined, { numeric: true }) ||
        reviewDate(b.review).localeCompare(reviewDate(a.review)),
    );
}

export function evidenceReviewClause(review: GapEvidenceReview, clauseByPointId: Map<string, string>): string {
  return clauseByPointId.get(review.analysisPointId) ?? review.clauseNo?.trim() ?? '';
}

function reviewDate(review: GapEvidenceReview): string {
  return review.completedAt ?? review.createdAt;
}

/** One quote as written in a cell: the text, then where it came from. */
export function evidenceQuoteLine(quote: GapEvidenceQuote): string {
  const text = quote.text.trim().replace(/\s+/g, ' ');
  const unverified = quote.verified ? '' : ' (not verified against the document)';
  return `"${text}" - ${evidenceQuoteRefLabel(quote)}${unverified}`;
}

export function evidenceActionChangeLabel(action: GapEvidenceActionResult): string {
  switch (action.applied) {
    case 'resolved':
      return 'Action resolved';
    case 'split':
      return 'Split: done part resolved, remaining part added as a new action';
    default:
      return 'No change';
  }
}

/** Excel sheets for the re-check results; none when the run was never re-checked against evidence. */
export function evidenceRecheckSheets(
  reviews: readonly GapEvidenceReview[] | null | undefined,
  clauseByPointId: Map<string, string>,
  regulationDocumentName: string,
): ExcelSheetSpec[] {
  const ordered = completedEvidenceReviews(reviews, clauseByPointId);
  if (!ordered.length) return [];

  const gapRows: string[][] = [];
  const actionRows: string[][] = [];
  for (const { clause, review } of ordered) {
    const on = formatActionPlanDate(reviewDate(review));
    for (const gap of [...review.gaps].sort((a, b) => a.index - b.index)) {
      gapRows.push([
        regulationDocumentName,
        clause,
        String(gap.index),
        gap.text?.trim() ?? '',
        gapEvidenceOutcomeLabel(gap.outcome),
        gap.covered?.trim() ?? '',
        gap.outcome === 'fulfilled' ? '' : (gap.remaining?.trim() ?? ''),
        normalizeMultiline((gap.quotes ?? []).map(evidenceQuoteLine).join('\n\n')),
        evidenceDocNames(review.evidenceDocuments),
        judgmentStatusLabel(review.priorFinalStatus),
        judgmentStatusLabel(review.newFinalStatus ?? review.priorFinalStatus),
        on,
        review.createdByName ?? '',
      ]);
    }
    for (const action of [...review.actions].sort((a, b) => a.gapIndex - b.gapIndex)) {
      actionRows.push([
        regulationDocumentName,
        clause,
        String(action.gapIndex || 1),
        action.originalText.trim(),
        gapEvidenceOutcomeLabel(action.outcome),
        action.fulfilledPart?.trim() ?? '',
        action.remainingPart?.trim() ?? '',
        evidenceActionChangeLabel(action),
        action.reason?.trim() ?? '',
        on,
      ]);
    }
  }

  const sheets: ExcelSheetSpec[] = [];
  if (gapRows.length) {
    sheets.push({
      sheetName: 'Evidence re-check',
      headers: [...EVIDENCE_GAP_HEADERS],
      rows: gapRows,
      colWidths: GAP_COL_WIDTHS,
    });
  }
  if (actionRows.length) {
    sheets.push({
      sheetName: 'Re-check actions',
      headers: [...EVIDENCE_ACTION_HEADERS],
      rows: actionRows,
      colWidths: ACTION_COL_WIDTHS,
    });
  }
  return sheets;
}

/**
 * The same results as plain lines for the PDF, per clause: a heading per re-check, then each gap with what the
 * evidence covered, what is still missing and the quotes, then each action with what was done.
 */
export function evidenceRecheckPdfLines(
  reviews: readonly GapEvidenceReview[],
): { text: string; bold?: boolean }[] {
  const out: { text: string; bold?: boolean }[] = [];
  for (const review of reviews) {
    const by = review.createdByName ? ` by ${review.createdByName}` : '';
    const docs = evidenceDocNames(review.evidenceDocuments);
    out.push({
      text: `Evidence re-check (${formatActionPlanDate(reviewDate(review))}${by})${docs ? ` - evidence: ${docs}` : ''}`,
      bold: true,
    });
    const before = judgmentStatusLabel(review.priorFinalStatus);
    const after = judgmentStatusLabel(review.newFinalStatus ?? review.priorFinalStatus);
    if (review.newFinalStatus && before !== after) out.push({ text: `Clause status: ${before} -> ${after}` });
    if (review.summary?.trim()) out.push({ text: review.summary.trim() });

    for (const gap of [...review.gaps].sort((a, b) => a.index - b.index)) {
      out.push({ text: `Gap ${gap.index}: ${gapEvidenceOutcomeLabel(gap.outcome)}${gap.text?.trim() ? ` - ${gap.text.trim()}` : ''}` });
      if (gap.covered?.trim()) out.push({ text: `   Covered by the evidence: ${gap.covered.trim()}` });
      if (gap.outcome !== 'fulfilled' && gap.remaining?.trim()) {
        out.push({ text: `   Still missing: ${gap.remaining.trim()}` });
      }
      for (const quote of gap.quotes ?? []) out.push({ text: `   - ${evidenceQuoteLine(quote)}` });
    }
    for (const action of [...review.actions].sort((a, b) => a.gapIndex - b.gapIndex)) {
      out.push({
        text: `Action (Gap ${action.gapIndex || 1}): ${gapEvidenceOutcomeLabel(action.outcome)} - ${evidenceActionChangeLabel(action)}. ${action.originalText.trim()}`,
      });
      if (action.fulfilledPart?.trim()) out.push({ text: `   Done by the evidence: ${action.fulfilledPart.trim()}` });
      if (action.remainingPart?.trim()) out.push({ text: `   Still to do: ${action.remainingPart.trim()}` });
    }
  }
  return out;
}
