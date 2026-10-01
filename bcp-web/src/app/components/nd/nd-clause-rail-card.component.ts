import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import type { ClauseRollup } from '../../../lib/nd/gap-state';
import { resolveClauseRailHeading } from '../../../lib/nd/clause-rail-card-display';

/** Shared clause card: number + status, heading, excerpt, gap/action/review chips, confidence. */
@Component({
  selector: 'app-nd-clause-rail-card',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './nd-clause-rail-card.component.html',
  styleUrls: ['./nd-clause-rail-card.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdClauseRailCardComponent {
  @Input() clauseNum = '';
  /** Clause title (not the number — shown once in the header row). */
  @Input() heading = '';
  /** @deprecated Prefer heading; still honored when heading is empty. */
  @Input() subheading = '';
  @Input() severity: string | null = null;
  @Input() severityLabelText = '';
  @Input() forwardStatusLabel = '';
  @Input() forwardStatusClass = '';
  /** Regulation / requirement excerpt. */
  @Input() clauseText = '';
  @Input() policySnippet = '';
  @Input() confidence = '—';
  @Input() gapCount = 0;
  @Input() rollup: ClauseRollup | null = null;
  @Input() showInfoButton = false;
  /** How many gap evidence re-checks looked at this clause; shows a history chip when above 0. */
  @Input() recheckCount = 0;

  @Output() infoClick = new EventEmitter<Event>();
  @Output() recheckHistoryClick = new EventEmitter<Event>();

  onRecheckHistoryClick(event: Event): void {
    event.stopPropagation();
    this.recheckHistoryClick.emit(event);
  }

  /** Normalized title under the clause number row (never repeats the number alone). */
  get displayHeading(): string {
    const fromHeading = resolveClauseRailHeading(this.clauseNum, this.heading);
    if (fromHeading) return fromHeading;
    return resolveClauseRailHeading(this.clauseNum, this.subheading);
  }

  get hasRollupChips(): boolean {
    if (!this.rollup) return false;
    return (
      this.rollup.gaps > 0 ||
      this.rollup.actions > 0 ||
      this.rollup.reviews > 0 ||
      this.rollup.resolvedGaps > 0 ||
      this.rollup.pendingGaps > 0 ||
      this.rollup.resolvedActions > 0 ||
      this.rollup.pendingActions > 0
    );
  }

  onInfoClick(event: Event): void {
    event.stopPropagation();
    this.infoClick.emit(event);
  }
}
