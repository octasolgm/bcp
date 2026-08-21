import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ACTION_PLAN_EXPORT_COLUMNS,
  REVIEW_EXPORT_COLUMNS,
  type GapAnalysisExportSelection,
} from '../../../lib/nd/export/gap-analysis-export';

/**
 * Asked before an Excel export: which sheets to write and which columns each
 * sheet keeps. Gap columns vary per run, so the caller supplies them.
 */
@Component({
  selector: 'app-nd-export-options-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-export-options-dialog.component.html',
  styleUrl: './nd-export-options-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdExportOptionsDialogComponent {
  /** Gap sheet columns for this run, in sheet order. */
  @Input() set gapColumns(value: string[]) {
    this.gapCols = value;
    this.selectedGap = new Set(value);
  }

  /** Hidden when the run has no action plans — there would be nothing to write. */
  @Input() hasActionPlans = false;
  @Input() hasReviews = false;

  @Output() cancelled = new EventEmitter<void>();
  @Output() confirmed = new EventEmitter<GapAnalysisExportSelection>();

  readonly actionPlanCols = [...ACTION_PLAN_EXPORT_COLUMNS];
  readonly reviewCols = [...REVIEW_EXPORT_COLUMNS];

  gapCols: string[] = [];
  selectedGap = new Set<string>();
  selectedActionPlan = new Set<string>(ACTION_PLAN_EXPORT_COLUMNS);
  selectedReview = new Set<string>(REVIEW_EXPORT_COLUMNS);

  includeActionPlans = true;
  includeReviews = true;

  isOn(set: Set<string>, col: string): boolean {
    return set.has(col);
  }

  toggle(set: Set<string>, col: string): void {
    if (set.has(col)) set.delete(col);
    else set.add(col);
  }

  toggleAll(set: Set<string>, cols: string[], on: boolean): void {
    set.clear();
    if (on) for (const c of cols) set.add(c);
  }

  get canConfirm(): boolean {
    return this.selectedGap.size > 0;
  }

  confirm(): void {
    this.confirmed.emit({
      gapColumns: [...this.selectedGap],
      includeActionPlans: this.hasActionPlans && this.includeActionPlans,
      actionPlanColumns: [...this.selectedActionPlan],
      includeReviews: this.hasReviews && this.includeReviews,
      reviewColumns: [...this.selectedReview],
    });
  }
}
