import { Component, EventEmitter, Input, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';
import {
  RUN_REVIEW_STATUS_OPTIONS,
  emptyRunReviewDraft,
  type RunReviewDraft,
  type RunReviewStatus,
} from '../../../lib/nd/run-review';
import {
  RISK_STANDARD_SUMMARY,
  riskScoreLabel,
  riskTierFromScore,
} from '../../../lib/nd/risk-priority-score';
import type { Department } from '../../../lib/nd/types';

export type RunReviewPanelMode = 'none' | 'maker' | 'checker' | 'reviewer';

export type RunReviewSubmitEvent = {
  action: 'submit' | 'approve' | 'pullback' | 'finalize' | 'pullback_to_checker' | 'pullback_to_maker';
  draft: RunReviewDraft;
};

@Component({
  selector: 'app-nd-run-review-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-run-review-panel.component.html',
  styleUrl: './nd-run-review-panel.component.scss',
})
export class NdRunReviewPanelComponent implements OnInit {
  private readonly ndApi = inject(NdApiService);

  @Input({ required: true }) mode: RunReviewPanelMode = 'none';
  @Input() submitting = false;
  @Input() error = '';
  @Input() reviewProgress: { total: number; reviewed: number } | null = null;
  @Input() initialDraft: Partial<RunReviewDraft> | null = null;
  @Input() resubmitLabel = false;

  @Output() submitReview = new EventEmitter<RunReviewSubmitEvent>();

  draft: RunReviewDraft = emptyRunReviewDraft();
  departments: Department[] = [];
  statusOptions = RUN_REVIEW_STATUS_OPTIONS;
  readonly riskStandardLabel = RISK_STANDARD_SUMMARY;

  get priorityScoreLabel(): string {
    return riskScoreLabel(this.draft.priority);
  }

  get priorityTierClass(): string {
    return `tier-${riskTierFromScore(this.draft.priority)}`;
  }

  async ngOnInit(): Promise<void> {
    if (this.initialDraft) {
      this.draft = { ...this.draft, ...this.initialDraft };
    }
    const res = await this.ndApi.getDepartments();
    if (res.success && res.data) {
      this.departments = (res.data as Department[]).filter((d) => d.isActive !== false);
    }
  }

  get showCheckerActions(): boolean {
    return this.mode === 'checker';
  }

  get showReviewerActions(): boolean {
    return this.mode === 'reviewer';
  }

  get showMakerActions(): boolean {
    return this.mode === 'maker';
  }

  get reviewProgressComplete(): boolean {
    if (!this.reviewProgress || this.reviewProgress.total === 0) return true;
    return this.reviewProgress.reviewed >= this.reviewProgress.total;
  }

  /** Makers cannot add per-action reviews — only checker/reviewer need this progress. */
  get showActionReviewProgress(): boolean {
    if (this.mode === 'maker') return false;
    return !!this.reviewProgress && this.reviewProgress.total > 0;
  }

  setStatus(status: RunReviewStatus): void {
    this.draft = { ...this.draft, status };
  }

  setPriority(value: number): void {
    this.draft = { ...this.draft, priority: Math.min(100, Math.max(0, value)) };
  }

  emit(action: RunReviewSubmitEvent['action']): void {
    this.submitReview.emit({ action, draft: { ...this.draft } });
  }
}
