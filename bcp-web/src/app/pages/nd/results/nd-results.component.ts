import { Component, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../../components/nd/nd-point-sort-controls.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { formatDate, parsePointSnapshot } from '../../../../lib/nd/utils';
import { countDisplayGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import {
  resolveAnalysisPointSeverity,
  resolvePointComplianceLabel,
} from '../../../../lib/nd/point-compliance-status';
import { exportResultsExcel } from '../../../../lib/nd/export/export-excel';
import { exportResultsPdf } from '../../../../lib/nd/export/export-pdf';
import type { ActionPlanHistoryEntry, AnalysisPoint, PointGapAttachment, ResultsData } from '../../../../lib/nd/types';
import { reviewsForPoint, type ActionItemReviewEntry, type ActionItemReviewStatus } from '../../../../lib/nd/action-item-review';
import { tempCommentsForPoint, type TempPointReviewComment, type TempReviewCommentsChangeEvent } from '../../../../lib/nd/temp-point-review-comment';
import { canAddActionItemReviews, isReviewRole, reviewDisabledHint } from '../../../../lib/nd/nd-review-run-helpers';
import type { ComplianceSeverity } from '../../../../lib/nd/point-compliance-status';
import { type SortDir } from '../../../../lib/nd/list-utils';
import { sortByPointKey, type PointSortMode } from '../../../../lib/nd/point-sort';

@Component({
  selector: 'app-nd-results',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdGapPointDetailComponent, NdPointSortControlsComponent],
  templateUrl: './nd-results.component.html',
  styleUrls: ['./nd-results.component.scss', '../nd-shared.scss'],
})
export class NdResultsComponent implements OnInit, OnChanges {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  @Input() embedMode = false;
  @Input() embedRunId: string | null = null;
  @Input() policyDocId: string | null = null;
  @Input() regulationDocId: string | null = null;
  @Output() runStatusChange = new EventEmitter<string>();
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();

  runId = '';
  data: ResultsData | null = null;
  loading = true;
  error = '';
  statusFilter = 'all';
  search = '';
  viewMode: 'cards' | 'list' = 'cards';
  editingPointId: string | null = null;
  capSavingPointId: string | null = null;
  history: ActionPlanHistoryEntry[] = [];
  historyPointId: string | null = null;
  actionLoading = false;
  expandedPointIds = new Set<string>();
  selectedPointId: string | null = null;
  pointSort: PointSortMode = 'number';
  pointSortDir: SortDir = 'asc';
  evidenceUploadingPointId: string | null = null;
  evidenceRerunningPointId: string | null = null;
  evidenceUploadingActionIndex: number | null = null;
  evidenceRerunningActionIndex: number | null = null;
  savingActionReviewIndex: number | null = null;
  savingReviewId: string | null = null;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.resolveRunId();
    if (!this.embedMode && this.runId) {
      await this.router.navigate(['/nd/gap-analysis'], {
        queryParams: { run: this.runId },
        replaceUrl: true,
      });
      return;
    }
    await this.load();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['embedRunId'] && !changes['embedRunId'].firstChange) {
      this.resolveRunId();
      void this.load();
    }
  }

  private resolveRunId(): void {
    this.runId = this.embedRunId ?? this.route.snapshot.paramMap.get('runId') ?? '';
  }

  get compliantCount(): number {
    return this.data?.points.filter((p) => resolveAnalysisPointSeverity(p) === 'compliant').length ?? 0;
  }

  get partialCount(): number {
    return this.data?.points.filter((p) => resolveAnalysisPointSeverity(p) === 'partial_compliant').length ?? 0;
  }

  get nonCompliantCount(): number {
    return this.data?.points.filter((p) => resolveAnalysisPointSeverity(p) === 'non_compliant').length ?? 0;
  }

  pointSeverity(point: AnalysisPoint): ComplianceSeverity | null {
    return resolveAnalysisPointSeverity(point);
  }

  finalStatusLabel(point: AnalysisPoint): string {
    return resolvePointComplianceLabel(point);
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getResults(this.runId);
    if (res.success && res.data) {
      this.data = res.data as ResultsData;
      if (this.embedMode && this.data.points.length) {
        this.expandedPointIds = new Set();
      } else if (this.data.points.length) {
        this.selectDefaultPoint();
      }
    } else {
      this.error = res.message ?? 'Failed to load results';
    }
    this.loading = false;
  }

  get selectedPoint(): AnalysisPoint | null {
    if (!this.data || !this.selectedPointId) return null;
    return this.data.points.find((p) => p.id === this.selectedPointId) ?? null;
  }

  get showPointsRail(): boolean {
    return this.viewMode === 'list';
  }

  setViewMode(mode: 'cards' | 'list'): void {
    this.viewMode = mode;
    if (mode === 'list' && !this.selectedPointId) this.selectDefaultPoint();
  }

  private selectDefaultPoint(): void {
    if (!this.data?.points.length) {
      this.selectedPointId = null;
      return;
    }
    const preferred =
      this.data.points.find((p) => {
        const s = resolveAnalysisPointSeverity(p);
        return s === 'partial_compliant' || s === 'non_compliant';
      }) ?? this.data.points[0];
    this.selectedPointId = preferred.id;
  }

  selectPoint(pointId: string): void {
    this.selectedPointId = pointId;
    this.editingPointId = null;
    if (this.historyPointId && this.historyPointId !== pointId) {
      this.closeHistory();
    }
  }

  isPointSelected(pointId: string): boolean {
    return this.selectedPointId === pointId;
  }

  get filteredPoints(): AnalysisPoint[] {
    if (!this.data) return [];
    const filtered = this.data.points.filter((p) => {
      if (this.statusFilter === 'dual_verify_failed' && p.dualVerifyStatus !== 'failed') return false;
      if (
        this.statusFilter !== 'all' &&
        this.statusFilter !== 'dual_verify_failed' &&
        resolveAnalysisPointSeverity(p) !== this.statusFilter
      )
        return false;
      if (!this.search.trim()) return true;
      const snap = parsePointSnapshot(p.pointSnapshot);
      const hay = `${snap.pointNumber ?? ''} ${snap.pointTitle ?? ''} ${snap.pointContent ?? ''}`.toLowerCase();
      return hay.includes(this.search.toLowerCase());
    });
    return sortByPointKey(
      filtered,
      this.pointSort,
      this.pointSortDir,
      (p) => parsePointSnapshot(p.pointSnapshot).pointNumber ?? '',
      (p) => resolveAnalysisPointSeverity(p) ?? '',
    );
  }

  onPointSortChange(event: { sort: 'number' | 'status'; dir: SortDir }): void {
    this.pointSort = event.sort;
    this.pointSortDir = event.dir;
  }

  get canExport(): boolean {
    if (!this.data) return false;
    return [
      'completed',
      'dual_verify_failed',
      'landing_ai_complete',
      'submitted_for_review',
      'checker_approved',
      'reviewer_approved',
      'pulled_back',
    ].includes(this.data.run.status);
  }

  get isMaker(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get canEditCap(): boolean {
    if (!this.data || !this.isMaker) return false;
    return !['submitted_for_review', 'checker_approved', 'reviewer_approved'].includes(this.data.run.status);
  }

  get canSubmitReview(): boolean {
    return false;
  }

  showCap(point: AnalysisPoint): boolean {
    if (point.finalActionPlan?.trim() || point.originalAiActionPlan?.trim()) return true;
    const severity = resolveAnalysisPointSeverity(point);
    return severity === 'partial_compliant' || severity === 'non_compliant';
  }

  gapCountForPoint(point: AnalysisPoint): number {
    return countDisplayGapsForAnalysisPoint(point, this.attachmentsForPoint(point.id).length);
  }

  attachmentsForPoint(pointId: string): PointGapAttachment[] {
    return (this.data?.pointAttachments ?? []).filter((a) => a.analysisPointId === pointId);
  }

  savedReviewsForPoint(pointId: string): ActionItemReviewEntry[] {
    return reviewsForPoint(this.data?.actionItemReviews, pointId);
  }

  savedTempCommentsForPoint(pointId: string): TempPointReviewComment[] {
    return tempCommentsForPoint(this.data?.tempReviewComments ?? [], pointId);
  }

  get canEditTempReviewComments(): boolean {
    const role = this.auth.getRole();
    return role === 'super_admin' || role === 'checker' || role === 'reviewer' || role === 'maker';
  }

  onTempReviewCommentsChanged(event: TempReviewCommentsChangeEvent): void {
    if (!this.data) return;
    const others = (this.data.tempReviewComments ?? []).filter(
      (c) => c.analysisPointId !== event.analysisPointId,
    );
    this.data = { ...this.data, tempReviewComments: [...others, ...event.comments] };
  }

  get canReviewActionGaps(): boolean {
    return canAddActionItemReviews(this.auth.getRole(), this.data?.run.status);
  }

  get canShowReviewPanel(): boolean {
    return isReviewRole(this.auth.getRole());
  }

  get gapReviewDisabledHint(): string {
    return reviewDisabledHint(this.auth.getRole(), this.data?.run.status);
  }

  get canUploadGapEvidence(): boolean {
    return this.canEditCap;
  }

  async saveActionItemReview(
    pointId: string,
    event: {
      reviewId?: string;
      actionIndex: number;
      status: ActionItemReviewStatus;
      comment: string;
      responsibility: string;
      dueDate: string;
      priority: string;
    },
  ): Promise<void> {
    if (!this.runId) return;
    this.savingActionReviewIndex = event.actionIndex;
    this.savingReviewId = event.reviewId ?? null;
    this.error = '';
    const body = {
      status: event.status,
      comment: event.comment.trim() || undefined,
      responsibility: event.responsibility.trim() || undefined,
      dueDate: event.dueDate.trim() || undefined,
      priority: event.priority || undefined,
    };
    const res = event.reviewId
      ? await this.api.updateActionItemReview(this.runId, event.reviewId, body)
      : await this.api.saveActionItemReview(this.runId, {
          analysisPointId: pointId,
          actionIndex: event.actionIndex,
          ...body,
        });
    this.savingActionReviewIndex = null;
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show(event.reviewId ? 'Review updated' : 'Review saved', 'success');
      await this.load();
    } else {
      this.error = res.message ?? 'Could not save review';
      this.toast.show(this.error, 'error');
    }
  }

  async deleteActionItemReview(pointId: string, reviewId: string): Promise<void> {
    if (!this.runId) return;
    this.savingReviewId = reviewId;
    const res = await this.api.deleteActionItemReview(this.runId, reviewId);
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show('Review deleted', 'success');
      await this.load();
    } else {
      this.error = res.message ?? 'Could not delete review';
      this.toast.show(this.error, 'error');
    }
    void pointId;
  }

  async reorderActionItemReview(
    pointId: string,
    event: { reviewId: string; actionIndex: number; direction: 'up' | 'down' },
  ): Promise<void> {
    if (!this.runId) return;
    this.savingReviewId = event.reviewId;
    this.savingActionReviewIndex = event.actionIndex;
    this.error = '';
    const res = await this.api.reorderActionItemReview(this.runId, event.reviewId, event.direction);
    this.savingReviewId = null;
    this.savingActionReviewIndex = null;
    if (res.success) {
      await this.load();
    } else {
      this.error = res.message ?? 'Could not reorder review';
      this.toast.show(this.error, 'error');
    }
    void pointId;
  }

  async onUploadGapEvidence(pointId: string, fileList: FileList, actionIndex?: number): Promise<void> {
    if (!fileList.length) return;
    this.evidenceUploadingPointId = pointId;
    this.evidenceUploadingActionIndex = actionIndex ?? null;
    this.error = '';
    const files = Array.from(fileList);
    const res = await this.api.uploadPointGapAttachments(this.runId, pointId, files, actionIndex);
    this.evidenceUploadingPointId = null;
    this.evidenceUploadingActionIndex = null;
    if (res.success) {
      this.toast.show(`Uploaded ${files.length} file(s)`, 'success');
      await this.load();
    } else {
      this.error = res.message ?? 'Upload failed';
      this.toast.show(this.error, 'error');
    }
  }

  async onDeleteGapEvidence(pointId: string, attachmentId: string): Promise<void> {
    const res = await this.api.deletePointGapAttachment(this.runId, pointId, attachmentId);
    if (res.success) {
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not remove file', 'error');
    }
  }

  async onRerunWithEvidence(pointId: string, mode: 'full' | 'dual'): Promise<void> {
    this.evidenceRerunningPointId = pointId;
    this.evidenceRerunningActionIndex = null;
    this.error = '';
    const opts = { evidenceOnly: true };
    const res =
      mode === 'dual'
        ? await this.api.rerunDualVerify(this.runId, pointId, opts)
        : await this.api.rerunPoint(this.runId, pointId, opts);
    this.evidenceRerunningPointId = null;
    if (res.success) {
      this.toast.show('Rerunning analysis for this point…', 'success');
      await this.load();
    } else {
      this.error = res.message ?? 'Rerun failed';
      this.toast.show(this.error, 'error');
    }
  }

  async onRerunGapEvidence(
    pointId: string,
    payload: { actionIndex: number; mode: 'full' | 'dual' },
  ): Promise<void> {
    this.evidenceRerunningPointId = pointId;
    this.evidenceRerunningActionIndex = payload.actionIndex;
    this.error = '';
    const opts = { evidenceOnly: true, actionIndex: payload.actionIndex };
    const res =
      payload.mode === 'dual'
        ? await this.api.rerunDualVerify(this.runId, pointId, opts)
        : await this.api.rerunPoint(this.runId, pointId, opts);
    this.evidenceRerunningPointId = null;
    this.evidenceRerunningActionIndex = null;
    if (res.success) {
      this.toast.show('Rerunning analysis for this gap…', 'success');
      await this.load();
    } else {
      this.error = res.message ?? 'Rerun failed';
      this.toast.show(this.error, 'error');
    }
  }

  regDocIdForPoint(point: AnalysisPoint): string | null {
    const snap = parsePointSnapshot(point.pointSnapshot);
    return snap.regulationDocumentId ?? this.regulationDocId;
  }

  togglePointExpanded(pointId: string): void {
    if (this.expandedPointIds.has(pointId)) this.expandedPointIds.delete(pointId);
    else this.expandedPointIds.add(pointId);
  }

  isPointExpanded(pointId: string): boolean {
    return this.embedMode ? this.expandedPointIds.has(pointId) : true;
  }

  expandAllPoints(): void {
    if (!this.data) return;
    this.expandedPointIds = new Set(this.data.points.map((p) => p.id));
  }

  collapseAllPoints(): void {
    this.expandedPointIds = new Set();
  }

  phaseSummary(point: AnalysisPoint): string {
    if (point.landingAiStatus === 'failed') return 'Phase 1 · Landing AI failed';
    if (point.googleAiStatus === 'failed' && point.landingAiStatus !== 'failed')
      return 'Phase 1 ✓ · Phase 2 failed';
    if (point.dualVerifyStatus === 'failed') return 'Phase 1 ✓ · Dual verify mismatch';
    return 'Phase 1 ✓ · Phase 2 ✓';
  }

  startEditCap(point: AnalysisPoint): void {
    this.editingPointId = point.id;
    this.closeHistory();
  }

  cancelEditCap(): void {
    this.editingPointId = null;
  }

  private patchPointActionPlan(pointId: string, content: string): void {
    const point = this.data?.points.find((p) => p.id === pointId);
    if (point) point.finalActionPlan = content;
  }

  async saveCap(pointId: string, content: string): Promise<void> {
    this.capSavingPointId = pointId;
    this.error = '';
    const res = await this.api.updateActionPlan(this.runId, pointId, content);
    this.capSavingPointId = null;
    if (res.success) {
      this.patchPointActionPlan(pointId, content);
      this.editingPointId = null;
      if (this.historyPointId === pointId) await this.loadHistory(pointId);
    } else {
      this.error = res.message ?? 'Failed to save action plan';
    }
  }

  openHistory(pointId: string): void {
    this.editingPointId = null;
    this.selectPoint(pointId);
    void this.loadHistory(pointId);
  }

  closeHistory(): void {
    this.historyPointId = null;
    this.history = [];
  }

  async toggleHistory(pointId: string): Promise<void> {
    if (this.historyPointId === pointId) this.closeHistory();
    else this.openHistory(pointId);
  }

  private async loadHistory(pointId: string): Promise<void> {
    const res = await this.api.getActionPlanHistory(this.runId, pointId);
    if (res.success && res.data) {
      this.history = res.data as ActionPlanHistoryEntry[];
      this.historyPointId = pointId;
    }
  }

  async revertToVersion(version: ActionPlanHistoryEntry): Promise<void> {
    if (!this.historyPointId) return;
    const res = await this.api.updateActionPlan(
      this.runId,
      this.historyPointId,
      version.actionPlanContent,
      version.versionNumber,
    );
    if (res.success) {
      this.patchPointActionPlan(this.historyPointId, version.actionPlanContent);
      this.editingPointId = null;
      await this.loadHistory(this.historyPointId);
    } else {
      this.error = res.message ?? 'Failed to restore version';
    }
  }

  async submitReview(): Promise<void> {
    this.actionLoading = true;
    const fn =
      this.data?.run.status === 'pulled_back'
        ? this.api.resubmitForReview(this.runId)
        : this.api.submitForReview(this.runId);
    const res = await fn;
    if (res.success) {
      await this.load();
      if (this.data?.run.status) this.runStatusChange.emit(this.data.run.status);
    } else this.error = res.message ?? 'Failed to submit for review';
    this.actionLoading = false;
  }

  async rerunPoint(pointId: string): Promise<void> {
    const res = await this.api.rerunPoint(this.runId, pointId);
    if (!res.success) this.error = res.message ?? 'Rerun failed';
    else await this.load();
  }

  async rerunDualOnly(pointId: string): Promise<void> {
    const res = await this.api.rerunDualVerify(this.runId, pointId);
    if (!res.success) this.error = res.message ?? 'Phase 2 rerun failed';
    else await this.load();
  }

  onOpenPdf(event: { docId: string; page?: string | null }): void {
    this.openPdf.emit(event);
  }

  exportPdf(): void {
    if (this.data) exportResultsPdf(this.data);
  }

  async exportExcel(): Promise<void> {
    if (this.data) await exportResultsExcel(this.data);
  }

  formatDate = formatDate;
  parsePointSnapshot = parsePointSnapshot;
}
