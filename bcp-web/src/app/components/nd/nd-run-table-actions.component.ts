import { Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import {
  canDeleteRun,
  canEditRunPlans,
  canSendRunForReview,
  runViewActionLabel,
  submitRunActionLabel,
} from '../../../lib/nd/analysis-run-actions';
import {
  analysisRunNeedsExecutionView,
  isLegacyAnalysisRun,
  ndAnalysisRunLink,
  ndAnalysisRunQuery,
} from '../../../lib/nd/run-links';
import type { AnalysisRunSummary } from '../../../lib/nd/types';

@Component({
  selector: 'app-nd-run-table-actions',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="row-actions-compact">
      <div class="row-actions-icons">
        @if (!legacy) {
          <button
            type="button"
            class="row-action-btn icon-only ghost"
            title="History"
            aria-label="History"
            (click)="onHistory($event)"
          >
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <path
                fill="none"
                stroke="currentColor"
                stroke-width="1.6"
                d="M10 4.5v5.25l3 1.75M10 18a8 8 0 100-16 8 8 0 000 16z"
              />
            </svg>
          </button>
        }

        @if (showStop) {
          <button
            type="button"
            class="row-action-btn icon-only danger"
            title="Stop analysis"
            aria-label="Stop analysis"
            [disabled]="stoppingId === run.id"
            (click)="onStop($event)"
          >
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <rect x="5.5" y="5.5" width="9" height="9" rx="1.2" fill="currentColor" stroke="none" />
            </svg>
          </button>
        }

        <a
          class="row-action-btn icon-only"
          [class.primary]="viewPrimary"
          [routerLink]="runLink()"
          [queryParams]="runQuery() || null"
          [title]="viewLabel"
          [attr.aria-label]="viewLabel"
          (click)="$event.stopPropagation()"
        >
          @if (viewLabel === 'Review' || viewLabel === 'Final review') {
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <path
                fill="none"
                stroke="currentColor"
                stroke-width="1.6"
                d="M6 10l2.5 2.5L14 7M10 18a8 8 0 100-16 8 8 0 000 16z"
              />
            </svg>
          } @else if (viewLabel === 'Continue') {
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <path
                fill="none"
                stroke="currentColor"
                stroke-width="1.6"
                d="M7 5l6 5-6 5V5z"
              />
            </svg>
          } @else {
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <path
                fill="none"
                stroke="currentColor"
                stroke-width="1.6"
                d="M3 10s3.5-5.5 7-5.5 7 5.5 7 5.5-3.5 5.5-7 5.5S3 10 3 10z"
              />
              <circle cx="10" cy="10" r="2.25" fill="currentColor" stroke="none" />
            </svg>
          }
        </a>

        @if (showDelete) {
          <button
            type="button"
            class="row-action-btn icon-only danger"
            title="Delete"
            aria-label="Delete"
            [disabled]="deletingId === run.id"
            (click)="onDelete($event)"
          >
            <svg viewBox="0 0 20 20" width="16" height="16" aria-hidden="true">
              <path
                fill="none"
                stroke="currentColor"
                stroke-width="1.6"
                d="M5 6h10M8 6V4.5h4V6M7.5 6l.5 9h4l.5-9"
              />
            </svg>
          </button>
        }
      </div>

      @if (showSubmit || showEditPlans) {
        <div class="row-actions-labeled">
          @if (showSubmit) {
            <button
              type="button"
              class="row-action-btn primary compact"
              [disabled]="submittingRunId === run.id"
              (click)="onSubmit($event)"
            >
              {{ submittingRunId === run.id ? 'Submitting…' : submitLabel }}
            </button>
          }
          @if (showEditPlans) {
            <a
              class="row-action-btn compact"
              [routerLink]="['/nd/gap-analysis']"
              [queryParams]="{ run: run.id }"
              title="Edit plans"
              (click)="$event.stopPropagation()"
            >
              Edit plans
            </a>
          }
        </div>
      }
    </div>
  `,
})
export class NdRunTableActionsComponent {
  private readonly auth = inject(NdAuthService);

  @Input({ required: true }) run!: AnalysisRunSummary;
  @Input() submittingRunId: string | null = null;
  @Input() deletingId: string | null = null;
  @Input() stoppingId: string | null = null;
  /** Checker/reviewer queue: Review vs View only in icon row. */
  @Input() queueReview = false;
  @Input() viewOnly = false;
  @Input() reviewLabel = 'Review';
  /** Overview / compact lists: icon row only (no Resubmit / Edit plans). */
  @Input() hideLabeledActions = false;

  @Output() historyClick = new EventEmitter<AnalysisRunSummary>();
  @Output() submitClick = new EventEmitter<AnalysisRunSummary>();
  @Output() deleteClick = new EventEmitter<AnalysisRunSummary>();
  @Output() stopClick = new EventEmitter<AnalysisRunSummary>();

  get legacy(): boolean {
    return isLegacyAnalysisRun(this.run);
  }

  get role(): string | null {
    return this.auth.getRole();
  }

  get showStop(): boolean {
    if (this.legacy || this.queueReview || this.viewOnly) return false;
    const role = this.role;
    if (role !== 'maker' && role !== 'super_admin') return false;
    return analysisRunNeedsExecutionView(this.run);
  }

  get showSubmit(): boolean {
    return !this.hideLabeledActions && canSendRunForReview(this.run, this.role);
  }

  get showEditPlans(): boolean {
    return !this.hideLabeledActions && canEditRunPlans(this.run, this.role);
  }

  get showDelete(): boolean {
    return canDeleteRun(this.run, this.role, this.auth.profile()?.id);
  }

  get submitLabel(): string {
    return submitRunActionLabel(this.run);
  }

  get viewLabel(): string {
    if (this.queueReview && !this.viewOnly) return this.reviewLabel;
    return runViewActionLabel(this.run, this.role, {
      queueReview: this.queueReview,
      viewOnly: this.viewOnly,
    });
  }

  get viewPrimary(): boolean {
    return this.viewLabel === 'Review' || this.viewLabel === 'Final review';
  }

  runLink(): string[] {
    return ndAnalysisRunLink(this.run, this.role);
  }

  runQuery(): Record<string, string> | undefined {
    return ndAnalysisRunQuery(this.run, this.role);
  }

  onHistory(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.historyClick.emit(this.run);
  }

  onSubmit(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.submitClick.emit(this.run);
  }

  onDelete(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.deleteClick.emit(this.run);
  }

  onStop(event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.stopClick.emit(this.run);
  }
}
