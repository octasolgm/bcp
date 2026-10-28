import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  COMPLIANCE_SEVERITY_LABELS,
  type ComplianceSeverity,
} from '../../../lib/nd/point-compliance-status';
import type { ClauseRollup } from '../../../lib/nd/gap-state';
import { NdRollupStatCardsComponent } from './nd-rollup-stat-cards.component';

export type ReportSummaryFilterId = ComplianceSeverity | 'all';

@Component({
  selector: 'app-nd-report-summary-stack',
  standalone: true,
  imports: [CommonModule, NdRollupStatCardsComponent],
  templateUrl: './nd-report-summary-stack.component.html',
  styleUrl: './nd-report-summary-stack.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdReportSummaryStackComponent {
  @Input() compliant = 0;
  @Input() partialCompliant = 0;
  @Input() nonCompliant = 0;
  @Input() labels = COMPLIANCE_SEVERITY_LABELS;
  @Input() rollup: ClauseRollup | null = null;
  @Input() showRollup = true;
  @Input() showHints = true;
  /** When true, compliance cards are buttons and emit filterSelect. */
  @Input() filterable = false;
  /** Active severity filter, or `all` when none selected. */
  @Input() activeFilter: ReportSummaryFilterId = 'all';

  @Output() filterSelect = new EventEmitter<ReportSummaryFilterId>();

  cardActive(severity: ComplianceSeverity): boolean {
    return this.filterable && this.activeFilter === severity;
  }

  onCardClick(severity: ComplianceSeverity): void {
    if (!this.filterable) return;
    this.filterSelect.emit(severity as ReportSummaryFilterId);
  }
}
