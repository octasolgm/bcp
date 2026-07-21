import { Component, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../../components/nd/nd-point-sort-controls.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ApiService } from '../../../services/api.service';
import { ToastService } from '../../../services/toast.service';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import { countDisplayGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { resolveAnalysisPointSeverity } from '../../../../lib/nd/point-compliance-status';
import {
  countSavedReviewProgress,
  pointHasSavedReviews,
  reviewsForPoint,
  validateSavedActionReviewsComplete,
  type ActionItemReviewStatus,
} from '../../../../lib/nd/action-item-review';
import { attachmentCountsByPoint, loadPointCommentsFromResults } from '../../../../lib/nd/nd-review-run-helpers';
import type { ActionPlanHistoryEntry, AnalysisPoint, InternalDocument, ResultsData } from '../../../../lib/nd/types';
import { internalDocCatalogFromRunDetail } from '../../../../lib/nd/run-internal-docs';
import type { PolicyDocCatalogEntry } from '../../../../lib/nd/policy-doc-resolve';
import { type SortDir } from '../../../../lib/nd/list-utils';
import { sortByPointKey, type PointSortMode } from '../../../../lib/nd/point-sort';

@Component({
  selector: 'app-nd-reviewer-review',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdGapPointDetailComponent, NdPointSortControlsComponent],
  templateUrl: './nd-reviewer-review.component.html',
  styleUrls: ['../nd-shared.scss'],
})
export class NdReviewerReviewComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly legacyApi = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  @ViewChild(NdGapPointDetailComponent) gapDetail?: NdGapPointDetailComponent;

  runId = '';
  data: ResultsData | null = null;
  policyDocId: string | null = null;
  policyDocCatalog: PolicyDocCatalogEntry[] = [];
  regulationDocId: string | null = null;
  pointSort: PointSortMode = 'number';
  pointSortDir: SortDir = 'asc';
  selectedId: string | null = null;
  overallComment = '';
  pointComments: Record<string, string> = {};
  savingActionReviewIndex: number | null = null;
  loading = true;
  submitting = false;
  error = '';
  history: ActionPlanHistoryEntry[] = [];
  historyOpen = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
    await this.loadData();
  }

  private async loadData(): Promise<void> {
    const [res, runRes, internalRes] = await Promise.all([
      this.api.getResults(this.runId),
      this.api.getAnalysisRun(this.runId),
      this.api.getInternalDocuments(),
    ]);
    if (res.success && res.data) {
      this.data = res.data as ResultsData;
      if (!this.selectedId && this.data.points.length > 0) this.selectedId = this.data.points[0].id;
      this.pointComments = loadPointCommentsFromResults(this.data);
    }
    this.policyDocId = this.firstDocIdFromRunDetail(runRes.data, 'selectedInternalDocIds');
    this.policyDocCatalog = internalDocCatalogFromRunDetail(
      runRes.data,
      (internalRes.data ?? []) as InternalDocument[],
    );
    this.regulationDocId = this.firstDocIdFromRunDetail(runRes.data, 'selectedRegulationDocIds');
    this.loading = false;
  }

  get selected(): AnalysisPoint | null {
    return this.data?.points.find((p) => p.id === this.selectedId) ?? null;
  }

  get sortedPoints(): AnalysisPoint[] {
    if (!this.data?.points.length) return [];
    return sortByPointKey(
      this.data.points,
      this.pointSort === 'default' ? 'number' : this.pointSort,
      this.pointSortDir,
      (p) => parsePointSnapshot(p.pointSnapshot).pointNumber ?? '',
      (p) => resolveAnalysisPointSeverity(p),
    );
  }

  onPointSortChange(event: { sort: 'number' | 'status'; dir: SortDir }): void {
    this.pointSort = event.sort;
    this.pointSortDir = event.dir;
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

  pointSeverity(point: AnalysisPoint): string {
    return resolveAnalysisPointSeverity(point);
  }

  gapCountForPoint(point: AnalysisPoint): number {
    return countDisplayGapsForAnalysisPoint(point, this.attachmentsForPoint(point.id).length);
  }

  attachmentsForPoint(pointId: string) {
    return (this.data?.pointAttachments ?? []).filter((a) => a.analysisPointId === pointId);
  }

  savedReviewsForSelected() {
    if (!this.selectedId) return [];
    return reviewsForPoint(this.data?.actionItemReviews, this.selectedId);
  }

  regDocIdForPoint(point: AnalysisPoint): string | null {
    const snap = parsePointSnapshot(point.pointSnapshot);
    return snap.regulationDocumentId ?? this.regulationDocId;
  }

  get reviewProgress(): { total: number; reviewed: number } {
    if (!this.data?.points.length) return { total: 0, reviewed: 0 };
    return countSavedReviewProgress(
      this.data.points,
      this.data.actionItemReviews,
      attachmentCountsByPoint(this.data),
    );
  }

  updatePointComment(value: string): void {
    if (!this.selectedId) return;
    this.pointComments = { ...this.pointComments, [this.selectedId]: value };
  }

  get selectedComment(): string {
    return this.selectedId ? (this.pointComments[this.selectedId] ?? '') : '';
  }

  hasComment(pointId: string): boolean {
    return !!this.pointComments[pointId]?.trim() || pointHasSavedReviews(pointId, this.data?.actionItemReviews);
  }

  async saveActionItemReview(event: {
    actionIndex: number;
    status: ActionItemReviewStatus;
    comment: string;
    responsibility: string;
    dueDate: string;
    priority: string;
  }): Promise<void> {
    if (!this.selectedId) return;
    this.savingActionReviewIndex = event.actionIndex;
    this.error = '';
    const res = await this.api.saveActionItemReview(this.runId, {
      analysisPointId: this.selectedId,
      actionIndex: event.actionIndex,
      status: event.status,
      comment: event.comment.trim() || undefined,
      responsibility: event.responsibility.trim() || undefined,
      dueDate: event.dueDate.trim() || undefined,
      priority: event.priority || undefined,
    });
    this.savingActionReviewIndex = null;
    if (res.success) {
      this.toast.show('Review saved', 'success');
      this.gapDetail?.clearReviewDraft(event.actionIndex);
      await this.loadData();
    } else {
      this.error = res.message ?? 'Could not save review';
      this.toast.show(this.error, 'error');
    }
  }

  openPdfFromNd(event: { docId: string; page?: string | null }): void {
    const openUrl = (url: string) => {
      const full = event.page ? `${url}#page=${event.page}` : url;
      window.open(full, '_blank', 'noopener');
    };
    this.legacyApi.getDocumentSignedUrl(event.docId).subscribe({
      next: (r) => {
        if (r.url) {
          openUrl(r.url);
          return;
        }
        void this.openRegulationFileUrl(event.docId, event.page);
      },
      error: () => void this.openRegulationFileUrl(event.docId, event.page),
    });
  }

  private async openRegulationFileUrl(docId: string, page?: string | null): Promise<void> {
    const res = await this.api.getRegulationDocumentFileUrl(docId);
    if (res.success && res.data?.url) {
      const url = page ? `${res.data.url}#page=${page}` : res.data.url;
      window.open(url, '_blank', 'noopener');
      return;
    }
    this.toast.show(res.message ?? 'Could not open PDF', 'error');
  }

  private firstDocIdFromRunDetail(raw: unknown, field: string): string | null {
    if (!raw || typeof raw !== 'object') return null;
    const run = (raw as { run?: Record<string, unknown> }).run;
    const value = run?.[field];
    if (typeof value === 'string') {
      try {
        const ids = JSON.parse(value) as unknown[];
        const first = ids.find((id) => typeof id === 'string' && id.trim());
        return typeof first === 'string' ? first : null;
      } catch {
        return null;
      }
    }
    return null;
  }

  private buildReviewBody() {
    const comments = Object.entries(this.pointComments)
      .filter(([, c]) => c.trim())
      .map(([analysisPointId, comment]) => ({ analysisPointId, comment }));
    return {
      overallComment: this.overallComment.trim() || undefined,
      pointComments: comments,
    };
  }

  private validateBeforeSubmit(): boolean {
    if (!this.data) return false;
    const validation = validateSavedActionReviewsComplete(
      this.data.points,
      this.data.actionItemReviews,
      attachmentCountsByPoint(this.data),
    );
    if (!validation.ok) {
      this.error = validation.message ?? 'Save a review on each action before submitting.';
      return false;
    }
    return true;
  }

  async handleFinalize(): Promise<void> {
    if (!this.validateBeforeSubmit()) return;
    this.submitting = true;
    this.error = '';
    const res = await this.api.finalizeAnalysis(this.runId, this.buildReviewBody());
    if (res.success) {
      await this.router.navigate(['/nd/reviewer']);
    } else {
      this.error = res.message ?? 'Failed to finalize';
    }
    this.submitting = false;
  }

  async handlePullBackToChecker(): Promise<void> {
    if (!this.validateBeforeSubmit()) return;
    this.submitting = true;
    this.error = '';
    const res = await this.api.pullBackToChecker(this.runId, this.buildReviewBody());
    if (res.success) {
      await this.router.navigate(['/nd/reviewer']);
    } else {
      this.error = res.message ?? 'Failed to pull back to checker';
    }
    this.submitting = false;
  }

  async handlePullBackToMaker(): Promise<void> {
    if (!this.validateBeforeSubmit()) return;
    this.submitting = true;
    this.error = '';
    const res = await this.api.pullBackToMaker(this.runId, this.buildReviewBody());
    if (res.success) {
      await this.router.navigate(['/nd/reviewer']);
    } else {
      this.error = res.message ?? 'Failed to pull back to maker';
    }
    this.submitting = false;
  }

  parsePointSnapshot = parsePointSnapshot;
}
