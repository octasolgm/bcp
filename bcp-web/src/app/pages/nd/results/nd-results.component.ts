import { Component, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate, parsePointSnapshot } from '../../../../lib/nd/utils';
import { exportResultsExcel } from '../../../../lib/nd/export/export-excel';
import { exportResultsPdf } from '../../../../lib/nd/export/export-pdf';
import type { ActionPlanHistoryEntry, AnalysisPoint, ResultsData } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-results',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdGapPointDetailComponent],
  templateUrl: './nd-results.component.html',
  styleUrls: ['./nd-results.component.scss', '../nd-shared.scss'],
})
export class NdResultsComponent implements OnInit, OnChanges {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);

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
  editingPointId: string | null = null;
  capSavingPointId: string | null = null;
  history: ActionPlanHistoryEntry[] = [];
  historyPointId: string | null = null;
  actionLoading = false;
  expandedPointIds = new Set<string>();

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.resolveRunId();
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
    return this.data?.points.filter((p) => p.finalStatus === 'compliant').length ?? 0;
  }

  get partialCount(): number {
    return this.data?.points.filter((p) => p.finalStatus === 'partial_compliant').length ?? 0;
  }

  get nonCompliantCount(): number {
    return this.data?.points.filter((p) => p.finalStatus === 'non_compliant').length ?? 0;
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getResults(this.runId);
    if (res.success && res.data) {
      this.data = res.data as ResultsData;
      if (this.embedMode && this.data.points.length) {
        const first =
          this.data.points.find((p) => p.finalStatus === 'partial_compliant' || p.finalStatus === 'non_compliant') ??
          this.data.points[0];
        this.expandedPointIds = new Set([first.id]);
      }
    } else {
      this.error = res.message ?? 'Failed to load results';
    }
    this.loading = false;
  }

  get filteredPoints(): AnalysisPoint[] {
    if (!this.data) return [];
    return this.data.points.filter((p) => {
      if (this.statusFilter === 'dual_verify_failed' && p.dualVerifyStatus !== 'failed') return false;
      if (
        this.statusFilter !== 'all' &&
        this.statusFilter !== 'dual_verify_failed' &&
        p.finalStatus !== this.statusFilter
      )
        return false;
      if (!this.search.trim()) return true;
      const snap = parsePointSnapshot(p.pointSnapshot);
      const hay = `${snap.pointNumber ?? ''} ${snap.pointTitle ?? ''} ${snap.pointContent ?? ''}`.toLowerCase();
      return hay.includes(this.search.toLowerCase());
    });
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
    if (!this.data || !this.isMaker) return false;
    return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(this.data.run.status);
  }

  finalStatusLabel(status: string | null | undefined): string {
    if (!status) return '—';
    if (status === 'compliant') return 'Compliance';
    if (status === 'partial_compliant') return 'Partial compliance';
    if (status === 'non_compliant') return 'Non-compliance';
    return status.replace(/_/g, ' ');
  }

  showCap(point: AnalysisPoint): boolean {
    if (point.finalActionPlan?.trim() || point.originalAiActionPlan?.trim()) return true;
    return point.finalStatus === 'partial_compliant' || point.finalStatus === 'non_compliant';
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
