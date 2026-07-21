import {
  Component,
  EventEmitter,
  inject,
  Input,
  OnChanges,
  OnDestroy,
  OnInit,
  Output,
  SimpleChanges,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { downloadExcelRows } from '../../../../lib/ai-lab/excel-write';
import {
  progressPointToReportItem,
  savedResultToReportItem,
  type DualVerifyReportItem,
} from '../../../../lib/dual-verify-report';
import {
  ApiService,
  type DualVerifySessionSummary,
} from '../../../services/api.service';
import { reportItemsToGapItems } from '../../../services/gap-analysis-mapper';
import {
  clearGapDrafts,
  clearGapItems,
  gapSeverityLabel,
  loadGapDrafts,
  loadGapItems,
  normalizeGapSeverity,
  saveGapDrafts,
  type GapDraftOverlay,
  type GapItemData,
  type GapSeverity,
} from '../../../services/reguliq-store';
import { analysisPointToReportItem } from '../../../../lib/nd/analysis-point-mapper';
import type { AnalysisPoint } from '../../../../lib/nd/types';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { DualVerifyResultCardComponent } from '../../../components/dual-verify-result-card/dual-verify-result-card.component';
import type { ActionPlanHistoryEntry, ResultsData } from '../../../../lib/nd/types';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import { countCapGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { resolveAnalysisPointSeverity } from '../../../../lib/nd/point-compliance-status';
import { parseReferenceComplianceBlock } from '../../../../lib/ai-lab/parse-reference-response';

/** Seeded TFS × IMPTFS combined compliance session (32 points). */
const SEEDED_COMPLIANCE_SESSION = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';

@Component({
  selector: 'app-nd-gap-analysis',
  standalone: true,
  imports: [FormsModule, RouterLink, NgTemplateOutlet, NdStatusBadgeComponent, DualVerifyResultCardComponent, NdGapPointDetailComponent],
  templateUrl: './nd-gap-analysis.component.html',
  styleUrl: './nd-gap-analysis.component.scss',
})
export class NdGapAnalysisComponent implements OnInit, OnChanges, OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly ndApi = inject(NdApiService);
  readonly auth = inject(NdAuthService);
  private saveTimer: ReturnType<typeof setTimeout> | null = null;

  /** Embedded below the analyse-v8 columns: no page header, run supplied via input. */
  @Input() embedMode = false;
  @Input() embedRunId: string | null = null;
  @Output() runStatusChange = new EventEmitter<string>();

  exporting = false;
  loading = true;
  deletingSession = false;
  loadError: string | null = null;
  sourceLabel = 'I M P T F S.pdf vs. TFS Guidelines';
  sessionKey = '';
  /** Raw session id for delete API (from ?session= or ?saved=compliance:…) */
  deletableSessionId: string | null = null;
  deletableSessionKind: 'dual' | 'compliance' | null = null;
  pointIds: string[] = [];
  pdfPreview: { title: string; page: string; body: string } | null = null;

  activeFilter = 'all';
  viewMode: 'cards' | 'list' = 'cards';
  selectedItemId: string | null = null;
  ndRunId: string | null = null;
  ndRunStatus = '';
  ndRunData: ResultsData | null = null;
  ndPolicyDocId: string | null = null;
  ndRegulationDocId: string | null = null;
  ndSearchQuery = '';
  workflowLoading = false;
  editingPointId: string | null = null;
  capSavingPointId: string | null = null;
  history: ActionPlanHistoryEntry[] = [];
  historyPointId: string | null = null;
  ndDetailError = '';

  readonly filters: { id: 'all' | GapSeverity; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'compliant', label: 'Compliance' },
    { id: 'partial_compliant', label: 'Partial compliance' },
    { id: 'non_compliant', label: 'Non-compliance' },
  ];

  items: GapItemData[] = [];
  reportByPointId = new Map<string, DualVerifyReportItem>();

  ngOnInit(): void {
    void this.auth.refreshProfile();
    if (this.embedMode) {
      if (this.embedRunId) {
        this.loadFromQuery(null, null, null, null, this.embedRunId);
      }
      return;
    }
    // Drop old localStorage demo gaps (§2.1 / §2.3 placeholders).
    const loaded = loadGapItems();
    const looksLikeDemo = loaded.some(
      (i) =>
        i.id === '01' &&
        i.section === '§2.1' &&
        /Senior Management SCP Approval/i.test(i.title),
    );
    if (looksLikeDemo) clearGapItems();

    this.route.queryParamMap.subscribe((params) => {
      const filter = params.get('filter');
      if (filter) {
        const normalized =
          filter === 'all' ? 'all' : normalizeGapSeverity(filter);
        if (this.filters.some((f) => f.id === normalized)) {
          this.activeFilter = normalized;
        }
      }

      const session = params.get('session');
      const saved = params.get('saved');
      const runId = params.get('run');
      this.loadFromQuery(session, saved, params.get('section'), params.get('focus'), runId);
    });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.embedMode) return;
    const change = changes['embedRunId'];
    if (change && !change.firstChange && this.embedRunId) {
      this.loadFromQuery(null, null, null, null, this.embedRunId);
    }
  }

  get summary() {
    return {
      compliant: this.items.filter((i) => i.severity === 'compliant').length,
      partialCompliant: this.items.filter((i) => i.severity === 'partial_compliant').length,
      nonCompliant: this.items.filter((i) => i.severity === 'non_compliant').length,
    };
  }

  get filteredItems(): GapItemData[] {
    let list = this.items;
    if (this.activeFilter !== 'all') {
      list = list.filter((i) => i.severity === this.activeFilter);
    }
    const q = this.ndSearchQuery.trim().toLowerCase();
    if (!q) return list;
    return list.filter(
      (i) =>
        i.title.toLowerCase().includes(q) ||
        i.section.toLowerCase().includes(q) ||
        i.regulatoryText.toLowerCase().includes(q),
    );
  }

  severityLabel = gapSeverityLabel;

  reportItemForGap(item: GapItemData): DualVerifyReportItem | null {
    const key = item.section.replace(/^§/, '');
    return this.reportByPointId.get(key) ?? null;
  }

  analysisPointForGap(item: GapItemData): AnalysisPoint | null {
    if (!this.ndRunData) return null;
    const key = item.section.replace(/^§/, '');
    return (
      this.ndRunData.points.find((p) => {
        const snap = parsePointSnapshot(p.pointSnapshot);
        const pid = snap.pointNumber || p.regulationPointId || p.id;
        return pid === key || p.id === key;
      }) ?? null
    );
  }

  gapCountForItem(item: GapItemData): number {
    const ndPoint = this.analysisPointForGap(item);
    if (ndPoint) return countCapGapsForAnalysisPoint(ndPoint);
    return item.gapCount ?? 0;
  }

  listRowMeta(item: GapItemData): {
    policySnippet: string;
    fulfills: string;
    actionPlan: string;
    confidence: string;
    status: string;
  } {
    const ndPoint = this.analysisPointForGap(item);
    const report = this.reportItemForGap(item);
    let policySnippet = item.policyText?.trim() || '—';
    let confidence = '—';
    let fulfills = '—';
    let status = this.severityLabel(item.severity);
    let actionPlan = item.gaps?.trim() || item.managementResponse?.trim() || '—';

    if (ndPoint) {
      actionPlan =
        ndPoint.finalActionPlan?.trim() ||
        ndPoint.originalAiActionPlan?.trim() ||
        actionPlan;
      const severity = resolveAnalysisPointSeverity(ndPoint);
      status =
        severity === 'compliant'
          ? 'Compliant'
          : severity === 'partial_compliant'
            ? 'Partial'
            : severity === 'non_compliant'
              ? 'Non-compliant'
              : status;
    }

    if (report?.llmMessage || report?.landingMessage) {
      const block = parseReferenceComplianceBlock((report.llmMessage || report.landingMessage || '').trim());
      if (block.outputResponse?.trim()) {
        policySnippet = block.outputResponse.trim().slice(0, 120);
        if (block.outputResponse.length > 120) policySnippet += '…';
      }
      if (block.confidence?.trim()) confidence = block.confidence.trim();
      if (block.fulfilledClauses?.trim()) {
        const lines = block.fulfilledClauses.split('\n').filter((l) => l.trim());
        fulfills = lines.length ? `${lines.length} item(s)` : '—';
      }
    }

    return { policySnippet, fulfills, actionPlan: actionPlan.slice(0, 80), confidence, status };
  }

  async rerunAllNdDualVerify(): Promise<void> {
    if (!this.ndRunId) return;
    this.workflowLoading = true;
    const res = await this.ndApi.rerunAllFailedDualVerify(this.ndRunId);
    this.workflowLoading = false;
    if (res.success) {
      this.toast.show('Rerunning failed dual verify checks…', 'success');
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.toast.show(res.message ?? 'Could not rerun dual verify', 'error');
    }
  }

  get showDualVerifyFailedBanner(): boolean {
    const status = this.ndRunData?.run.status ?? '';
    return (
      status === 'dual_verify_failed' ||
      (this.ndRunData?.run.dualVerifyFailedCount ?? 0) > 0
    );
  }

  get canEditNdCap(): boolean {
    if (!this.ndRunData || !this.ndRunId) return false;
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return !['submitted_for_review', 'checker_approved', 'reviewer_approved'].includes(this.ndRunData.run.status);
  }

  snapshotForPoint(point: AnalysisPoint) {
    return parsePointSnapshot(point.pointSnapshot);
  }

  regDocIdForPoint(point: AnalysisPoint): string | null {
    const snap = this.snapshotForPoint(point);
    return snap.regulationDocumentId ?? this.ndRegulationDocId;
  }

  policyDocIdForPoint(_point: AnalysisPoint): string | null {
    return this.ndPolicyDocId;
  }

  startEditCap(point: AnalysisPoint): void {
    this.editingPointId = point.id;
    this.closeHistory();
  }

  cancelEditCap(): void {
    this.editingPointId = null;
  }

  async saveCap(pointId: string, content: string): Promise<void> {
    if (!this.ndRunId) return;
    this.capSavingPointId = pointId;
    this.ndDetailError = '';
    const res = await this.ndApi.updateActionPlan(this.ndRunId, pointId, content);
    this.capSavingPointId = null;
    if (res.success) {
      const point = this.ndRunData?.points.find((p) => p.id === pointId);
      if (point) point.finalActionPlan = content;
      this.editingPointId = null;
      if (this.historyPointId === pointId) await this.loadHistory(pointId);
    } else {
      this.ndDetailError = res.message ?? 'Failed to save action plan';
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

  private async loadHistory(pointId: string): Promise<void> {
    if (!this.ndRunId) return;
    const res = await this.ndApi.getActionPlanHistory(this.ndRunId, pointId);
    if (res.success && res.data) {
      this.history = res.data as ActionPlanHistoryEntry[];
      this.historyPointId = pointId;
    }
  }

  async revertToVersion(version: ActionPlanHistoryEntry): Promise<void> {
    if (!this.ndRunId || !this.historyPointId) return;
    const res = await this.ndApi.updateActionPlan(
      this.ndRunId,
      this.historyPointId,
      version.actionPlanContent,
      version.versionNumber,
    );
    if (res.success) {
      const point = this.ndRunData?.points.find((p) => p.id === this.historyPointId);
      if (point) point.finalActionPlan = version.actionPlanContent;
      this.editingPointId = null;
      await this.loadHistory(this.historyPointId);
    } else {
      this.ndDetailError = res.message ?? 'Failed to restore version';
    }
  }

  get subtitle(): string {
    if (this.loading) return 'Loading analysis results…';
    if (this.loadError) return this.sourceLabel;
    const n = this.items.length;
    return `${this.sourceLabel} — ${n} finding${n === 1 ? '' : 's'}`;
  }

  get canDeleteSession(): boolean {
    return !!this.deletableSessionId && !!this.deletableSessionKind && !this.deletingSession;
  }

  confirmDeleteSession(): void {
    if (!this.deletableSessionId || !this.deletableSessionKind) return;
    const label = this.sourceLabel || this.deletableSessionId;
    const ok = window.confirm(
      `Delete analysis session "${label}" permanently?\n\nThis removes the session from the database. Draft edits for this report will also be cleared.`,
    );
    if (!ok) return;

    this.deletingSession = true;
    const id = this.deletableSessionId;
    const kind = this.deletableSessionKind;
    const key = this.sessionKey;

    const onDone = (message: string) => {
      this.deletingSession = false;
      if (key) clearGapDrafts(key);
      this.toast.show(message, 'success');
      this.router.navigate(['/gap-analysis']);
    };

    const onFail = (message: string) => {
      this.deletingSession = false;
      this.toast.show(message, 'error');
    };

    if (kind === 'compliance') {
      this.api.deleteComplianceSession(id).subscribe({
        next: () => onDone('Compliance session deleted'),
        error: (e) => onFail(e?.error?.message ?? 'Could not delete compliance session'),
      });
      return;
    }

    this.api.deleteDualVerifySession(id).subscribe({
      next: () => onDone('Analysis session deleted'),
      error: () => onDone('Session removed (may already be gone on server)'),
    });
  }

  get canSubmitNdReview(): boolean {
    if (!this.ndRunData) return false;
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(
      this.ndRunData.run.status,
    );
  }

  get ndWorkflowHint(): string {
    const status = this.ndRunData?.run.status ?? '';
    if (status === 'dual_verify_failed') {
      const n = this.ndRunData?.run.dualVerifyFailedCount ?? 0;
      return n > 0
        ? `${n} point(s) failed dual verify — Landing AI results are kept. Rerun dual verify or edit action plans before submit.`
        : 'Dual verify failed on one or more points — review Phase 2 output and rerun if needed.';
    }
    if (status === 'submitted_for_review') return 'Submitted to checker — awaiting review.';
    if (status === 'checker_approved') return 'Checker approved — with reviewer for final sign-off.';
    if (status === 'reviewer_approved') return 'Final review complete.';
    if (status === 'pulled_back') return 'Pulled back by checker — edit action plans and resubmit.';
    return '';
  }

  async submitNdReview(): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;
    this.workflowLoading = true;
    const res =
      this.ndRunData.run.status === 'pulled_back'
        ? await this.ndApi.resubmitForReview(this.ndRunId)
        : await this.ndApi.submitForReview(this.ndRunId);
    this.workflowLoading = false;
    if (res.success) {
      this.toast.show('Sent to checker for review', 'success');
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.toast.show(res.message ?? 'Could not submit for review', 'error');
    }
  }

  openNdResultsEditor(): void {
    if (this.ndRunId) void this.router.navigate(['/nd/gap-analysis'], { queryParams: { run: this.ndRunId } });
  }

  openCheckerQueue(): void {
    void this.router.navigate(['/nd/checker']);
  }

  openReviewerQueue(): void {
    void this.router.navigate(['/nd/reviewer']);
  }

  setFilter(id: string): void {
    this.activeFilter = id;
    this.ensureListSelection();
  }

  setViewMode(mode: 'cards' | 'list'): void {
    this.viewMode = mode;
    if (mode === 'list') this.ensureListSelection();
  }

  private ensureListSelection(): void {
    const list = this.filteredItems;
    if (!list.length) {
      this.selectedItemId = null;
      return;
    }
    if (!this.selectedItemId || !list.some((i) => i.id === this.selectedItemId)) {
      this.selectedItemId = list[0].id;
    }
  }

  selectGapItem(item: GapItemData): void {
    this.selectedItemId = item.id;
    this.editingPointId = null;
    this.closeHistory();
  }

  get selectedGapItem(): GapItemData | null {
    if (!this.selectedItemId) return null;
    return this.filteredItems.find((i) => i.id === this.selectedItemId) ?? null;
  }

  toggleItem(item: GapItemData): void {
    item.expanded = !item.expanded;
    this.persistSoon();
  }

  collapseAllItems(): void {
    for (const item of this.items) item.expanded = false;
    this.persistSoon();
  }

  expandAllItems(): void {
    for (const item of this.items) item.expanded = true;
    this.persistSoon();
  }

  onFieldChange(): void {
    this.persistSoon();
  }

  openPdf(kind: 'reg' | 'policy', item: GapItemData): void {
    this.pdfPreview = {
      title: kind === 'reg' ? 'Regulatory requirement source' : 'Policy extract source',
      page: kind === 'reg' ? item.regPage : item.policyPage,
      body: kind === 'reg' ? item.regulatoryText : item.policyText,
    };
  }

  closePdf(): void {
    this.pdfPreview = null;
  }

  openPdfFromNd(event: { docId: string; page?: string | null }): void {
    this.api.getDocumentSignedUrl(event.docId).subscribe({
      next: (r) => {
        if (!r.url) {
          this.toast.show('Could not open PDF', 'error');
          return;
        }
        const url = event.page ? `${r.url}#page=${event.page}` : r.url;
        window.open(url, '_blank', 'noopener');
      },
      error: () => this.toast.show('Could not open PDF', 'error'),
    });
  }

  async exportXlsx(): Promise<void> {
    if (this.exporting) return;
    this.exporting = true;
    try {
      const headers = [
        'ID',
        'Section',
        'Title',
        'Severity',
        'Regulatory Requirement',
        'Policy Extract',
        'Gaps Identified',
        'Management Response',
        'Design Effectiveness',
        'Operating Effectiveness',
        'Overall Effectiveness',
        'Document Reference',
        'Evidence',
      ];
      const rows = this.items.map((i) => [
        i.id,
        i.section,
        i.title,
        gapSeverityLabel(i.severity),
        i.regulatoryText,
        i.policyText,
        i.gaps,
        i.managementResponse,
        i.designEffectiveness,
        i.operatingEffectiveness,
        i.overallEffectiveness,
        i.documentReference,
        i.evidence,
      ]);
      await downloadExcelRows(
        `reguliq-gap-analysis-${new Date().toISOString().slice(0, 10)}.xlsx`,
        'Gap Analysis',
        headers,
        rows,
        [8, 10, 36, 12, 40, 40, 36, 36, 18, 18, 18, 22, 36],
      );
      this.toast.show('Exported gap analysis Excel file', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exporting = false;
    }
  }

  ngOnDestroy(): void {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.persistDrafts();
  }

  private loadFromQuery(
    session: string | null,
    saved: string | null,
    section: string | null,
    focus: string | null,
    runId: string | null,
  ): void {
    this.loading = true;
    this.loadError = null;
    this.items = [];
    this.reportByPointId = new Map();
    this.pointIds = [];
    this.deletableSessionId = null;
    this.deletableSessionKind = null;

    if (runId) {
      this.ndRunId = runId;
      this.sessionKey = `nd-run:${runId}`;
      void this.loadNdRun(runId, section, focus);
      return;
    }

    this.ndRunId = null;
    this.ndRunData = null;
    this.ndRunStatus = '';

    if (session) {
      this.sessionKey = `session:${session}`;
      this.deletableSessionId = session;
      this.deletableSessionKind = 'dual';
      this.sourceLabel = 'Dual-verify session';
      this.api
        .getJob(session)
        .pipe(catchError(() => this.api.getNestJob(session)))
        .subscribe({
          next: (r) => {
            if (!r?.data?.session) {
              this.loading = false;
              this.loadError =
                'This analysis session was deleted or is no longer available.';
              return;
            }
            const points = r.data?.points ?? [];
            const report = points
              .filter(
                (p) =>
                  p.status === 'completed' ||
                  p.status === 'failed' ||
                  p.agreementJson ||
                  p.landingMessage ||
                  p.llmMessage,
              )
              .map((p) =>
                progressPointToReportItem({
                  pointId: p.pointId,
                  pointTitle: p.pointTitle,
                  status: p.status,
                  landingMessage: p.landingMessage,
                  llmMessage: p.llmMessage,
                  agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
                  errorMessage: p.errorMessage,
                }),
              );
            this.applyReport(report, section, focus);
          },
          error: () => {
            this.loading = false;
            this.loadError =
              'Could not load this analysis session. It may have been deleted — try opening from Documents again or run a new analysis on V2.';
            this.toast.show(this.loadError, 'error');
          },
        });
      return;
    }

    if (saved?.startsWith('compliance:')) {
      const id = saved.slice('compliance:'.length);
      this.sessionKey = `compliance:${id}`;
      this.deletableSessionId = id;
      this.deletableSessionKind = 'compliance';
      this.sourceLabel = 'I M P T F S.pdf vs. TFS Guidelines';
      this.api.loadComplianceSession(id).subscribe({
        next: (r) => {
          const report: DualVerifyReportItem[] = [];
          for (const row of (r.results as Record<string, unknown>[]) ?? []) {
            const item = savedResultToReportItem(
              row as Parameters<typeof savedResultToReportItem>[0],
            );
            if (item) report.push(item);
          }
          this.applyReport(report, section, focus);
        },
        error: () => {
          this.loading = false;
          this.loadError = 'Could not load compliance session.';
          this.toast.show(this.loadError, 'error');
        },
      });
      return;
    }

    // Default: latest completed dual-verify, else seeded compliance bundle.
    this.resolveDefaultSession(section, focus);
  }

  private resolveDefaultSession(section: string | null, focus: string | null): void {
    forkJoin({
      dual: this.api.listDualVerifySessions().pipe(
        map((r) => r.data ?? []),
        catchError(() => of([] as DualVerifySessionSummary[])),
      ),
      compliance: this.api.listComplianceSessions().pipe(
        map((r) => r.sessions ?? []),
        catchError(() => of([])),
      ),
    }).subscribe({
      next: ({ dual, compliance }) => {
        // Prefer the full TFS × IMPTFS compliance bundle (or richest compliance
        // session) — not a recent partial dual-verify smoke run (1–25 pts).
        const seeded =
          compliance.find((s) => s.id === SEEDED_COMPLIANCE_SESSION) ??
          [...compliance]
            .filter((s) => (s.comparedPoints ?? 0) > 0)
            .sort((a, b) => (b.comparedPoints ?? 0) - (a.comparedPoints ?? 0))[0];

        const bestCompliancePts = seeded?.comparedPoints ?? 0;
        const latestDual = [...dual]
          .filter(
            (s) =>
              s.transport !== 'db' &&
              s.status === 'completed' &&
              s.completedPoints > 0,
          )
          .sort((a, b) => (b.completedPoints ?? 0) - (a.completedPoints ?? 0))[0];

        const dualPts = latestDual?.completedPoints ?? 0;
        if (seeded && bestCompliancePts >= dualPts) {
          this.trySeededCompliance(compliance, section, focus);
          return;
        }

        if (latestDual) {
          this.sessionKey = `session:${latestDual.id}`;
          this.deletableSessionId = latestDual.id;
          this.deletableSessionKind = 'dual';
          this.sourceLabel =
            latestDual.label || 'I M P T F S.pdf vs. TFS Guidelines';
          this.api.getJob(latestDual.id).subscribe({
            next: (r) => {
              const points = r.data?.points ?? [];
              const report = points
                .filter((p) => p.status === 'completed' && (p.landingMessage || p.llmMessage))
                .map((p) =>
                  progressPointToReportItem({
                    pointId: p.pointId,
                    pointTitle: p.pointTitle,
                    status: p.status,
                    landingMessage: p.landingMessage,
                    llmMessage: p.llmMessage,
                    agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
                    errorMessage: p.errorMessage,
                  }),
                );
              if (report.length) {
                this.applyReport(report, section, focus);
              } else {
                this.trySeededCompliance(compliance, section, focus);
              }
            },
            error: () => this.trySeededCompliance(compliance, section, focus),
          });
          return;
        }

        this.trySeededCompliance(compliance, section, focus);
      },
      error: () => {
        this.loading = false;
        this.loadError = null;
      },
    });
  }

  private trySeededCompliance(
    sessions: { id: string; label?: string; comparedPoints?: number }[],
    section: string | null,
    focus: string | null,
  ): void {
    const seeded =
      sessions.find((s) => s.id === SEEDED_COMPLIANCE_SESSION) ??
      sessions.find((s) => (s.comparedPoints ?? 0) > 0);

    if (!seeded) {
      this.loading = false;
      this.sessionKey = '';
      this.items = [];
      return;
    }

    this.sessionKey = `compliance:${seeded.id}`;
    this.deletableSessionId = seeded.id;
    this.deletableSessionKind = 'compliance';
    this.sourceLabel = seeded.label || 'I M P T F S.pdf vs. TFS Guidelines';
    this.api.loadComplianceSession(seeded.id).subscribe({
      next: (r) => {
        const report: DualVerifyReportItem[] = [];
        for (const row of (r.results as Record<string, unknown>[]) ?? []) {
          const item = savedResultToReportItem(
            row as Parameters<typeof savedResultToReportItem>[0],
          );
          if (item) report.push(item);
        }
        this.applyReport(report, section, focus);
      },
      error: () => {
        this.loading = false;
        this.items = [];
      },
    });
  }

  private async loadNdRun(
    runId: string,
    section: string | null,
    focus: string | null,
  ): Promise<void> {
    const [res, runRes] = await Promise.all([
      this.ndApi.getResults(runId),
      this.ndApi.getAnalysisRun(runId),
    ]);
    if (!res.success || !res.data) {
      this.loading = false;
      this.loadError = res.message ?? 'Could not load analysis results.';
      this.toast.show(this.loadError, 'error');
      return;
    }

    const data = res.data as ResultsData;
    this.ndRunData = data;
    this.ndRunStatus = data.run.status;
    this.runStatusChange.emit(data.run.status);
    this.sourceLabel = data.run.name || 'Analysis run';
    this.ndPolicyDocId =
      runRes.success && runRes.data
        ? this.firstDocIdFromRunDetail(runRes.data, 'selectedInternalDocIds')
        : null;
    this.ndRegulationDocId =
      runRes.success && runRes.data
        ? this.firstDocIdFromRunDetail(runRes.data, 'selectedRegulationDocIds')
        : null;

    const report = data.points
      .map((p) => analysisPointToReportItem(p))
      .filter((item): item is DualVerifyReportItem => item !== null);
    this.applyReport(report, section, focus);
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
    if (Array.isArray(value)) {
      const first = value.find((id) => typeof id === 'string' && id.trim());
      return typeof first === 'string' ? first : null;
    }
    return null;
  }

  private applyReport(
    report: DualVerifyReportItem[],
    section: string | null,
    focus: string | null,
  ): void {
    const overlays = this.sessionKey ? loadGapDrafts(this.sessionKey) : {};
    this.reportByPointId = new Map(report.map((r) => [r.pointId, r]));
    this.pointIds = report
      .filter((i) => {
        const hasBoth = Boolean(i.landingMessage?.trim() && i.llmMessage?.trim());
        const hasLandingOnly = Boolean(i.landingMessage?.trim());
        const okStatus =
          i.status === 'completed' || i.status === 'loaded' || i.status === 'failed' || !i.status;
        return (
          okStatus &&
          (hasBoth ||
            hasLandingOnly ||
            Boolean(i.agreement?.summary) ||
            Boolean(i.errorMessage))
        );
      })
      .map((i) => i.pointId);

    let items = reportItemsToGapItems(report, overlays).map((item) => ({
      ...item,
      severity: normalizeGapSeverity(item.severity),
    }));

    if (section) {
      items = items.map((i) => ({ ...i, expanded: i.section === section }));
    }
    if (focus) {
      const f = focus.toLowerCase();
      items = items.map((i) => ({
        ...i,
        expanded: i.title.toLowerCase().includes(f) || i.section.toLowerCase().includes(f),
      }));
    }

    this.items = items;
    this.loading = false;
    this.loadError = items.length
      ? null
      : 'No saved findings in this session — the run may have been cancelled, failed, or never finished.';
  }

  private persistSoon(): void {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => this.persistDrafts(), 400);
  }

  private persistDrafts(): void {
    if (!this.sessionKey || !this.pointIds.length) return;
    const overlays: Record<string, GapDraftOverlay> = {};
    this.items.forEach((item) => {
      const pointId = item.section.replace(/^§/, '');
      if (!pointId) return;
      overlays[pointId] = {
        gaps: item.gaps,
        managementResponse: item.managementResponse,
        designEffectiveness: item.designEffectiveness,
        operatingEffectiveness: item.operatingEffectiveness,
        overallEffectiveness: item.overallEffectiveness,
        documentReference: item.documentReference,
        evidence: item.evidence,
        signedOff: item.signedOff,
        expanded: item.expanded,
      };
    });
    saveGapDrafts(this.sessionKey, overlays);
  }
}
