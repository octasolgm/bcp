import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  HostListener,
  Input,
  Output,
} from '@angular/core';
import {
  evidenceDocNames,
  evidenceQuoteRefLabel,
  gapEvidenceOutcomeLabel,
  groupReviewsByRerun,
  judgmentStatusLabel,
  reviewHasNewFindings,
  type GapEvidenceQuote,
  type GapEvidenceReview,
  type GapEvidenceRunHistoryEntry,
} from '../../../lib/nd/gap-evidence-rerun';
import { complianceSeverityLabel, type ComplianceSeverity } from '../../../lib/nd/point-compliance-status';
import { formatDate } from '../../../lib/nd/utils';

/**
 * Right-side history of gap evidence re-checks — for one uploaded document or one clause. Each
 * re-check lists the clauses it looked at, what the evidence now covers, what is still missing,
 * and which actions were resolved or split, or that nothing changed.
 */
@Component({
  selector: 'app-nd-evidence-history-drawer',
  standalone: true,
  templateUrl: './nd-evidence-history-drawer.component.html',
  styleUrl: './nd-evidence-history-drawer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdEvidenceHistoryDrawerComponent {
  @Input() eyebrow = 'Re-check history';
  @Input() title = '';
  @Input() reviews: GapEvidenceReview[] = [];
  /** Display label per analysis point id, e.g. "§3.3 Protection against liability". */
  @Input() clauseLabels: Record<string, string> = {};
  @Output() closed = new EventEmitter<void>();
  @Output() openClause = new EventEmitter<string>();
  @Output() openDocument = new EventEmitter<{ docId: string; page?: string | null; find?: string }>();

  readonly gapEvidenceOutcomeLabel = gapEvidenceOutcomeLabel;
  readonly evidenceQuoteRefLabel = evidenceQuoteRefLabel;
  readonly evidenceDocNames = evidenceDocNames;
  readonly reviewHasNewFindings = reviewHasNewFindings;
  readonly formatDate = formatDate;
  readonly judgmentStatusLabel = judgmentStatusLabel;

  /** Re-checks the user has expanded; every one starts collapsed to its header and summary. */
  private readonly openRuns = new Set<string>();

  /** Clauses the user has folded away inside an open re-check; every clause starts expanded. */
  private readonly collapsedClauses = new Set<string>();

  isClauseCollapsed(reviewId: string): boolean {
    return this.collapsedClauses.has(reviewId);
  }

  toggleClause(reviewId: string): void {
    if (this.collapsedClauses.has(reviewId)) this.collapsedClauses.delete(reviewId);
    else this.collapsedClauses.add(reviewId);
  }

  isRunOpen(rerunId: string): boolean {
    return this.openRuns.has(rerunId);
  }

  toggleRun(rerunId: string): void {
    if (this.openRuns.has(rerunId)) this.openRuns.delete(rerunId);
    else this.openRuns.add(rerunId);
  }

  get runs(): GapEvidenceRunHistoryEntry[] {
    return groupReviewsByRerun(this.reviews);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closed.emit();
  }

  clauseLabel(review: GapEvidenceReview): string {
    return this.clauseLabels[review.analysisPointId] || (review.clauseNo ? `§${review.clauseNo}` : 'Clause');
  }

  runSummary(run: GapEvidenceRunHistoryEntry): string {
    const changed = run.reviews.filter(reviewHasNewFindings).length;
    const failed = run.reviews.filter((r) => r.status === 'failed').length;
    const parts = [`${run.reviews.length} clause${run.reviews.length === 1 ? '' : 's'} checked`];
    parts.push(changed ? `${changed} with new findings` : 'no new findings');
    if (failed) parts.push(`${failed} failed`);
    return parts.join(' · ');
  }

  statusChange(review: GapEvidenceReview): string {
    const from = review.priorFinalStatus;
    const to = review.newFinalStatus;
    if (!from || !to || from === to) return '';
    return `${complianceSeverityLabel(from as ComplianceSeverity)} → ${complianceSeverityLabel(to as ComplianceSeverity)}`;
  }

  openQuote(quote: GapEvidenceQuote): void {
    if (quote.documentId) {
      this.openDocument.emit({ docId: quote.documentId, page: quote.page != null ? String(quote.page) : null, find: quote.text });
    }
  }
}
