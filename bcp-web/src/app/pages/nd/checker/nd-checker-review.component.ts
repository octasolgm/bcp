import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import { countCapGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { resolveAnalysisPointSeverity } from '../../../../lib/nd/point-compliance-status';
import {
  flattenActionItemReviews,
  pointHasActionReviewFlag,
  type ActionItemReviewDraft,
  type ActionItemReviewStatus,
} from '../../../../lib/nd/action-item-review';
import type { ActionPlanHistoryEntry, AnalysisPoint, ResultsData } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-checker-review',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdGapPointDetailComponent],
  templateUrl: './nd-checker-review.component.html',
  styleUrls: ['../nd-shared.scss'],
})
export class NdCheckerReviewComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  runId = '';
  data: ResultsData | null = null;
  selectedId: string | null = null;
  overallComment = '';
  pointComments: Record<string, string> = {};
  actionItemReviews: Record<string, Record<number, ActionItemReviewDraft>> = {};
  loading = true;
  submitting = false;
  error = '';
  history: ActionPlanHistoryEntry[] = [];
  historyOpen = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
    const res = await this.api.getResults(this.runId);
    if (res.success && res.data) {
      this.data = res.data as ResultsData;
      if (this.data.points.length > 0) this.selectedId = this.data.points[0].id;
    }
    this.loading = false;
  }

  get selected(): AnalysisPoint | null {
    return this.data?.points.find((p) => p.id === this.selectedId) ?? null;
  }

  selectPoint(id: string): void {
    this.selectedId = id;
    this.historyOpen = false;
    this.history = [];
  }

  async openHistory(): Promise<void> {
    if (!this.selectedId) return;
    const res = await this.api.getActionPlanHistory(this.runId, this.selectedId);
    if (res.success && res.data) {
      this.history = res.data as ActionPlanHistoryEntry[];
      this.historyOpen = true;
    }
  }

  closeHistory(): void {
    this.historyOpen = false;
    this.history = [];
  }

  showCap(point: AnalysisPoint): boolean {
    return true;
  }

  pointSeverity(point: AnalysisPoint): string {
    return resolveAnalysisPointSeverity(point);
  }

  gapCountForPoint(point: AnalysisPoint): number {
    return countCapGapsForAnalysisPoint(point);
  }

  actionReviewsForSelected(): Record<number, ActionItemReviewDraft> {
    if (!this.selectedId) return {};
    return this.actionItemReviews[this.selectedId] ?? {};
  }

  updateActionItemReview(event: {
    actionIndex: number;
    status?: ActionItemReviewStatus;
    comment: string;
  }): void {
    if (!this.selectedId) return;
    const current = { ...(this.actionItemReviews[this.selectedId] ?? {}) };
    const prev = current[event.actionIndex] ?? { status: '' as const, comment: '' };
    current[event.actionIndex] = {
      status: event.status ?? prev.status,
      comment: event.comment,
    };
    this.actionItemReviews = { ...this.actionItemReviews, [this.selectedId]: current };
  }

  updatePointComment(value: string): void {
    if (!this.selectedId) return;
    this.pointComments = { ...this.pointComments, [this.selectedId]: value };
  }

  get selectedComment(): string {
    return this.selectedId ? (this.pointComments[this.selectedId] ?? '') : '';
  }

  hasComment(pointId: string): boolean {
    return !!this.pointComments[pointId]?.trim() || pointHasActionReviewFlag(pointId, this.actionItemReviews);
  }

  async handleApprove(): Promise<void> {
    await this.submitReview('approve');
  }

  async handlePullBack(): Promise<void> {
    await this.submitReview('pullback');
  }

  private async submitReview(action: 'approve' | 'pullback'): Promise<void> {
    this.submitting = true;
    this.error = '';
    const comments = Object.entries(this.pointComments)
      .filter(([, c]) => c.trim())
      .map(([analysisPointId, comment]) => ({ analysisPointId, comment }));
    const actionItemReviews = flattenActionItemReviews(this.actionItemReviews);
    const body = {
      overallComment: this.overallComment.trim() || undefined,
      pointComments: comments,
      actionItemReviews: actionItemReviews.length ? actionItemReviews : undefined,
    };
    const res =
      action === 'approve'
        ? await this.api.approveAnalysis(this.runId, body)
        : await this.api.pullBackAnalysis(this.runId, body);
    if (res.success) {
      await this.router.navigate(['/nd/checker']);
    } else {
      this.error = res.message ?? `Failed to ${action === 'approve' ? 'approve' : 'pull back'}`;
    }
    this.submitting = false;
  }

  parsePointSnapshot = parsePointSnapshot;
}
