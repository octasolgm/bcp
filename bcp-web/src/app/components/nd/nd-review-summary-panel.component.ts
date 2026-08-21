import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  assigneesForPlan,
  formatActionPlanDate,
  isActionPlanOverdue,
  type ActionPlanEntry,
  type ActionPlanReviewEntry,
} from '../../../lib/nd/action-plan';

type ReviewRow = ActionPlanReviewEntry & { clause: string; planText: string };

/**
 * Summary a checker or reviewer sees before signing off: how much is still open,
 * what each action commits to, and the review trail filtered by date.
 */
@Component({
  selector: 'app-nd-review-summary-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-review-summary-panel.component.html',
  styleUrl: './nd-review-summary-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdReviewSummaryPanelComponent {
  @Input() plans: ActionPlanEntry[] = [];
  /** Point id → clause number, so rows can name the clause they belong to. */
  @Input() clauseByPointId = new Map<string, string>();

  readonly formatDate = formatActionPlanDate;
  readonly isOverdue = isActionPlanOverdue;

  /** Inclusive yyyy-mm-dd bounds on review date; blank means unbounded. */
  fromDate = '';
  toDate = '';

  collapsed = false;

  get pendingPlans(): ActionPlanEntry[] {
    return this.plans.filter((p) => p.status !== 'resolved');
  }

  get resolvedCount(): number {
    return this.plans.filter((p) => p.status === 'resolved').length;
  }

  get overdueCount(): number {
    return this.plans.filter((p) => isActionPlanOverdue(p)).length;
  }

  /** Distinct gaps that still carry at least one open action. */
  get pendingPointCount(): number {
    return new Set(this.pendingPlans.map((p) => p.analysisPointId)).size;
  }

  clauseFor(plan: ActionPlanEntry): string {
    return this.clauseByPointId.get(plan.analysisPointId) ?? '';
  }

  responsibleFor(plan: ActionPlanEntry): string {
    const owners = assigneesForPlan(plan).filter((a) => a.label.trim());
    if (!owners.length) return 'Unassigned';
    return owners.map((o) => o.label).join(', ');
  }

  get reviews(): ReviewRow[] {
    const rows: ReviewRow[] = [];
    for (const plan of this.plans) {
      for (const review of plan.reviews ?? []) {
        rows.push({ ...review, clause: this.clauseFor(plan), planText: plan.actionPlan });
      }
    }
    return rows
      .filter((r) => this.withinRange(r.createdAt))
      .sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1));
  }

  private withinRange(iso: string): boolean {
    if (!this.fromDate && !this.toDate) return true;
    const day = (iso ?? '').slice(0, 10);
    if (!day) return false;
    if (this.fromDate && day < this.fromDate) return false;
    if (this.toDate && day > this.toDate) return false;
    return true;
  }

  get totalReviewCount(): number {
    return this.plans.reduce((sum, p) => sum + (p.reviews?.length ?? 0), 0);
  }

  get isFiltered(): boolean {
    return !!(this.fromDate || this.toDate);
  }

  clearRange(): void {
    this.fromDate = '';
    this.toDate = '';
  }
}
