import { Directive, HostListener, inject, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { forkJoin, from, of, Subscription } from 'rxjs';
import { catchError, finalize } from 'rxjs/operators';
import { ToastService } from '../../services/toast.service';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';
import { WorkspaceService } from '../../services/workspace.service';
import {
  ApiService,
  type GovPoint,
  type SessionProgress,
  type StoredDocumentDto,
} from '../../services/api.service';

type SessionPoint = SessionProgress['points'][number];
import {
  analyzeGovPointSet,
  buildPointDisplayChapters,
  buildRegulationDocPointDisplay,
  formatPointCountSummary,
  formatStoredAnalyseMeta,
  type LibraryPointDisplayChapter,
  type LibraryPointDisplayDoc,
  type LibraryPointDisplayRow,
} from '../../../lib/library-points-utils';
import {
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  pointMatchesPrefix,
  type GovPointChapterGroup,
} from '../../../lib/gov-point-filter';
import { ANALYSIS_ROUTES } from '../../navigation/analysis-routes';
import { shellRouteSegments } from '../../services/app-route-prefix';
import {
  progressPointToReportItem,
  savedResultToReportItem,
  type DualVerifyReportItem,
} from '../../../lib/dual-verify-report';
import { parseReferenceComplianceBlock } from '../../../lib/ai-lab/parse-reference-response';
import { NdApiService } from '../../services/nd/nd-api.service';
import {
  dedupeRegulationDocuments,
  normalizeRegulationPoint,
  regulationPointToGovPoint,
  regulationPointToNdGovPoint,
  sortRegulationDocuments,
  type NdGovPoint,
} from '../../../lib/regulation-catalog-utils';
import { parsePointSnapshot } from '../../../lib/nd/utils';
import type { AnalysisPoint, AnalysisRunSummary, RegulationDocument } from '../../../lib/nd/types';
import { analysisRunNeedsExecutionView } from '../../../lib/nd/run-links';
import {
  needsPhase2Rerun,
  normalizeSessionPointStatus,
} from '../../../lib/session-point-status';
import { reportItemsToGapItems } from '../../services/gap-analysis-mapper';
import {
  gapSeverityLabel,
  gapSeverityShortLabel,
  type GapItemData,
  type GapSeverity,
} from '../../services/reguliq-store';

/** Seeded TFS × IMPTFS compliance session (32 points in DB). */
export const SEEDED_DEMO_COMPLIANCE_SESSION = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';
const DEMO_POINT_DELAY_MS = 250;

export type AnalysisState = 'idle' | 'running' | 'complete';
export type RegViewMode = 'grid' | 'list';
export type RegPanelMode = 'uploaded' | 'upload';
export type CompliancePanelMode = 'uploaded' | 'upload';

export type PointPhaseChipState = 'idle' | 'running' | 'ok' | 'fail' | 'warn' | 'skip';

export type PointPhaseChip = {
  label: string;
  state: PointPhaseChipState;
};

export type PointPhaseDisplay = {
  phase1: PointPhaseChip;
  phase2: PointPhaseChip;
};

export type RegCard = {
  id: string;
  title: string;
  source: string;
  clauses: number;
  type: string;
  matchHash?: string;
  matchTitle?: RegExp;
  documentId?: string;
};

export type AnalysisStep = { label: string; done: boolean; active: boolean };

export type PointDetail = {
  pointId: string;
  title: string;
  status: string;
  severity: string;
  solved: boolean;
  summary: string;
  section: string;
};

/** Shared logic for original Analyse page and v2–v7 design variants. */
@Directive()
export abstract class AnalyseBase implements OnInit, OnDestroy {
  protected readonly toast = inject(ToastService);
  protected readonly route = inject(ActivatedRoute);
  protected readonly router = inject(Router);
  protected readonly api = inject(ApiService);
  protected readonly ndApi = inject(NdApiService);

  /** When true, load regulations from the ND catalog (DB-backed points). */
  protected useNdRegulationCatalog = false;
  protected readonly workspace = inject(WorkspaceService);
  protected readonly activeSessions = inject(ActiveAnalysisSessionsService);

  readonly TFS_HASH =
    'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff';
  readonly IMPTFS_HASH =
    '6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717';

  readonly regCards: RegCard[] = [
    {
      id: 'tfs',
      title: 'TFS Guidelines',
      source: 'CBUAE · July 2021',
      clauses: 96,
      type: 'Guidance',
      matchHash: 'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff',
      matchTitle: /tfs guidelines/i,
    },
  ];

  readonly designVersions = [...ANALYSIS_ROUTES].filter((v) => v.path !== '/analyse');

  /** V5 left-rail tab */
  activeSetupTab: 'regulation' | 'compliance' = 'regulation';

  highlightedCardIds = new Set<string>();
  regViewMode: RegViewMode = 'list';
  regPanelMode: RegPanelMode = 'uploaded';
  regSearch = '';
  regDropdownOpen = false;

  regulationDocs: StoredDocumentDto[] = [];
  selectedRegIds = new Set<string>();
  selectedRegDocs: StoredDocumentDto[] = [];

  storageConfigured = false;
  loadingRegs = false;
  loadingPoints = false;
  uploadingReg = false;
  uploadingCompliance = false;
  loadingCompliance = false;
  attachingCompliance = false;
  compliancePanelMode: CompliancePanelMode = 'uploaded';
  complianceSearch = '';
  complianceDocs: StoredDocumentDto[] = [];
  selectedComplianceDocId: string | null = null;
  selectedComplianceIds = new Set<string>();

  pendingRegBump: { file: File; nextVersion: string; title: string } | null = null;
  pendingComplianceBump: { file: File; nextVersion: string; title: string } | null = null;

  analysisState: AnalysisState = 'idle';
  pointsCollapsed = false;
  progress = 0;
  analysisSteps: AnalysisStep[] = [
    { label: 'Parsing and chunking document', done: false, active: false },
    { label: 'Loading regulation clauses', done: false, active: false },
    { label: 'Cross-referencing requirements', done: false, active: false },
    { label: 'Identifying gaps and risk levels', done: false, active: false },
    { label: 'Generating remediation actions', done: false, active: false },
  ];

  govSearch = '';
  rawGovPoints: GovPoint[] = [];
  govPoints: GovPoint[] = [];
  chapterGroups: GovPointChapterGroup[] = [];
  pointDisplayChapters: LibraryPointDisplayChapter[] = [];
  pointDisplayFlatRows: LibraryPointDisplayRow[] = [];
  pointDisplayUseChapters = false;
  regulationDisplayDocs: LibraryPointDisplayDoc[] = [];
  regulationDisplayUseDocGroups = false;
  expandedRegDocKeys = new Set<string>();
  expandedRegChapterKeys = new Set<string>();
  expandedChapters = new Set<string>();
  selected = new Set<string>();
  govSourceLabel = '';
  error = '';

  complianceFile: File | null = null;
  complianceDoc: StoredDocumentDto | null = null;
  complianceFileName = '';
  complianceFileSize = '';
  seededImptfs: StoredDocumentDto | null = null;

  /** Point selected in results column for detail view */
  selectedDetailPointId: string | null = null;
  sessionPointResults = new Map<string, SessionPoint>();

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private ndRunPollTimer: ReturnType<typeof setInterval> | null = null;
  protected ndRunId: string | null = null;
  ndRunWorkflowStatus = '';
  /** Set from ND run detail when resuming — used to restore library mode in analyse-v8. */
  protected ndRunLibraryId: string | null = null;
  protected ndRunDualVerifyFailedCount = 0;
  showInlineGapReport = false;
  inlineGapItems: GapItemData[] = [];
  inlineGapFilter: 'all' | GapSeverity = 'all';
  private analysisCompleteUiDone = false;
  private sessionParamSub: Subscription | null = null;
  granularity: 'leaf' | 'section' = 'leaf';
  aiModel = 'gemini-3.5-flash';
  forceRefresh = false;
  sessionId: string | null = null;
  progressDone = 0;
  progressTotal = 0;
  findingsPreview: Array<{ severity: string; title: string; section: string; pointId: string }> = [];
  sessionPointStatus = new Map<string, string>();
  retryingPointId: string | null = null;
  isDemoRun = false;
  demoNdRunId: string | null = null;
  demoSaveInProgress = false;
  demoSaveError = '';
  private demoTimer: ReturnType<typeof setTimeout> | null = null;
  private demoQueue: string[] = [];
  private demoResultsByPoint = new Map<string, DualVerifyReportItem>();
  /** Point IDs that belong to the active dual-verify session (subset of loaded gov points). */
  protected sessionSelectedPointIds: Set<string> | null = null;
  private pointsLoadGen = 0;

  /** ND library mode: points loaded from a saved library instead of regulation file selection. */
  protected useLibraryPoints = false;
  protected libraryPrimaryRegDocId: string | null = null;
  protected librarySourceLabel = '';

  /** Regulation file mode: stored vs analysis-ready point counts. */
  protected regStoredCount = 0;
  protected regSkippedCount = 0;
  protected regComparableCounts = new Map<string, number>();

  abstract readonly versionLabel: string;
  abstract readonly versionPath: string;

  ngOnInit(): void {
    this.checkStorage();
    const pendingSession = this.route.snapshot.queryParamMap.get('session');
    const pendingRun = this.route.snapshot.queryParamMap.get('run');
    this.refreshRegulations(() => {
      if (!pendingSession && !pendingRun) this.autoSelectTfs();
    });
    this.refreshComplianceDocs(() => {
      if (!pendingSession && !pendingRun) this.autoSelectImptfs();
    });
    this.sessionParamSub = this.route.queryParamMap.subscribe((params) => {
      const sid = params.get('session');
      const runId = params.get('run');
      if (sid && sid !== this.sessionId) {
        this.stopNdRunPolling();
        this.ndRunId = null;
        this.attachToExistingSession(sid);
      } else if (runId && runId !== this.ndRunId) {
        this.stopPolling();
        this.sessionId = null;
        void this.attachToNdAnalysisRun(runId);
      } else if (!sid && !runId && this.sessionId) {
        this.sessionSelectedPointIds = null;
      }
    });
  }

  ngOnDestroy(): void {
    this.sessionParamSub?.unsubscribe();
    this.stopPolling();
    this.stopNdRunPolling();
    this.stopDemoRun();
  }

  get currentSessionId(): string | null {
    return this.sessionId;
  }

  get currentNdRunId(): string | null {
    return this.ndRunId;
  }

  get hasResumableRun(): boolean {
    return Boolean(this.sessionId || this.ndRunId);
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.regDropdownOpen) this.closeRegDropdown();
  }

  get workspaceId(): string {
    return this.workspace.current().id;
  }

  get coverageRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    const statusOf = (id: string) => {
      if (this.sessionPointStatus.has(id)) return this.sessionPointStatus.get(id)!;
      return this.selected.has(id) && this.hasResumableRun ? 'queued' : 'not-run';
    };
    return this.govPoints.map((p) => ({
      pointId: p.point_id,
      title: p.title || p.text,
      status: statusOf(p.point_id),
      selected: this.selected.has(p.point_id),
    }));
  }

  /** Selected regulation points only — for analysing list & result panels */
  get selectedCoverageRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    return this.analysingListRows;
  }

  /** Points included in the current / last analysis run (excludes unrun selections). */
  get runScopePointIds(): Set<string> | null {
    if (this.analysisState !== 'complete' && this.analysisState !== 'running' && !this.hasResumableRun) {
      return null;
    }
    if (this.analysisState === 'complete' && this.sessionPointResults.size) {
      return new Set(this.sessionPointResults.keys());
    }
    if (this.demoQueue.length) return new Set(this.demoQueue);
    if (this.sessionSelectedPointIds?.size) return this.sessionSelectedPointIds;
    return null;
  }

  get analysingListRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    const rows = this.coverageRows.filter((r) => r.selected);
    const scope = this.runScopePointIds;
    if (scope) return rows.filter((r) => scope.has(r.pointId));
    return rows;
  }

  get analysingListTotal(): number {
    if (this.progressTotal > 0) return this.progressTotal;
    const scope = this.runScopePointIds;
    return scope?.size ?? this.analysingListRows.length;
  }

  get analysingListDone(): number {
    return this.analysingListRows.filter((r) => r.status === 'completed').length;
  }

  get coverageCounts(): {
    total: number;
    selected: number;
    run: number;
    notRun: number;
    failed: number;
    completed: number;
  } {
    const rows = this.coverageRows;
    const notRun = rows.filter((r) => r.status === 'not-run').length;
    const failed = rows.filter((r) => r.status === 'failed').length;
    const completed = rows.filter((r) => r.status === 'completed').length;
    const run = rows.filter((r) => r.status !== 'not-run').length;
    return {
      total: rows.length,
      selected: this.govPoints.length
        ? this.comparableSelectedIds().length
        : this.hasResumableRun && this.sessionSelectedPointIds?.size
          ? this.sessionSelectedPointIds.size
          : this.selected.size,
      run,
      notRun,
      failed,
      completed,
    };
  }

  get selectedPointDetail(): PointDetail | null {
    if (!this.selectedDetailPointId) return null;
    const p = this.govPoints.find((g) => g.point_id === this.selectedDetailPointId);
    const result = this.sessionPointResults.get(this.selectedDetailPointId);
    const status =
      this.sessionPointStatus.get(this.selectedDetailPointId) ??
      (this.selected.has(this.selectedDetailPointId) && this.hasResumableRun ? 'queued' : 'not-run');
    const agreement = result?.agreementJson;
    const severity = this.severityFromPoint(agreement);
    const solved = this.isPointSolved(agreement, status);
    return {
      pointId: this.selectedDetailPointId,
      title: p?.title || p?.text || result?.pointTitle || this.selectedDetailPointId,
      status,
      severity,
      solved,
      summary:
        agreement?.summary ||
        result?.errorMessage ||
        (status === 'not-run' ? 'This point has not been analysed yet.' : 'Analysis in progress…'),
      section: p?.section ? `§${p.section}` : `§${this.selectedDetailPointId}`,
    };
  }

  get selectedPointReportItem(): DualVerifyReportItem | null {
    if (!this.selectedDetailPointId) return null;
    const p = this.sessionPointResults.get(this.selectedDetailPointId);
    if (!p) return null;
    if (!p.landingMessage && !p.llmMessage && p.status !== 'failed' && !p.errorMessage) return null;
    const gov = this.govPoints.find((g) => g.point_id === this.selectedDetailPointId);
    return progressPointToReportItem({
      pointId: p.pointId,
      pointTitle: p.pointTitle ?? gov?.title,
      status: this.normalizeStoredPointStatus(p) as DualVerifyReportItem['status'],
      landingMessage: p.landingMessage,
      llmMessage: p.llmMessage,
      agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
      errorMessage: p.errorMessage,
    });
  }

  get canManagePoints(): boolean {
    return !!this.sessionId && (this.analysisState === 'complete' || this.analysisState === 'running');
  }

  /** True while a dual-verify session is actively running (resume from in-progress). */
  get isSessionActive(): boolean {
    if (this.analysisState === 'running') return true;
    if (!this.sessionId && !this.ndRunId) return false;
    if (this.analysisState === 'complete') {
      const active = [...this.sessionPointStatus.values()].some(
        (s) => s === 'queued' || s === 'running' || s === 'processing',
      );
      if (active) return true;
      return false;
    }
    const statuses = [...this.sessionPointStatus.values()];
    if (statuses.some((s) => s === 'queued' || s === 'running' || s === 'processing')) {
      return true;
    }
    const total = this.progressTotal ?? this.sessionSelectedPointIds?.size ?? 0;
    const done =
      (this.progressDone ?? 0) +
      statuses.filter((s) => s === 'failed').length;
    return total > 0 && done < total;
  }

  /** Done/total label for the in-progress control (e.g. 0/1). */
  get sessionProgressLabel(): string {
    const total = this.analysingListTotal;
    if (!total) return '0/0';
    const done = this.hasResumableRun ? this.analysingListDone : this.progressDone;
    return `${done}/${total}`;
  }

  get displayProgressDone(): number {
    if (!this.hasResumableRun) return this.progressDone;
    return this.coverageCounts.completed;
  }

  get showNdDualVerifyFailedBanner(): boolean {
    return (
      this.ndRunDualVerifyFailedCount > 0 ||
      this.ndRunWorkflowStatus.toLowerCase() === 'dual_verify_failed'
    );
  }

  protected async retryAllNdDualVerifyFailed(): Promise<void> {
    await this.retryAllNdPhase2();
  }

  get phase2RetryCount(): number {
    return this.coverageRows.filter((r) => this.needsPhase2RerunForPoint(r.pointId)).length;
  }

  needsPhase2RerunForPoint(pointId: string): boolean {
    const p = this.sessionPointResults.get(pointId);
    if (!p) return false;
    return needsPhase2Rerun({
      status: p.status,
      landingMessage: p.landingMessage,
      llmMessage: p.llmMessage,
      errorMessage: p.errorMessage,
    });
  }

  private normalizeStoredPointStatus(p: SessionPoint): string {
    return normalizeSessionPointStatus({
      status: p.status,
      landingMessage: p.landingMessage,
      llmMessage: p.llmMessage,
    });
  }

  formatCoverageStatus(status: string): string {
    const map: Record<string, string> = {
      completed: 'Done',
      failed: 'Failed',
      running: 'Running',
      processing: 'Running',
      queued: 'Queued',
      cancelled: 'Cancelled',
      skipped: 'No demo data',
      'not-run': 'Not run',
    };
    return map[status] ?? status;
  }

  gapComplianceLabel(severity: GapSeverity): string {
    return gapSeverityLabel(severity);
  }

  gapComplianceShortLabel(severity: GapSeverity): string {
    return gapSeverityShortLabel(severity);
  }

  getPointGapSeverity(pointId: string): GapSeverity | null {
    const p = this.sessionPointResults.get(pointId);
    if (!p) return null;
    const status = this.sessionPointStatus.get(pointId);
    if (status !== 'completed' && p.status !== 'completed') return null;
    const gov = this.govPoints.find((g) => g.point_id === pointId);
    const report = progressPointToReportItem({
      pointId: p.pointId,
      pointTitle: p.pointTitle ?? gov?.title,
      status: this.normalizeStoredPointStatus(p) as DualVerifyReportItem['status'],
      landingMessage: p.landingMessage,
      llmMessage: p.llmMessage,
      agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
      errorMessage: p.errorMessage,
    });
    if (!report) return null;
    const gaps = reportItemsToGapItems([report]);
    return gaps[0]?.severity ?? null;
  }

  get filteredInlineGapItems(): GapItemData[] {
    if (this.inlineGapFilter === 'all') return this.inlineGapItems;
    return this.inlineGapItems.filter((i) => i.severity === this.inlineGapFilter);
  }

  get inlineGapSummary(): { compliant: number; partialCompliant: number; nonCompliant: number } {
    return {
      compliant: this.inlineGapItems.filter((i) => i.severity === 'compliant').length,
      partialCompliant: this.inlineGapItems.filter((i) => i.severity === 'partial_compliant').length,
      nonCompliant: this.inlineGapItems.filter((i) => i.severity === 'non_compliant').length,
    };
  }

  buildInlineGapItems(): void {
    const reports: DualVerifyReportItem[] = [];
    for (const [pointId, p] of this.sessionPointResults) {
      const status = this.sessionPointStatus.get(pointId) ?? p.status;
      if (status !== 'completed' && status !== 'failed' && p.status !== 'completed') continue;
      const gov = this.govPoints.find((g) => g.point_id === pointId);
      const report = progressPointToReportItem({
        pointId: p.pointId,
        pointTitle: p.pointTitle ?? gov?.title,
        status: this.normalizeStoredPointStatus(p) as DualVerifyReportItem['status'],
        landingMessage: p.landingMessage,
        llmMessage: p.llmMessage,
        agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
        errorMessage: p.errorMessage,
      });
      if (report && (report.landingMessage || report.llmMessage || report.errorMessage)) {
        reports.push(report);
      }
    }
    reports.sort((a, b) =>
      this.govPointSortKey(a.pointId).localeCompare(this.govPointSortKey(b.pointId), undefined, {
        numeric: true,
      }),
    );
    this.inlineGapItems = reportItemsToGapItems(reports);
    const first =
      this.inlineGapItems.find((i) => i.severity !== 'compliant') ?? this.inlineGapItems[0];
    if (first) first.expanded = true;
  }

  protected onAnalysisComplete(): void {
    if (this.analysisCompleteUiDone) return;
    this.analysisCompleteUiDone = true;
    this.buildInlineGapItems();
    this.showInlineGapReport = true;
    const afterPersist = () => void this.refreshNdRunWorkflowStatus();
    // Demo runs persist in finishDemoRun() before this is called.
    if (!this.isDemoRun && !this.activeNdRunId && this.sessionPointResults.size > 0) {
      void this.persistDemoNdRun().then(afterPersist);
    } else {
      afterPersist();
    }
  }

  protected async refreshNdRunWorkflowStatus(): Promise<void> {
    const id = this.activeNdRunId;
    if (!id) {
      this.ndRunWorkflowStatus = '';
      return;
    }
    const res = await this.ndApi.getAnalysisRun(id);
    if (res.success && res.data) {
      const data = res.data as { run: { status: string } };
      this.ndRunWorkflowStatus = data.run.status ?? '';
    }
  }

  retryDemoSave(): void {
    if (this.demoSaveInProgress) return;
    void this.persistDemoNdRun().then(() => {
      if (this.activeNdRunId) void this.refreshNdRunWorkflowStatus();
    });
  }

  gapItemPointId(item: GapItemData): string {
    return item.section.replace(/^§\s*/, '').trim();
  }

  trackInlineGapItem(item: GapItemData): string {
    return `${item.section}|${item.expanded ? '1' : '0'}`;
  }

  toggleInlineGapItem(item: GapItemData): void {
    const idx = this.inlineGapItems.findIndex(
      (i) => i.section.trim() === item.section.trim() || i.id === item.id,
    );
    if (idx < 0) return;
    this.inlineGapItems[idx].expanded = !this.inlineGapItems[idx].expanded;
    this.inlineGapItems = [...this.inlineGapItems];
  }

  expandInlineGapItem(event: Event, item: GapItemData): void {
    event.stopPropagation();
    event.preventDefault();
    const idx = this.inlineGapItems.findIndex(
      (i) => i.section.trim() === item.section.trim() || i.id === item.id,
    );
    if (idx < 0) return;
    this.inlineGapItems[idx].expanded = true;
    this.inlineGapItems = [...this.inlineGapItems];
  }

  setInlineGapFilter(id: 'all' | GapSeverity): void {
    this.inlineGapFilter = id;
  }

  getPointPhaseStatus(pointId: string): PointPhaseDisplay | null {
    const p = this.sessionPointResults.get(pointId);
    const status = this.sessionPointStatus.get(pointId);

    if (!p && status === 'running') {
      return {
        phase1: { label: 'Phase 1', state: 'running' },
        phase2: { label: 'Phase 2', state: 'idle' },
      };
    }
    if (!p) return null;

    if ((status === 'failed' || p.status === 'failed') && !p.landingMessage) {
      return {
        phase1: { label: 'Phase 1', state: 'fail' },
        phase2: { label: 'Phase 2', state: 'skip' },
      };
    }

    if (p.runningStage?.toLowerCase().includes('landing')) {
      return {
        phase1: { label: 'Phase 1', state: 'running' },
        phase2: { label: 'Phase 2', state: 'idle' },
      };
    }

    if (
      p.runningStage?.toLowerCase().includes('llm') ||
      p.runningStage?.toLowerCase().includes('phase2')
    ) {
      return {
        phase1: { label: 'Phase 1', state: 'ok' },
        phase2: { label: 'Phase 2', state: 'running' },
      };
    }

    if (p.landingMessage && !p.llmMessage && this.needsPhase2RerunForPoint(pointId)) {
      return {
        phase1: { label: 'Phase 1', state: 'ok' },
        phase2: { label: 'Phase 2', state: 'fail' },
      };
    }

    if (p.landingMessage && p.llmMessage) {
      const agree = p.agreementJson?.status;
      const phase2State: PointPhaseChipState =
        agree && agree !== 'aligned' ? 'warn' : 'ok';
      return {
        phase1: { label: 'Phase 1', state: 'ok' },
        phase2: { label: 'Phase 2', state: phase2State },
      };
    }

    return null;
  }

  getPointPhaseLabel(pointId: string): string | null {
    const p = this.sessionPointResults.get(pointId);
    const status = this.sessionPointStatus.get(pointId);
    if (!p && status === 'running') return 'Analysing…';
    if (!p) return null;
    if ((status === 'failed' || p.status === 'failed') && !p.landingMessage) {
      return 'Phase 1 · Landing AI failed';
    }
    if (p.landingMessage && !p.llmMessage && this.needsPhase2RerunForPoint(pointId)) {
      return 'Phase 1 ✓ · Phase 2 failed';
    }
    if (p.landingMessage && p.llmMessage) {
      const agree = p.agreementJson?.status;
      if (agree && agree !== 'aligned') return 'Phase 1 ✓ · Phase 2 mismatch';
      return 'Phase 1 ✓ · Phase 2 ✓';
    }
    if (p.runningStage?.toLowerCase().includes('landing')) return 'Phase 1 · Landing AI';
    if (p.runningStage?.toLowerCase().includes('llm') || p.runningStage?.toLowerCase().includes('phase2'))
      return 'Phase 1 ✓ · Phase 2';
    return null;
  }

  complianceStatusLabel(item: DualVerifyReportItem | null): string {
    if (!item?.landingMessage && !item?.llmMessage) return '—';
    const block = parseReferenceComplianceBlock((item.llmMessage || item.landingMessage || '').trim());
    const raw = `${block.status ?? ''} ${item.agreement?.landingStatus ?? ''} ${item.agreement?.llmStatus ?? ''}`.toLowerCase();
    if (/\bnon[- ]?compliant\b/.test(raw) || /\bnon\b/.test(raw) && /compliant/.test(raw)) return 'Non-compliance';
    if (/\bpartial\b/.test(raw)) return 'Partial compliance';
    if (/\bcompliant\b/.test(raw)) return 'Compliance';
    if (item.agreement?.status === 'aligned') return 'Compliance';
    return 'Partial compliance';
  }

  get policyPdfDocId(): string | null {
    if (this.selectedComplianceIds.size > 0) {
      return [...this.selectedComplianceIds][0];
    }
    return this.complianceDoc?.id ?? this.seededImptfs?.id ?? null;
  }

  get regulationPdfDocId(): string | null {
    return this.selectedRegDocs[0]?.id ?? this.libraryPrimaryRegDocId ?? null;
  }

  openPdfDocument(docId: string | null, page?: string | null): void {
    if (!docId) {
      this.toast.show('PDF document not linked for this run', 'warning');
      return;
    }
    const openUrl = (url: string) => {
      const full = page ? `${url}#page=${page}` : url;
      window.open(full, '_blank', 'noopener');
    };
    this.api.getDocumentSignedUrl(docId).subscribe({
      next: (r) => {
        if (r.url) {
          openUrl(r.url);
          return;
        }
        void this.openRegulationFileUrl(docId, page);
      },
      error: () => void this.openRegulationFileUrl(docId, page),
    });
  }

  private async openRegulationFileUrl(docId: string, page?: string | null): Promise<void> {
    const res = await this.ndApi.getRegulationDocumentFileUrl(docId);
    if (res.success && res.data?.url) {
      const url = page ? `${res.data.url}#page=${page}` : res.data.url;
      window.open(url, '_blank', 'noopener');
      return;
    }
    this.toast.show(res.message ?? 'Could not open PDF', 'error');
  }

  runDemoAnalysis(): void {
    if (this.isSessionActive || this.analysisState === 'running') return;
    if (!this.govPoints.length) {
      this.toast.show('Load regulation points first', 'warning');
      return;
    }

    this.stopDemoRun();
    this.isDemoRun = true;
    this.demoNdRunId = null;
    this.ndRunId = null;
    this.ndRunWorkflowStatus = '';
    this.analysisCompleteUiDone = false;
    this.showInlineGapReport = false;
    this.inlineGapItems = [];
    this.stopPolling();
    this.sessionId = `demo:${SEEDED_DEMO_COMPLIANCE_SESSION}`;
    this.analysisState = 'running';
    this.pointsCollapsed = true;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.demoResultsByPoint.clear();
    this.error = '';
    this.progress = 5;
    this.progressDone = 0;
    this.findingsPreview = [];

    const selectedIds = this.comparableSelectedIds();
    if (!selectedIds.length) {
      this.toast.show('No comparable points selected for analysis', 'warning');
      return;
    }
    this.sessionSelectedPointIds = new Set(selectedIds);
    for (const id of this.sessionSelectedPointIds) this.sessionPointStatus.set(id, 'queued');

    this.api.loadComplianceSession(SEEDED_DEMO_COMPLIANCE_SESSION).subscribe({
      next: (r) => {
        for (const row of (r.results as Record<string, unknown>[]) ?? []) {
          const item = savedResultToReportItem(
            row as Parameters<typeof savedResultToReportItem>[0],
          );
          if (item) this.demoResultsByPoint.set(item.pointId, item);
        }

        const matched = this.buildDemoQueue(this.sessionSelectedPointIds!, this.demoResultsByPoint);

        if (!matched.length) {
          this.analysisState = 'idle';
          this.isDemoRun = false;
          this.error = 'No demo results found in database. Run bundle seed first.';
          this.toast.show(this.error, 'error', 5000);
          return;
        }

        const matchedSet = new Set(matched);
        let skipped = 0;
        for (const id of this.sessionSelectedPointIds!) {
          if (!matchedSet.has(id)) {
            this.sessionPointStatus.set(id, 'skipped');
            skipped++;
          }
        }

        this.demoQueue = matched;
        this.progressTotal = this.demoQueue.length;
        if (skipped > 0) {
          this.toast.show(
            `Demo: ${matched.length} points with saved results. ${skipped} selected point${skipped === 1 ? '' : 's'} ha${skipped === 1 ? 's' : 've'} no demo data — use Run for full analysis.`,
            'warning',
            7000,
          );
        }
        this.resetSteps();
        this.markStep(0, true);
        this.toast.show('Demo run started — using saved results (no AI credits)', 'success', 2500);
        this.processNextDemoPoint(0);
      },
      error: () => {
        this.analysisState = 'idle';
        this.isDemoRun = false;
        this.error = 'Could not load demo analysis from database.';
        this.toast.show(this.error, 'error', 5000);
      },
    });
  }

  stopDemoRun(): void {
    if (this.demoTimer) {
      clearTimeout(this.demoTimer);
      this.demoTimer = null;
    }
  }

  openDemoWorkflow(): void {
    if (this.demoNdRunId) {
      void this.router.navigate(['/nd/gap-analysis'], { queryParams: { run: this.demoNdRunId } });
      return;
    }
    void this.ndApi.createDemoAnalysisFromSeed().then((res) => {
      if (res.success && res.data?.id) {
        this.demoNdRunId = res.data.id;
        this.ndRunId = res.data.id;
        void this.router.navigate(['/nd/gap-analysis'], { queryParams: { run: res.data.id } });
      } else {
        this.toast.show(res.message ?? 'Could not open workflow', 'error');
      }
    });
  }

  openNdWorkflow(): void {
    const runId = this.activeNdRunId;
    if (runId) {
      void this.router.navigate(['/nd/gap-analysis'], { queryParams: { run: runId } });
      return;
    }
    this.openDemoWorkflow();
  }

  /** Persist demo gap analysis run to ND database with actual session results. */
  protected persistDemoNdRun(): Promise<void> {
    this.demoSaveInProgress = true;
    this.demoSaveError = '';
    const pointIds = this.demoQueue.length
      ? [...this.demoQueue]
      : [...this.sessionPointResults.keys()];

    const points = pointIds
      .map((pointId) => {
        const session = this.sessionPointResults.get(pointId);
        const item = this.resolveDemoResult(pointId);
        const gov = this.govPoints.find((g) => g.point_id === pointId);
        const landingMessage = session?.landingMessage ?? item?.landingMessage ?? null;
        const llmMessage = session?.llmMessage ?? item?.llmMessage ?? null;
        return {
          pointId,
          title: gov?.title ?? session?.pointTitle ?? item?.pointTitle ?? null,
          text: gov?.text ?? null,
          landingMessage,
          llmMessage,
          agreementJson: session?.agreementJson ?? item?.agreement ?? null,
        };
      })
      .filter((p) => p.landingMessage || p.llmMessage);

    if (!points.length) {
      this.toast.show('No demo point results to save — run bundle seed or use Run for full analysis.', 'warning', 6000);
      return Promise.resolve();
    }

    const selectedSnapshot = this.govPoints
      .filter((p) => this.selected.has(p.point_id))
      .map((p) => ({
        pointNumber: p.point_id,
        pointTitle: p.title ?? null,
        pointContent: p.text,
        pageReference: p.section ?? null,
      }));

    const regIds = this.selectedRegDocs.map((d) => d.id).filter(Boolean) as string[];
    if (!regIds.length && this.libraryPrimaryRegDocId) regIds.push(this.libraryPrimaryRegDocId);
    const intIds =
      this.selectedComplianceIds.size > 0
        ? [...this.selectedComplianceIds]
        : this.complianceDoc?.id
          ? [this.complianceDoc.id]
          : [];

    const complianceLabel = (this.complianceFileName || this.complianceDoc?.originalFileName || 'Compliance').slice(0, 48);
    const regLabel = this.selectedRegLabel.slice(0, 120);
    const name = `[Demo] ${complianceLabel} × ${regLabel}`.slice(0, 240);

    return this.ndApi
      .saveDemoAnalysisRun({
        name,
        selectedPointsSnapshot: selectedSnapshot,
        selectedInternalDocIds: intIds,
        selectedRegulationDocIds: regIds,
        points,
      })
      .then((res) => {
        this.demoSaveInProgress = false;
        if (res.success && res.data?.id) {
          this.demoNdRunId = res.data.id;
          this.ndRunId = res.data.id;
          this.ndRunWorkflowStatus = res.data.status ?? 'completed';
          void this.refreshNdRunWorkflowStatus();
          this.onNdRunSaved(res.data.id);
          this.toast.show(
            `Saved to analysis runs — ${res.data.pointCount ?? points.length} points. Open All analysis runs to view.`,
            'success',
            5000,
          );
        } else {
          const msg =
            res.message ??
            'Could not save demo run. Sign in as maker or super admin and ensure the API is running.';
          this.demoSaveError = msg;
          this.toast.show(`Demo finished but not saved: ${msg}`, 'error', 8000);
          console.error('demo-save failed', res);
        }
      })
      .catch((err: unknown) => {
        this.demoSaveInProgress = false;
        const msg = err instanceof Error ? err.message : 'Network error saving demo run';
        this.demoSaveError = msg;
        this.toast.show(`Demo finished but not saved: ${msg}`, 'error', 8000);
        console.error('demo-save error', err);
      });
  }

  /** Match selected regulation points to seeded demo results; queue uses gov point ids for UI consistency. */
  private buildDemoQueue(
    selectedIds: Set<string>,
    resultsByPoint: Map<string, DualVerifyReportItem>,
  ): string[] {
    const queue: string[] = [];
    for (const id of selectedIds) {
      if (this.resolveDemoResult(id, resultsByPoint)) queue.push(id);
    }
    return queue.sort((a, b) => this.govPointSortKey(a).localeCompare(this.govPointSortKey(b), undefined, { numeric: true }));
  }

  private govPointSortKey(pointId: string): string {
    const gov = this.govPoints.find((g) => g.point_id === pointId);
    return gov?.section?.trim() || pointId;
  }

  private resolveDemoResult(
    pointId: string,
    resultsByPoint: Map<string, DualVerifyReportItem> = this.demoResultsByPoint,
  ): DualVerifyReportItem | undefined {
    const direct = resultsByPoint.get(pointId);
    if (direct) return direct;

    const gov = this.govPoints.find((g) => g.point_id === pointId);
    const section = gov?.section?.trim();
    if (section) {
      const bySection = resultsByPoint.get(section);
      if (bySection) return bySection;
      const normSection = this.normalizePointId(section);
      for (const [key, item] of resultsByPoint) {
        if (this.normalizePointId(key) === normSection) return item;
      }
    }

    const norm = this.normalizePointId(pointId);
    for (const [key, item] of resultsByPoint) {
      if (this.normalizePointId(key) === norm) return item;
    }
    return undefined;
  }

  private normalizePointId(id: string): string {
    return id.replace(/^§\s*/, '').trim().toLowerCase();
  }

  private processNextDemoPoint(index: number): void {
    if (index >= this.demoQueue.length) {
      this.finishDemoRun();
      return;
    }

    const pointId = this.demoQueue[index];
    this.sessionPointStatus.set(pointId, 'running');
    this.selectedDetailPointId ??= pointId;

    this.demoTimer = setTimeout(() => {
      const item = this.resolveDemoResult(pointId);
      if (item) {
        const sessionPoint: SessionPoint = {
          id: pointId,
          pointId,
          pointTitle: item.pointTitle,
          status: 'completed',
          landingMessage: item.landingMessage,
          llmMessage: item.llmMessage,
          agreementJson: item.agreement,
        };
        this.sessionPointResults.set(pointId, sessionPoint);
        this.sessionPointStatus.set(pointId, 'completed');
      } else {
        this.sessionPointStatus.set(pointId, 'failed');
      }

      this.progressDone = index + 1;
      this.progress = Math.round((this.progressDone / this.progressTotal) * 100);
      this.processNextDemoPoint(index + 1);
    }, DEMO_POINT_DELAY_MS);
  }

  private finishDemoRun(): void {
    this.stopDemoRun();
    this.analysisState = 'complete';
    this.progress = 100;
    this.analysisSteps.forEach((s) => {
      s.done = true;
      s.active = false;
    });

    const points = [...this.sessionPointResults.values()];
    this.findingsPreview = points.slice(0, 8).map((p) => ({
      severity: this.severityFromPoint(p.agreementJson),
      title: p.pointTitle || p.pointId,
      section: `§${p.pointId}`,
      pointId: p.pointId,
    }));

    void this.persistDemoNdRun().finally(() => {
      this.isDemoRun = false;
      this.onAnalysisComplete();
      if (this.activeNdRunId) {
        this.toast.show('Demo analysis complete — review gap report below', 'success', 4000);
      }
    });
  }

  get selectedRegLabel(): string {
    if (this.useLibraryPoints && this.librarySourceLabel) return this.librarySourceLabel;
    if (!this.selectedRegDocs.length) return 'selected regulation';
    return this.selectedRegDocs.map((d) => d.title || d.originalFileName).join(', ');
  }

  get catalogCards(): RegCard[] {
    if (!this.regulationDocs.length) {
      return this.regCards.map((c) => ({ ...c }));
    }
    return this.regulationDocs.map((d) => {
      const preset = this.regCards.find((c) => this.cardMatchesDoc(c, d));
      return {
        id: preset?.id ?? `doc:${d.id}`,
        title: d.title || d.originalFileName || preset?.title || 'Regulation',
        source: preset?.source ?? `${d.version} · ${d.uploaded}`,
        clauses: d.pointCount ?? preset?.clauses ?? 0,
        type: d.category || preset?.type || 'Regulation',
        documentId: d.id,
        matchHash: preset?.matchHash ?? d.fileHash ?? undefined,
        matchTitle: preset?.matchTitle,
      };
    });
  }

  get filteredCatalogCards(): RegCard[] {
    const q = this.regSearch.trim().toLowerCase();
    if (!q) return this.catalogCards;
    return this.catalogCards.filter(
      (c) =>
        c.title.toLowerCase().includes(q) ||
        c.source.toLowerCase().includes(q) ||
        c.type.toLowerCase().includes(q),
    );
  }

  get filteredComplianceDocs(): StoredDocumentDto[] {
    const q = this.complianceSearch.trim().toLowerCase();
    if (!q) return this.complianceDocs;
    return this.complianceDocs.filter((d) => {
      const label = `${d.title} ${d.originalFileName} ${d.category} ${d.version}`.toLowerCase();
      return label.includes(q);
    });
  }

  get filteredRegulationDocs(): StoredDocumentDto[] {
    const q = this.regSearch.trim().toLowerCase();
    if (!q) return this.regulationDocs;
    return this.regulationDocs.filter((d) => {
      const label = `${d.title} ${d.originalFileName} ${d.category}${d.isNdManual ? ' custom manual' : ''}`.toLowerCase();
      return label.includes(q);
    });
  }

  get dropdownSummary(): string {
    const n = this.selectedRegIds.size;
    if (!n) return 'Select regulation files…';
    if (n === 1) {
      const d = this.selectedRegDocs[0];
      return d ? this.fileLabel(d) : '1 file selected';
    }
    return `${n} files selected`;
  }

  get visibleChapterGroups(): GovPointChapterGroup[] {
    const q = this.govSearch.trim().toLowerCase();
    if (!q) return this.chapterGroups;
    return this.chapterGroups
      .map((ch) => ({
        ...ch,
        sections: ch.sections
          .map((sec) => ({
            ...sec,
            points: sec.points.filter(
              (p) =>
                p.point_id.toLowerCase().includes(q) ||
                (p.title ?? '').toLowerCase().includes(q) ||
                (p.text ?? '').toLowerCase().includes(q),
            ),
          }))
          .filter((sec) => sec.points.length > 0),
        points: ch.points.filter(
          (p) =>
            p.point_id.toLowerCase().includes(q) ||
            (p.title ?? '').toLowerCase().includes(q) ||
            (p.text ?? '').toLowerCase().includes(q),
        ),
      }))
      .filter((ch) => ch.sections.length > 0);
  }

  get visiblePointDisplayChapters(): LibraryPointDisplayChapter[] {
    const q = this.govSearch.trim().toLowerCase();
    if (!q) return this.pointDisplayChapters;
    return this.pointDisplayChapters
      .map((ch) => ({
        ...ch,
        sections: ch.sections
          .map((sec) => ({
            ...sec,
            rows: sec.rows.filter((row) => this.displayRowMatchesSearch(row, q)),
          }))
          .filter((sec) => sec.rows.length > 0),
      }))
      .filter((ch) => ch.sections.length > 0)
      .map((ch) => ({
        ...ch,
        storedCount: ch.sections.reduce((n, s) => n + s.rows.length, 0),
        analyseCount: ch.sections.reduce(
          (n, s) => n + s.rows.filter((r) => r.forAnalysis).length,
          0,
        ),
      }));
  }

  displayRowMatchesSearch(row: LibraryPointDisplayRow, q: string): boolean {
    const p = row.point;
    return (
      row.displayId.toLowerCase().includes(q) ||
      (p.title ?? '').toLowerCase().includes(q) ||
      p.text.toLowerCase().includes(q)
    );
  }

  displayChapterAnalyseRows(ch: LibraryPointDisplayChapter): LibraryPointDisplayRow[] {
    return ch.sections.flatMap((sec) => sec.rows.filter((r) => r.forAnalysis));
  }

  displaySectionAnalyseRows(sec: LibraryPointDisplayChapter['sections'][number]): LibraryPointDisplayRow[] {
    return sec.rows.filter((r) => r.forAnalysis);
  }

  displayChapterAllSelected(ch: LibraryPointDisplayChapter): boolean {
    const rows = this.displayChapterAnalyseRows(ch);
    return rows.length > 0 && rows.every((r) => this.selected.has(r.point.point_id));
  }

  displayChapterSelectedCount(ch: LibraryPointDisplayChapter): number {
    return this.displayChapterAnalyseRows(ch).filter((r) =>
      this.selected.has(r.point.point_id),
    ).length;
  }

  displayDocSelectedCount(doc: LibraryPointDisplayDoc): number {
    if (doc.useChapters) {
      return doc.chapters.reduce((n, ch) => n + this.displayChapterSelectedCount(ch), 0);
    }
    return doc.flatRows
      .filter((r) => r.forAnalysis && this.selected.has(r.point.point_id))
      .length;
  }

  togglePointIds(ids: string[]): void {
    if (!ids.length) return;
    const all = ids.every((id) => this.selected.has(id));
    for (const id of ids) {
      if (all) this.selected.delete(id);
      else this.selected.add(id);
    }
  }

  displaySectionAllSelected(sec: LibraryPointDisplayChapter['sections'][number]): boolean {
    const rows = this.displaySectionAnalyseRows(sec);
    return rows.length > 0 && rows.every((r) => this.selected.has(r.point.point_id));
  }

  toggleDisplayChapterSelection(ch: LibraryPointDisplayChapter): void {
    const rows = this.displayChapterAnalyseRows(ch);
    const all = this.displayChapterAllSelected(ch);
    for (const row of rows) {
      if (all) this.selected.delete(row.point.point_id);
      else this.selected.add(row.point.point_id);
    }
  }

  toggleDisplaySectionSelection(sec: LibraryPointDisplayChapter['sections'][number]): void {
    const rows = this.displaySectionAnalyseRows(sec);
    const all = this.displaySectionAllSelected(sec);
    for (const row of rows) {
      if (all) this.selected.delete(row.point.point_id);
      else this.selected.add(row.point.point_id);
    }
  }

  regChapterKey(docKey: string, chapter: string): string {
    return `${docKey}:${chapter}`;
  }

  toggleRegDoc(docKey: string): void {
    const next = new Set(this.expandedRegDocKeys);
    if (next.has(docKey)) next.delete(docKey);
    else {
      next.add(docKey);
      const doc = this.regulationDisplayDocs.find((d) => d.key === docKey);
      if (doc?.useChapters) {
        const chapterKeys = new Set(this.expandedRegChapterKeys);
        for (const ch of doc.chapters) {
          chapterKeys.add(this.regChapterKey(docKey, ch.chapter));
        }
        this.expandedRegChapterKeys = chapterKeys;
      }
    }
    this.expandedRegDocKeys = next;
  }

  isRegDocExpanded(docKey: string): boolean {
    return this.expandedRegDocKeys.has(docKey);
  }

  toggleRegChapter(docKey: string, chapter: string): void {
    const key = this.regChapterKey(docKey, chapter);
    const next = new Set(this.expandedRegChapterKeys);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedRegChapterKeys = next;
  }

  isRegChapterExpanded(docKey: string, chapter: string): boolean {
    return this.expandedRegChapterKeys.has(this.regChapterKey(docKey, chapter));
  }

  showRegSectionBar(
    sections: { key: string }[],
    key: string,
    chapter: string,
  ): boolean {
    return sections.length > 1 || key !== chapter;
  }

  get visibleRegulationDisplayDocs(): LibraryPointDisplayDoc[] {
    const q = this.govSearch.trim().toLowerCase();
    if (!q) return this.regulationDisplayDocs;
    return this.regulationDisplayDocs
      .map((doc) => {
        if (doc.useChapters) {
          const chapters = doc.chapters
            .map((ch) => ({
              ...ch,
              sections: ch.sections
                .map((sec) => ({
                  ...sec,
                  rows: sec.rows.filter((row) => this.displayRowMatchesSearch(row, q)),
                }))
                .filter((sec) => sec.rows.length > 0),
            }))
            .filter((ch) => ch.sections.length > 0)
            .map((ch) => ({
              ...ch,
              storedCount: ch.sections.reduce((n, s) => n + s.rows.length, 0),
              analyseCount: ch.sections.reduce(
                (n, s) => n + s.rows.filter((r) => r.forAnalysis).length,
                0,
              ),
            }));
          const storedCount = chapters.reduce((n, ch) => n + ch.storedCount, 0);
          const analyseCount = chapters.reduce((n, ch) => n + ch.analyseCount, 0);
          return { ...doc, chapters, flatRows: [], storedCount, analyseCount };
        }
        const flatRows = doc.flatRows.filter((row) => this.displayRowMatchesSearch(row, q));
        return {
          ...doc,
          flatRows,
          chapters: [],
          storedCount: flatRows.length,
          analyseCount: flatRows.filter((r) => r.forAnalysis).length,
        };
      })
      .filter((doc) => doc.storedCount > 0);
  }

  get selectedCount(): number {
    if (this.govPoints.length) return this.comparableSelectedIds().length;
    if (this.sessionId && this.sessionSelectedPointIds?.size) {
      return this.sessionSelectedPointIds.size;
    }
    return this.selected.size;
  }

  /** Selected point ids that exist in the current comparable govPoints list (excludes section headers). */
  comparableSelectedIds(): string[] {
    if (!this.govPoints.length) return [...this.selected];
    const govIds = new Set(this.govPoints.map((p) => p.point_id));
    return [...this.selected].filter((id) => govIds.has(id));
  }

  /** ND run id for gap report / workflow (demo or live ND run). */
  get activeNdRunId(): string | null {
    return this.demoNdRunId ?? this.ndRunId;
  }

  protected syncSelectionToGovPoints(): void {
    if (!this.govPoints.length) return;
    const govIds = new Set(this.govPoints.map((p) => p.point_id));
    for (const id of [...this.selected]) {
      if (!govIds.has(id)) this.selected.delete(id);
    }
    if (this.sessionSelectedPointIds) {
      this.sessionSelectedPointIds = new Set(
        [...this.sessionSelectedPointIds].filter((id) => govIds.has(id)),
      );
    }
    if (this.progressTotal > this.govPoints.length) {
      this.progressTotal = this.sessionSelectedPointIds?.size ?? this.comparableSelectedIds().length;
    }
  }

  get canRun(): boolean {
    return (
      !!this.complianceFile &&
      this.selected.size > 0 &&
      this.govPoints.length > 0 &&
      this.analysisState !== 'running' &&
      !this.loadingPoints &&
      !this.uploadingReg &&
      !this.uploadingCompliance &&
      !this.attachingCompliance &&
      !this.loadingCompliance
    );
  }

  get runBlockedReason(): string | null {
    if (this.uploadingCompliance || this.attachingCompliance) {
      return 'Wait for the compliance document to finish attaching.';
    }
    if (!this.complianceFile) {
      return 'Select or upload a compliance document.';
    }
    if (!this.useLibraryPoints && !this.selectedRegDocs.length && !this.govPoints.length) {
      return 'Select or upload at least one regulation document.';
    }
    if (!this.govPoints.length) {
      return this.useLibraryPoints
        ? 'Select a regulation points library to load regulation points.'
        : 'Select regulation file(s) to load regulation points.';
    }
    if (!this.selected.size) return 'Select at least one regulation point.';
    return null;
  }

  get logText(): string {
    const lines = [
      `✓ Compliance doc: ${this.complianceFileSize || this.complianceFileName || '—'}`,
      `✓ Regulation: ${this.selectedRegLabel} (${this.govPoints.length} clauses)`,
    ];
    if (this.selectedRegDocs.length) {
      for (const d of this.selectedRegDocs) {
        lines.push(`  · ${d.originalFileName || d.title}`);
      }
    }
    lines.push('-- Initial findings --', 'Cross-referencing requirements…');
    return lines.join('\n');
  }

  get briefingText(): string {
    return `**BRIEFING:**
Analysis of ${this.complianceFileName || 'compliance document'} against ${this.selectedRegLabel}.

**Manual Scope:** ${this.complianceFileSize || '—'} · UAE/DIFC Branch

**Key Compliance Gaps:**
${this.findingsPreview
  .slice(0, 4)
  .map((f, i) => `${i + 1}. ${f.title} (${f.section})`)
  .join('\n') || '1. Review dual-verify report for detailed gap findings'}`;
  }

  selectPointForDetail(pointId: string): void {
    this.selectedDetailPointId = pointId;
  }

  isPointSolved(
    agreement?: { status?: string; label?: string; summary?: string } | null,
    status?: string,
  ): boolean {
    if (status && status !== 'completed') return false;
    const v = `${agreement?.status ?? ''} ${agreement?.label ?? ''}`.toLowerCase();
    if (!v.trim()) return false;
    return (
      v.includes('compliant') ||
      v.includes('aligned') ||
      v.includes('agree') ||
      v.includes('met') ||
      v.includes('satisfied')
    );
  }

  pointStatusClass(status: string): string {
    return status;
  }

  checkStorage(): void {
    this.api.getDocumentsStorageHealth().subscribe({
      next: (r) => {
        this.storageConfigured = !!r.storageConfigured;
      },
      error: () => {
        this.storageConfigured = false;
      },
    });
  }

  refreshRegulations(after?: () => void): void {
    if (this.useNdRegulationCatalog) {
      this.loadingRegs = true;
      void this.loadNdRegulationCatalog()
        .then((docs) => {
          this.regulationDocs = docs;
          this.syncHighlightedCards();
          this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
          after?.();
        })
        .finally(() => {
          this.loadingRegs = false;
        });
      return;
    }

    this.loadingRegs = true;
    this.api.listStoredDocuments('regulation', this.workspaceId).subscribe({
      next: (r) => {
        this.loadingRegs = false;
        this.regulationDocs = r.data ?? [];
        this.syncHighlightedCards();
        this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
        after?.();
      },
      error: () => {
        this.loadingRegs = false;
        this.regulationDocs = [];
        after?.();
      },
    });
  }

  private async loadNdRegulationCatalog(): Promise<StoredDocumentDto[]> {
    const res = await this.ndApi.getRegulationDocuments();
    if (!res.success || !res.data) return [];

    const deduped = sortRegulationDocuments(dedupeRegulationDocuments(res.data as RegulationDocument[]));
    return deduped.map((nd) => this.ndRegulationToStoredDoc(nd));
  }

  private ndRegulationToStoredDoc(nd: RegulationDocument): StoredDocumentDto {
    return {
      id: nd.id,
      title: nd.name,
      category: 'Regulation',
      pages: 0,
      uploaded: nd.createdAt,
      version: nd.isManual || nd.source === 'manual' ? 'Manual' : 'v1',
      status: nd.extractionStatus === 'extracted' || nd.extractionStatus === 'manual' ? 'active' : 'pending',
      filter: 'regulation',
      fileType: nd.isManual || nd.source === 'manual' ? 'manual' : 'pdf',
      docKind: 'regulation',
      storagePath: '',
      history: [],
      originalFileName: nd.name,
      sizeBytes: 0,
      pointCount: nd.pointCount ?? 0,
      isNdManual: nd.isManual === true || nd.source === 'manual',
      ndStoredDocumentId: nd.storedDocumentId ?? null,
    };
  }

  private async fetchNdRegulationPoints(id: string): Promise<{
    success: boolean;
    points: GovPoint[];
    message?: string;
    document?: StoredDocumentDto;
  }> {
    const doc = this.regulationDocs.find((d) => d.id === id);
    const res = await this.ndApi.getDocumentPoints(id);
    const raw = (res.success && res.data ? res.data : []) as Record<string, unknown>[];
    const points = raw
      .map(normalizeRegulationPoint)
      .filter((p) => p.pointNumber)
      .map((p) =>
        regulationPointToNdGovPoint(p, {
          docId: id,
          docName: doc?.title ?? 'Regulation document',
          isManual: doc?.isNdManual === true,
        }),
      );

    if (doc) {
      doc.pointCount = points.length;
      const analysed = analyzeGovPointSet(
        points.map((p) => ({
          point_id: p.pointNumber,
          title: p.title,
          text: p.text,
          section: p.section,
        })),
        { docName: doc.title },
      );
      this.regComparableCounts.set(id, analysed.analyseCount);
    }

    return {
      success: !!res.success,
      points,
      message: res.message,
      document: doc,
    };
  }

  private autoSelectTfs(): void {
    if (this.route.snapshot.queryParamMap.get('session') || this.sessionId) return;
    const tfsDoc =
      this.regulationDocs.find(
        (d) => (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase(),
      ) ??
      this.regulationDocs.find((d) => d.ndStoredDocumentId && (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase()) ??
      this.regulationDocs.find((d) => /tfs guidelines/i.test(`${d.title} ${d.originalFileName}`));
    if (tfsDoc) {
      if (!this.selectedRegIds.has(tfsDoc.id)) {
        this.selectedRegIds.add(tfsDoc.id);
        this.syncSelectedDocs();
        this.loadPointsForSelectedFiles();
      }
      return;
    }
    this.seedAndSelectTfs();
  }

  refreshComplianceDocs(after?: () => void): void {
    this.loadingCompliance = true;
    this.api.listStoredDocuments('document', this.workspaceId).subscribe({
      next: (r) => {
        this.loadingCompliance = false;
        this.complianceDocs = r.data ?? [];
        this.seededImptfs =
          this.complianceDocs.find(
            (d) => (d.fileHash ?? '').toLowerCase() === this.IMPTFS_HASH.toLowerCase(),
          ) ??
          this.complianceDocs.find((d) =>
            /i\s*m\s*p\s*t\s*f\s*s|imptfs/i.test(`${d.title} ${d.originalFileName}`),
          ) ??
          null;
        if (
          this.selectedComplianceDocId &&
          !this.complianceDocs.some((d) => d.id === this.selectedComplianceDocId)
        ) {
          this.selectedComplianceDocId = null;
        }
        for (const id of [...this.selectedComplianceIds]) {
          if (!this.complianceDocs.some((d) => d.id === id)) {
            this.selectedComplianceIds.delete(id);
          }
        }
        after?.();
      },
      error: () => {
        this.loadingCompliance = false;
        this.complianceDocs = [];
        this.seededImptfs = null;
        after?.();
      },
    });
  }

  private autoSelectImptfs(): void {
    if (this.complianceFile || !this.seededImptfs) return;
    this.selectComplianceDoc(this.seededImptfs, { silent: true });
  }

  selectComplianceDoc(
    doc: StoredDocumentDto,
    opts?: { silent?: boolean; preserveAnalysisState?: boolean },
  ): void {
    if (this.attachingCompliance) return;
    this.selectedComplianceDocId = doc.id;
    this.selectedComplianceIds.add(doc.id);
    this.compliancePanelMode = 'uploaded';
    this.attachComplianceDoc(doc, opts);
  }

  toggleComplianceFile(doc: StoredDocumentDto, event?: Event): void {
    event?.stopPropagation();
    if (this.selectedComplianceIds.has(doc.id)) {
      this.selectedComplianceIds.delete(doc.id);
      if (this.selectedComplianceDocId === doc.id) {
        const nextId = [...this.selectedComplianceIds][0];
        const nextDoc = nextId ? this.complianceDocs.find((d) => d.id === nextId) : null;
        if (nextDoc) {
          this.selectComplianceDoc(nextDoc, { silent: true });
        } else {
          this.removeCompliance();
        }
      }
      return;
    }
    const hadOthers = this.selectedComplianceIds.size > 0;
    this.selectComplianceDoc(doc, { silent: hadOthers });
  }

  isComplianceFileSelected(id: string): boolean {
    return this.selectedComplianceIds.has(id);
  }

  selectAllFilteredCompliance(): void {
    for (const doc of this.filteredComplianceDocs) {
      this.selectedComplianceIds.add(doc.id);
    }
    const last = this.filteredComplianceDocs[this.filteredComplianceDocs.length - 1];
    if (last) this.selectComplianceDoc(last, { silent: true });
  }

  clearComplianceSelection(): void {
    this.selectedComplianceIds.clear();
    this.removeCompliance();
  }

  private attachComplianceDoc(
    doc: StoredDocumentDto,
    opts?: { silent?: boolean; preserveAnalysisState?: boolean },
  ): void {
    this.attachingCompliance = true;
    this.error = '';
    this.api.getDocumentSignedUrl(doc.id).subscribe({
      next: async (r) => {
        try {
          if (!r.url) throw new Error('No download URL');
          const res = await fetch(r.url);
          if (!res.ok) throw new Error(`Download failed (${res.status})`);
          const blob = await res.blob();
          const name = doc.originalFileName || doc.title || 'compliance.pdf';
          const file = new File([blob], name, { type: blob.type || 'application/pdf' });
          this.complianceFile = file;
          this.complianceFileName = name;
          this.complianceFileSize = `${Math.round(file.size / 1024)} KB`;
          this.complianceDoc = doc;
          if (!opts?.preserveAnalysisState) {
            this.analysisState = 'idle';
          }
          if (!opts?.silent) {
            this.toast.show(`Attached ${name}`, 'success', 2200);
          }
        } catch (e) {
          this.selectedComplianceDocId = null;
          this.toast.show(
            e instanceof Error ? e.message : 'Could not attach compliance document',
            'error',
            4000,
          );
        } finally {
          this.attachingCompliance = false;
        }
      },
      error: () => {
        this.attachingCompliance = false;
        this.selectedComplianceDocId = null;
        this.toast.show('Could not get compliance download link', 'error');
      },
    });
  }

  complianceMeta(doc: StoredDocumentDto): string {
    const size = doc.sizeBytes > 0 ? `${Math.round(doc.sizeBytes / 1024)} KB` : '';
    return [doc.uploaded, size].filter(Boolean).join(' · ');
  }

  setRegViewMode(mode: RegViewMode): void {
    this.regViewMode = mode;
  }

  toggleRegDropdown(event?: Event): void {
    event?.stopPropagation();
    this.regDropdownOpen = !this.regDropdownOpen;
    if (this.regDropdownOpen) this.refreshRegulations();
  }

  closeRegDropdown(): void {
    this.regDropdownOpen = false;
  }

  selectRegCard(card: RegCard): void {
    if (card.documentId) {
      this.toggleRegFileById(card.documentId);
      return;
    }
    const matches = this.regulationDocs.filter((d) => this.cardMatchesDoc(card, d));
    if (!matches.length) {
      if (card.id === 'tfs') {
        this.toast.show('Linking TFS Guidelines from seed…', 'warning', 2500);
        this.seedAndSelectTfs();
        return;
      }
      this.toast.show(
        `No uploaded file matching “${card.title}” yet — use Upload.`,
        'warning',
        3500,
      );
      return;
    }
    const allSelected = matches.every((d) => this.selectedRegIds.has(d.id));
    for (const doc of matches) {
      if (allSelected) this.selectedRegIds.delete(doc.id);
      else this.selectedRegIds.add(doc.id);
    }
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  isCardSelected(card: RegCard): boolean {
    if (card.documentId) return this.selectedRegIds.has(card.documentId);
    return this.highlightedCardIds.has(card.id);
  }

  clearRegSelection(): void {
    this.selectedRegIds.clear();
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  selectAllFilteredRegs(): void {
    for (const d of this.filteredRegulationDocs) this.selectedRegIds.add(d.id);
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  toggleRegFile(doc: StoredDocumentDto, event?: Event): void {
    event?.stopPropagation();
    this.toggleRegFileById(doc.id);
  }

  toggleRegFileById(id: string): void {
    if (this.selectedRegIds.has(id)) this.selectedRegIds.delete(id);
    else this.selectedRegIds.add(id);
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  isRegFileSelected(id: string): boolean {
    return this.selectedRegIds.has(id);
  }

  private syncSelectedDocs(): void {
    this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
    this.syncHighlightedCards();
  }

  private syncHighlightedCards(): void {
    this.highlightedCardIds.clear();
    for (const card of this.regCards) {
      if (this.selectedRegDocs.some((d) => this.cardMatchesDoc(card, d))) {
        this.highlightedCardIds.add(card.id);
      }
    }
  }

  private cardMatchesDoc(card: RegCard, d: StoredDocumentDto): boolean {
    if (card.matchHash && (d.fileHash ?? '').toLowerCase() === card.matchHash.toLowerCase()) return true;
    const name = `${d.originalFileName} ${d.title}`;
    return !!card.matchTitle?.test(name);
  }

  private seedAndSelectTfs(): void {
    this.uploadingReg = true;
    this.api.seedTfsGuidelines().subscribe({
      next: (r) => {
        this.uploadingReg = false;
        this.refreshRegulations();
        if (r.document) {
          this.selectedRegIds.add(r.document.id);
          this.syncSelectedDocs();
          this.loadPointsForSelectedFiles();
        }
        this.toast.show(r.message ?? 'TFS linked', 'success', 3000);
      },
      error: (e: HttpErrorResponse) => {
        this.uploadingReg = false;
        this.error = e.error?.message ?? 'Could not seed TFS Guidelines.';
      },
    });
  }

  loadPointsForSelectedFiles(
    onLoaded?: () => void,
    opts?: { silent?: boolean; selectedIds?: Iterable<string> },
  ): void {
    const ids = [...this.selectedRegIds];
    if (!ids.length) {
      this.rawGovPoints = [];
      this.govPoints = [];
      this.chapterGroups = [];
      this.selected.clear();
      this.govSourceLabel = '';
      this.selectedDetailPointId = null;
      onLoaded?.();
      return;
    }

    const loadGen = ++this.pointsLoadGen;
    const selectionOverride =
      opts?.selectedIds != null
        ? new Set(opts.selectedIds)
        : this.sessionSelectedPointIds;

    this.loadingPoints = true;
    this.error = '';
    this.pointsCollapsed = false;
    forkJoin(
      ids.map((id) => {
        if (this.useNdRegulationCatalog) {
          return from(this.fetchNdRegulationPoints(id)).pipe(
            catchError((e: HttpErrorResponse) =>
              of({
                success: false as const,
                points: [] as GovPoint[],
                message: e.error?.message ?? 'load failed',
                document: this.regulationDocs.find((d) => d.id === id),
              }),
            ),
          );
        }
        const doc = this.regulationDocs.find((d) => d.id === id);
        if (doc?.isNdManual) {
          return from(this.fetchNdRegulationPoints(id)).pipe(
            catchError((e: HttpErrorResponse) =>
              of({
                success: false as const,
                points: [] as GovPoint[],
                message: e.error?.message ?? 'load failed',
                document: doc,
              }),
            ),
          );
        }
        return this.api.loadDocumentPoints(id).pipe(
          catchError((e: HttpErrorResponse) =>
            of({
              success: false as const,
              points: [] as GovPoint[],
              message: e.error?.message ?? 'load failed',
              document: undefined as StoredDocumentDto | undefined,
            }),
          ),
        );
      }),
    )
      .pipe(finalize(() => {
        if (loadGen === this.pointsLoadGen) this.loadingPoints = false;
      }))
      .subscribe((results) => {
        if (loadGen !== this.pointsLoadGen) return;
        const byId = new Map<string, GovPoint>();
        const labels: string[] = [];
        const regulationDisplayDocs: LibraryPointDisplayDoc[] = [];
        let useNdDocGroups = false;

        for (const r of results) {
          if (r.document) {
            const idx = this.regulationDocs.findIndex((d) => d.id === r.document!.id);
            if (idx >= 0) {
              const updated = { ...r.document };
              if (r.points?.length) updated.pointCount = r.points.length;
              this.regulationDocs[idx] = updated;
            }
            if (r.points?.length) {
              const ndPoints = r.points as NdGovPoint[];
              const analysed = analyzeGovPointSet(
                ndPoints.map((p) => ({
                  point_id: p.pointNumber ?? p.point_id,
                  title: p.title,
                  text: p.text,
                  section: p.section,
                })),
                { docName: r.document.title },
              );
              this.regComparableCounts.set(r.document.id, analysed.analyseCount);
            }
          }

          const doc = r.document;
          const docPoints = (r.points ?? []) as NdGovPoint[];
          for (const p of docPoints) {
            byId.set(p.point_id, p);
          }
          if (r.message) labels.push(r.message);

          if (this.useNdRegulationCatalog && doc && docPoints.length) {
            useNdDocGroups = true;
            const grouping = docPoints.map((p) => ({
              point_id: p.pointNumber,
              title: p.title,
              text: p.text,
              section: p.section,
            }));
            const analyzed = analyzeGovPointSet(grouping, {
              docName: doc.isNdManual ? doc.title : doc.title,
            });
            const comparableNums = new Set(analyzed.comparable.map((p) => p.point_id));
            const comparableIds = new Set(
              docPoints
                .filter((p) => comparableNums.has(p.pointNumber))
                .map((p) => p.point_id),
            );
            const display = buildRegulationDocPointDisplay(
              docPoints,
              doc.isNdManual === true,
              comparableIds,
            );
            regulationDisplayDocs.push({
              key: doc.id,
              docId: doc.id,
              docName: doc.title || doc.originalFileName || 'Regulation document',
              ...display,
            });
          }
        }
        this.syncSelectedDocs();
        const merged = [...byId.values()];
        const fileNames = this.selectedRegDocs.map((d) => d.title || d.originalFileName).join(', ');
        this.applyGovPoints(
          merged,
          fileNames ? `Regulation: ${fileNames}` : labels.join(' · ') || `${merged.length} points from selected files`,
          selectionOverride,
          useNdDocGroups ? regulationDisplayDocs : undefined,
        );
        if (!merged.length) {
          this.error = 'No extract points for the selected file(s). Upload/extract the regulation first.';
        } else if (!opts?.silent) {
          this.toast.show(
            `Loaded ${this.govPoints.length} points for gap analysis (${this.regStoredCount} stored)`,
            'success',
            2800,
          );
        }
        onLoaded?.();
      });
  }

  onRegulationUpload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.uploadRegulationFile(file, false);
  }

  confirmRegVersionBump(): void {
    if (!this.pendingRegBump) return;
    const { file } = this.pendingRegBump;
    this.pendingRegBump = null;
    this.uploadRegulationFile(file, true);
  }

  cancelRegVersionBump(): void {
    this.pendingRegBump = null;
  }

  private uploadRegulationFile(file: File, confirmVersionBump: boolean): void {
    if (!this.storageConfigured) {
      this.error = 'Configure Supabase Storage before uploading.';
      this.toast.show(this.error, 'error', 4000);
      return;
    }
    this.uploadingReg = true;
    this.error = '';
    const form = new FormData();
    form.append('file', file);
    form.append('workspaceId', this.workspaceId);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadRegulation(form).subscribe({
      next: (r) => {
        this.uploadingReg = false;
        if (r.duplicate && r.existing) {
          this.pendingRegBump = {
            file,
            nextVersion: r.nextVersion ?? 'v2',
            title: r.existing.title,
          };
          return;
        }
        if (!r.success) {
          this.error = r.message ?? 'Regulation upload failed.';
          return;
        }
        if (r.document) {
          this.regulationDocs = [
            r.document,
            ...this.regulationDocs.filter((d) => d.id !== r.document!.id),
          ];
          this.selectedRegIds.add(r.document.id);
          this.syncSelectedDocs();
        }
        if (r.points?.length) {
          this.applyGovPoints(r.points, r.message ?? `Extracted ${r.pointCount ?? 0} points`);
        } else {
          this.loadPointsForSelectedFiles();
        }
        this.toast.show(r.message ?? 'Regulation extracted', 'success', 3500);
      },
      error: (err: HttpErrorResponse) => {
        this.uploadingReg = false;
        const body = err.error as {
          duplicate?: boolean;
          nextVersion?: string;
          existing?: StoredDocumentDto;
          message?: string;
        } | null;
        if (err.status === 409 && body?.duplicate && body.existing) {
          this.pendingRegBump = {
            file,
            nextVersion: body.nextVersion ?? 'v2',
            title: body.existing.title,
          };
          return;
        }
        this.error = body?.message ?? 'Regulation upload / extract failed.';
        this.toast.show(this.error, 'error', 5000);
      },
    });
  }

  protected applyGovPoints(
    points: GovPoint[],
    note: string,
    selectionOnly?: Set<string> | null,
    regulationDisplayDocs?: LibraryPointDisplayDoc[],
  ): void {
    this.rawGovPoints = points;
    let filtered: GovPoint[];
    let analyzed: ReturnType<typeof analyzeGovPointSet>;

    if (regulationDisplayDocs?.length) {
      const comparable: GovPoint[] = [];
      for (const doc of regulationDisplayDocs) {
        const rows = doc.useChapters
          ? doc.chapters.flatMap((ch) => ch.sections.flatMap((sec) => sec.rows))
          : doc.flatRows;
        for (const row of rows) {
          if (row.forAnalysis) comparable.push(row.point);
        }
      }
      filtered = comparable;
      analyzed = {
        storedCount: points.length,
        analyseCount: filtered.length,
        skippedCount: Math.max(0, points.length - filtered.length),
        comparable: filtered,
        skipped: [],
      };
      this.regulationDisplayDocs = regulationDisplayDocs;
      this.regulationDisplayUseDocGroups = true;
      this.pointDisplayChapters = [];
      this.pointDisplayFlatRows = [];
      this.pointDisplayUseChapters = false;
      this.expandedRegDocKeys = new Set();
      this.expandedRegChapterKeys = new Set();
      if (regulationDisplayDocs.length) {
        this.expandedRegDocKeys = new Set(regulationDisplayDocs.map((d) => d.key));
        for (const doc of regulationDisplayDocs) {
          if (doc.useChapters) {
            for (const ch of doc.chapters) {
              this.expandedRegChapterKeys.add(this.regChapterKey(doc.key, ch.chapter));
            }
          }
        }
      }
    } else {
      this.regulationDisplayDocs = [];
      this.regulationDisplayUseDocGroups = false;
      analyzed = analyzeGovPointSet(points);
      filtered = analyzed.comparable;
      const isForAnalysis = (p: GovPoint) => filtered.some((f) => f.point_id === p.point_id);
      const display = buildPointDisplayChapters(points, isForAnalysis);
      this.pointDisplayChapters = display.chapters;
      this.pointDisplayFlatRows = display.flatRows;
      this.pointDisplayUseChapters = display.useChapters;
    }

    this.govPoints = filtered;
    if (!this.useLibraryPoints) {
      this.regStoredCount = analyzed.storedCount;
      this.regSkippedCount = analyzed.skippedCount;
    }
    this.chapterGroups = groupGovPointsByChapter(filtered);
    this.selected.clear();
    if (selectionOnly?.size) {
      for (const p of filtered) {
        if (selectionOnly.has(p.point_id)) this.selected.add(p.point_id);
      }
    } else {
      filtered.forEach((p) => this.selected.add(p.point_id));
    }
    this.expandedChapters.clear();
    if (!this.regulationDisplayUseDocGroups) {
      if (this.pointDisplayChapters.length) {
        this.expandedChapters.add(this.pointDisplayChapters[0].chapter);
      } else if (this.chapterGroups.length) {
        this.expandedChapters.add(this.chapterGroups[0].chapter);
      }
    }
    if (!this.useLibraryPoints) {
      this.govSourceLabel = formatPointCountSummary(analyzed);
    } else {
      this.govSourceLabel = note;
    }
    const tfs = this.regCards.find((c) => c.id === 'tfs');
    if (tfs && this.highlightedCardIds.has('tfs')) {
      tfs.clauses = filtered.length || tfs.clauses;
    }
    this.syncSelectionToGovPoints();
  }

  regPointsFootnote(): string {
    if (!this.regStoredCount || this.useLibraryPoints) return '';
    return `${this.regStoredCount} stored · ${this.govPoints.length} compared in gap analysis`;
  }

  onComplianceSelect(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.uploadComplianceFile(file, false);
  }

  confirmComplianceVersionBump(): void {
    if (!this.pendingComplianceBump) return;
    const { file } = this.pendingComplianceBump;
    this.pendingComplianceBump = null;
    this.uploadComplianceFile(file, true);
  }

  cancelComplianceVersionBump(): void {
    this.pendingComplianceBump = null;
  }

  private uploadComplianceFile(file: File, confirmVersionBump: boolean): void {
    this.complianceFile = file;
    this.complianceFileName = file.name;
    this.complianceFileSize = `${Math.round(file.size / 1024)} KB`;
    this.selectedComplianceDocId = null;
    this.complianceDoc = null;
    this.error = '';
    this.analysisState = 'idle';

    if (!this.storageConfigured) {
      this.toast.show(`Attached ${file.name} (local only)`, 'warning', 3000);
      return;
    }

    this.uploadingCompliance = true;
    const form = new FormData();
    form.append('file', file);
    form.append('docKind', 'document');
    form.append('category', 'Compliance');
    form.append('filter', 'aml');
    form.append('workspaceId', this.workspaceId);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadDocument(form).subscribe({
      next: (res) => {
        this.uploadingCompliance = false;
        if ('duplicate' in res && res.duplicate && res.existing) {
          this.pendingComplianceBump = {
            file,
            nextVersion: res.nextVersion ?? 'v2',
            title: res.existing.title,
          };
          return;
        }
        if ('data' in res && res.success && res.data) {
          this.complianceDoc = res.data;
          this.selectedComplianceDocId = res.data.id;
          this.complianceDocs = [
            res.data,
            ...this.complianceDocs.filter((d) => d.id !== res.data!.id),
          ];
          this.toast.show(`Compliance saved · ${res.data.version}`, 'success', 2500);
        }
      },
      error: (err: HttpErrorResponse) => {
        this.uploadingCompliance = false;
        const body = err.error as {
          duplicate?: boolean;
          nextVersion?: string;
          existing?: StoredDocumentDto;
          message?: string;
        } | null;
        if (err.status === 409 && body?.duplicate && body.existing) {
          this.pendingComplianceBump = {
            file,
            nextVersion: body.nextVersion ?? 'v2',
            title: body.existing.title,
          };
          this.toast.show('This file already exists — select it from the list or confirm a new version.', 'warning', 4500);
          return;
        }
        this.toast.show(this.apiErrorMessage(err, 'Storage upload failed — file kept locally'), 'error', 4000);
      },
    });
  }

  removeCompliance(): void {
    this.complianceFile = null;
    this.complianceFileName = '';
    this.complianceFileSize = '';
    this.complianceDoc = null;
    this.selectedComplianceDocId = null;
    this.selectedComplianceIds.clear();
    this.analysisState = 'idle';
    this.pointsCollapsed = false;
  }

  togglePointsCollapsed(): void {
    this.pointsCollapsed = !this.pointsCollapsed;
  }

  toggleChapter(chapter: string): void {
    if (this.expandedChapters.has(chapter)) this.expandedChapters.delete(chapter);
    else this.expandedChapters.add(chapter);
  }

  isChapterExpanded(chapter: string): boolean {
    return this.expandedChapters.has(chapter);
  }

  toggle(id: string): void {
    if (this.selected.has(id)) this.selected.delete(id);
    else this.selected.add(id);
  }

  togglePointsByPrefix(prefix: string): void {
    const matching = this.govPoints.filter((p) =>
      pointMatchesPrefix(p.point_id, prefix, p.section),
    );
    const allSelected = matching.every((p) => this.selected.has(p.point_id));
    for (const p of matching) {
      if (allSelected) this.selected.delete(p.point_id);
      else this.selected.add(p.point_id);
    }
  }

  chapterAllSelected(chapter: string, points: GovPoint[]): boolean {
    return points.length > 0 && points.every((p) => this.selected.has(p.point_id));
  }

  sectionAllSelected(points: GovPoint[]): boolean {
    return points.length > 0 && points.every((p) => this.selected.has(p.point_id));
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  selectAll(): void {
    this.govPoints.forEach((p) => this.selected.add(p.point_id));
  }

  clearSelection(): void {
    this.selected.clear();
  }

  formatChapterLabel = formatChapterLabel;
  formatSectionGroupLabel = formatSectionGroupLabel;
  formatGovPointDisplayId = formatGovPointDisplayId;

  truncate(text: string, n: number): string {
    const t = (text ?? '').trim();
    return t.length <= n ? t : `${t.slice(0, n)}…`;
  }

  fileLabel(doc: StoredDocumentDto): string {
    if (doc.isNdManual) return doc.title || 'Manual custom points';
    return doc.originalFileName || doc.title;
  }

  pointMeta(doc: StoredDocumentDto): string {
    return `${doc.version} · ${doc.pointCount ?? 0} pts`;
  }

  runAnalysis(): void {
    this.stopDemoRun();
    this.isDemoRun = false;
    this.demoNdRunId = null;
    this.analysisCompleteUiDone = false;
    this.showInlineGapReport = false;
    this.inlineGapItems = [];
    const blocked = this.runBlockedReason;
    if (blocked) {
      this.error = blocked;
      this.toast.show(blocked, 'error', 3000);
      return;
    }

    const ids = this.comparableSelectedIds();
    if (!ids.length) {
      this.error = 'Select at least one comparable regulation point.';
      this.toast.show(this.error, 'error', 3000);
      return;
    }
    const selectedGovPoints = this.govPoints.filter((p) => ids.includes(p.point_id));

    const form = new FormData();
    form.append('pointIds', JSON.stringify(ids));
    form.append(
      'govPointsJson',
      JSON.stringify(
        selectedGovPoints.map((p) => ({
          pointId: p.point_id,
          title: p.title ?? null,
          text: p.text,
          section: p.section ?? null,
        })),
      ),
    );
    form.append('granularity', this.granularity);
    form.append('govDocId', 'gov-tfs-guidelines');
    form.append('internalDocId', 'internal-imptfs');
    form.append('phase2Model', this.aiModel);
    form.append('forceRefresh', String(this.forceRefresh));
    form.append('internalFile', this.complianceFile!);
    if (this.complianceDoc?.id) {
      form.append('analysisInternalDocumentId', this.complianceDoc.id);
    }
    if (this.selectedRegDocs[0]?.id) {
      form.append('analysisRegulationDocumentId', this.selectedRegDocs[0].id);
    } else if (this.libraryPrimaryRegDocId) {
      form.append('analysisRegulationDocumentId', this.libraryPrimaryRegDocId);
    }
    form.append('analysisWorkspaceId', this.workspaceId);
    const regLabel =
      this.selectedRegDocs.length > 1
        ? this.selectedRegDocs
            .map((d) => d.originalFileName || d.title)
            .filter(Boolean)
            .join(' + ')
        : this.selectedRegDocs[0]?.originalFileName
          || this.selectedRegDocs[0]?.title
          || 'TFS Guidelines.pdf';
    form.append('analysisRegulationFileName', regLabel);
    form.append(
      'analysisInternalFileName',
      this.complianceFileName || this.complianceDoc?.originalFileName || 'I M P T F S.pdf',
    );

    this.stopPolling();
    this.analysisState = 'running';
    this.pointsCollapsed = true;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.sessionSelectedPointIds = new Set(ids);
    this.selectedDetailPointId = null;
    for (const id of ids) this.sessionPointStatus.set(id, 'queued');
    this.error = '';
    this.progress = 8;
    this.progressDone = 0;
    this.progressTotal = ids.length;
    this.findingsPreview = [];
    this.resetSteps();
    this.markStep(0, true);
    this.analysisSteps[1].label = `Loading regulation clauses (${this.govPoints.length} found)`;

    this.api.startJob(form).subscribe({
      next: (r) => {
        this.sessionId = (r.data as { id: string }).id;
        this.toast.show('Dual-verify job started', 'success', 2000);
        this.markStep(0, false);
        this.markStep(1, true);
        this.progress = 25;
        this.activeSessions.refresh();
        this.poll(this.sessionId!);
      },
      error: (e: HttpErrorResponse) => {
        this.analysisState = 'idle';
        this.pointsCollapsed = false;
        this.error = this.apiErrorMessage(e, 'Start failed');
        this.toast.show(this.error, 'error', 5000);
      },
    });
  }

  stopAnalysis(): void {
    if (this.isDemoRun) {
      this.stopDemoRun();
      this.analysisState = 'complete';
      this.toast.show('Demo run stopped', 'warning', 2500);
      return;
    }
    if (!this.sessionId || !this.isSessionActive) return;
    const label = this.complianceFileName || 'this analysis';
    const ok = window.confirm(
      `Stop "${label}"?\n\nQueued points will be cancelled. A point already being analysed may finish its current pass first.`,
    );
    if (!ok) return;

    const sessionId = this.sessionId;
    this.api.cancelSession(sessionId).subscribe({
      next: () => {
        this.stopPolling();
        this.analysisState = 'complete';
        this.progress = 100;
        this.analysisSteps.forEach((st) => {
          st.done = true;
          st.active = false;
        });
        for (const [pointId, status] of this.sessionPointStatus) {
          if (status === 'queued' || status === 'processing' || status === 'running') {
            this.sessionPointStatus.set(pointId, 'cancelled');
          }
        }
        this.toast.show('Analysis stopped', 'warning', 3500);
        this.activeSessions.refresh();
      },
      error: (e: HttpErrorResponse) => {
        this.toast.show(this.apiErrorMessage(e, 'Could not stop analysis'), 'error', 4000);
      },
    });
  }

  openFullReport(): void {
    this.buildInlineGapItems();
    this.showInlineGapReport = true;
    this.scrollToInlineGapReport();
  }

  /** Hook for shells to scroll to the inline gap report panel. */
  protected scrollToInlineGapReport(): void {}

  /** Called after a demo/ND run is persisted to the database. */
  protected onNdRunSaved(_runId: string): void {}

  openFinding(pointId: string): void {
    this.selectPointForDetail(pointId);
  }

  openAdvancedWorkbench(): void {
    if (this.sessionId) {
      this.router.navigate(shellRouteSegments(this.router, '/dual-verify'), {
        queryParams: { session: this.sessionId },
      });
      return;
    }
    this.router.navigate(shellRouteSegments(this.router, '/dual-verify'));
  }

  retrySinglePoint(pointId: string): void {
    if (this.ndRunId) {
      void this.retryNdPoint(pointId, this.needsPhase2RerunForPoint(pointId));
      return;
    }
    if (!this.sessionId) return;
    this.enqueueRetry([pointId], !this.needsPhase2RerunForPoint(pointId));
  }

  retryAllRunPoints(): void {
    if (!this.sessionId) return;
    const ids = [...this.sessionPointStatus.keys()];
    if (!ids.length) {
      this.toast.show('No analysed points to re-run yet', 'warning');
      return;
    }
    this.enqueueRetry(ids, true);
  }

  runRemainingPoints(): void {
    if (this.ndRunId) {
      void this.launchNdAnalysisRun(this.ndRunId, this.comparableSelectedIds());
      return;
    }
    if (!this.sessionId) return;
    const ids = this.coverageRows
      .filter((r) => r.status === 'not-run')
      .map((r) => r.pointId);
    if (!ids.length) {
      this.toast.show('All loaded points already have a run status', 'info');
      return;
    }
    for (const id of ids) {
      this.selected.add(id);
      this.sessionSelectedPointIds?.add(id);
    }
    this.enqueueRetry(ids, false);
  }

  retryFailedOnly(): void {
    if (this.ndRunId) {
      void this.retryAllNdPhase2();
      return;
    }
    if (!this.sessionId) return;
    const ids = this.coverageRows
      .filter((r) => this.needsPhase2RerunForPoint(r.pointId))
      .map((r) => r.pointId);
    if (!ids.length) {
      this.toast.show('No points need Phase 2 retry', 'info');
      return;
    }
    this.enqueueRetry(ids, false);
  }

  private async retryNdPoint(pointId: string, phase2Only: boolean): Promise<void> {
    if (!this.ndRunId) return;
    const p = this.sessionPointResults.get(pointId);
    if (!p?.id) {
      this.toast.show('Point not found in this run', 'warning');
      return;
    }
    this.retryingPointId = pointId;
    this.analysisState = 'running';
    const res = phase2Only
      ? await this.ndApi.rerunDualVerify(this.ndRunId, p.id)
      : await this.ndApi.rerunPoint(this.ndRunId, p.id);
    this.retryingPointId = null;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not re-queue point', 'error');
      return;
    }
    this.sessionPointStatus.set(pointId, 'queued');
    this.toast.show(phase2Only ? 'Re-running Phase 2…' : 'Re-queued point', 'success', 2200);
    this.pollNdRun(this.ndRunId);
  }

  protected async retryAllNdPhase2(): Promise<void> {
    if (!this.ndRunId) return;
    this.retryingPointId = '__batch__';
    this.analysisState = 'running';
    const res = await this.ndApi.rerunAllFailedDualVerify(this.ndRunId);
    this.retryingPointId = null;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not re-queue Phase 2 retries', 'error');
      return;
    }
    this.toast.show('Re-running Phase 2 for failed points…', 'success', 2200);
    this.pollNdRun(this.ndRunId);
  }

  private enqueueRetry(pointIds: string[], forceRefresh: boolean): void {
    if (!this.sessionId || !pointIds.length) return;
    this.retryingPointId = pointIds.length === 1 ? pointIds[0] : '__batch__';
    this.error = '';
    this.analysisState = 'running';
    this.pointsCollapsed = true;

    const form = new FormData();
    form.append('pointIds', JSON.stringify(pointIds));
    form.append('forceRefresh', String(forceRefresh));
    const points = this.govPoints.filter((p) => pointIds.includes(p.point_id));
    if (points.length) {
      form.append(
        'govPointsJson',
        JSON.stringify(
          points.map((p) => ({
            pointId: p.point_id,
            title: p.title ?? null,
            text: p.text,
            section: p.section ?? null,
          })),
        ),
      );
    }
    if (this.complianceFile) form.append('internalFile', this.complianceFile);

    this.api.retryPoints(this.sessionId, form).subscribe({
      next: (r) => {
        const n = r.data?.requeued ?? pointIds.length;
        this.toast.show(`Re-queued ${n} point(s)`, 'success', 2200);
        this.retryingPointId = null;
        for (const id of pointIds) {
          this.sessionPointStatus.set(id, 'queued');
          this.sessionSelectedPointIds?.add(id);
        }
        this.progressTotal = Math.max(this.progressTotal, this.sessionSelectedPointIds?.size ?? this.progressTotal);
        this.poll(this.sessionId!);
      },
      error: (e: HttpErrorResponse) => {
        this.retryingPointId = null;
        this.analysisState = 'complete';
        this.error = e.error?.message ?? 'Could not re-queue points';
        this.toast.show(this.error, 'error', 4000);
      },
    });
  }

  attachToExistingSession(sessionId: string): void {
    forkJoin({
      progress: this.api.getJob(sessionId),
      regs: this.api.listStoredDocuments('regulation', this.workspaceId),
      compliance: this.api.listStoredDocuments('document', this.workspaceId),
    }).subscribe({
      next: ({ progress: r, regs, compliance }) => {
        const s = r.data?.session;
        const points = r.data?.points ?? [];
        if (!s) {
          this.toast.show('Session not found', 'warning');
          return;
        }

        const sessionPointIds = points.map((p) => p.pointId).filter(Boolean);
        this.sessionSelectedPointIds = new Set(sessionPointIds);
        // Show Stop + progress immediately while regulation/compliance files load.
        this.applySessionJobState(sessionId, s, points);
        if (this.analysisState === 'running') this.onRunResumeAttached();

        if (regs.data?.length) this.regulationDocs = regs.data;
        if (compliance.data?.length) {
          this.complianceDocs = compliance.data;
          this.seededImptfs =
            this.complianceDocs.find(
              (d) => (d.fileHash ?? '').toLowerCase() === this.IMPTFS_HASH.toLowerCase(),
            ) ??
            this.complianceDocs.find((d) =>
              /i\s*m\s*p\s*t\s*f\s*s|imptfs/i.test(`${d.title} ${d.originalFileName}`),
            ) ??
            null;
        }

        if (s.granularity === 'leaf' || s.granularity === 'section') {
          this.granularity = s.granularity;
        }
        if (s.phase2Model) this.aiModel = s.phase2Model;

        const regDoc = this.findStoredDoc(this.regulationDocs, {
          id: s.regulationDocumentId,
          hash: s.govFileHash,
          name: s.govFileName,
        });
        const complianceDoc = this.findStoredDoc(this.complianceDocs, {
          id: s.internalDocumentId,
          hash: s.internalFileHash,
          name: s.internalFileName,
        });

        this.selectedRegIds.clear();
        if (regDoc) {
          this.selectedRegIds.add(regDoc.id);
        } else if (this.regulationDocs.length) {
          const tfs =
            this.regulationDocs.find(
              (d) => (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase(),
            ) ?? this.regulationDocs[0];
          if (tfs) this.selectedRegIds.add(tfs.id);
        }
        this.syncSelectedDocs();

        this.selectedComplianceIds.clear();
        if (complianceDoc) {
          this.selectedComplianceIds.add(complianceDoc.id);
          this.selectComplianceDoc(complianceDoc, { silent: true, preserveAnalysisState: true });
        }

        const applyJobState = () => {
          this.applySessionJobState(sessionId, s, points);
          if (this.analysisState === 'running') this.onRunResumeAttached();
        };

        if (this.selectedRegIds.size) {
          this.loadPointsForSelectedFiles(applyJobState, {
            silent: true,
            selectedIds: sessionPointIds,
          });
        }
      },
      error: () => this.toast.show('Could not load analysis session', 'error'),
    });
  }

  private findStoredDoc(
    docs: StoredDocumentDto[],
    hint: { id?: string | null; hash?: string | null; name?: string | null },
  ): StoredDocumentDto | null {
    if (hint.id) {
      const byId = docs.find((d) => d.id === hint.id);
      if (byId) return byId;
    }
    if (hint.hash) {
      const h = hint.hash.toLowerCase();
      const byHash = docs.find((d) => (d.fileHash ?? '').toLowerCase() === h);
      if (byHash) return byHash;
    }
    if (hint.name) {
      const raw = hint.name.trim();
      const base = raw.replace(/\.[^.]+$/, '').toLowerCase();
      const byName = docs.find((d) => {
        const file = (d.originalFileName ?? '').toLowerCase();
        const title = (d.title ?? '').toLowerCase();
        return file === raw.toLowerCase() || title === base || file === raw.toLowerCase();
      });
      if (byName) return byName;
    }
    return null;
  }

  private applySessionJobState(
    sessionId: string,
    s: SessionProgress['session'],
    points: SessionProgress['points'],
  ): void {
    this.sessionId = sessionId;
    this.progressDone = s.completedPoints ?? 0;
    this.progressTotal = s.totalPoints ?? 0;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.selected.clear();
    this.sessionSelectedPointIds = new Set<string>();

    for (const p of points) {
      if (!p.pointId) continue;
      this.sessionPointStatus.set(p.pointId, this.normalizeStoredPointStatus(p));
      this.sessionPointResults.set(p.pointId, p);
      this.sessionSelectedPointIds.add(p.pointId);
      this.selected.add(p.pointId);
    }

    const firstResult =
      points.find((p) => p.status === 'completed' || p.status === 'failed') ?? points[0];
    this.selectedDetailPointId = firstResult?.pointId ?? null;
    this.syncSelectionToGovPoints();

    const st = (s.status || '').toLowerCase();
    const done = (s.completedPoints ?? 0) + (s.failedPoints ?? 0);
    const stillRunning =
      st === 'queued' ||
      st === 'processing' ||
      st === 'running' ||
      (this.progressTotal > 0 && done < this.progressTotal && !['completed', 'failed', 'cancelled'].includes(st));

    if (stillRunning) {
      this.analysisState = 'running';
      this.pointsCollapsed = true;
      this.progress =
        this.progressTotal > 0
          ? Math.min(95, Math.max(10, Math.round((this.progressDone / this.progressTotal) * 100)))
          : 10;
      this.resetSteps();
      if (this.progressDone > 0) {
        this.markStep(0, true);
        this.markStep(1, true);
      }
      this.poll(sessionId);
    } else {
      this.analysisState = 'complete';
      this.progress = 100;
      this.findingsPreview = points
        .filter((p) => p.status === 'completed' || p.agreementJson)
        .slice(0, 8)
        .map((p) => ({
          severity: this.severityFromPoint(p.agreementJson),
          title: p.pointTitle || p.pointId,
          section: `§${p.pointId}`,
          pointId: p.pointId,
        }));
      this.onAnalysisComplete();
    }
  }

  private poll(sessionId: string): void {
    this.stopPolling();
    this.pollTimer = setInterval(() => {
      this.api.getJob(sessionId).subscribe({
        next: (r) => {
          const s = r.data?.session;
          const points = r.data?.points ?? [];
          if (!s) return;
          this.progressDone = s.completedPoints ?? 0;
          this.progressTotal = s.totalPoints ?? this.progressTotal;
          for (const p of points) {
            if (p.pointId) {
              this.sessionPointStatus.set(p.pointId, this.normalizeStoredPointStatus(p));
              this.sessionPointResults.set(p.pointId, p);
            }
          }
          const pct =
            this.progressTotal > 0
              ? Math.round((this.progressDone / this.progressTotal) * 100)
              : 10;
          this.progress = Math.min(95, Math.max(25, pct));

          if (this.progressDone > 0) {
            this.markStep(1, false);
            this.markStep(2, true);
          }
          if (pct > 50) {
            this.markStep(2, false);
            this.markStep(3, true);
          }
          if (pct > 80) {
            this.markStep(3, false);
            this.markStep(4, true);
          }

          if (s.status === 'completed' || s.status === 'failed' || s.status === 'cancelled') {
            this.stopPolling();
            this.activeSessions.refresh();
            this.progress = 100;
            this.analysisSteps.forEach((st) => {
              st.done = true;
              st.active = false;
            });
            if (s.status === 'completed') {
              this.analysisState = 'complete';
              this.findingsPreview = points
                .filter((p) => p.status === 'completed' || p.agreementJson)
                .slice(0, 8)
                .map((p) => ({
                  severity: this.severityFromPoint(p.agreementJson),
                  title: p.pointTitle || p.pointId,
                  section: `§${p.pointId}`,
                  pointId: p.pointId,
                }));
              this.toast.show('Analysis complete', 'success');
              this.onAnalysisComplete();
            } else {
              this.analysisState = 'idle';
              const failed = points.filter((p) => p.status === 'failed');
              const err = failed.find((p) => p.errorMessage)?.errorMessage;
              this.error = err
                ? `Analysis failed: ${err}`
                : `Session ${s.status}`;
              this.toast.show(this.error, 'error', 6000);
            }
          }
        },
        error: () => {
          /* keep polling */
        },
      });
    }, 2000);
  }

  protected severityFromPoint(
    agreement?: { status?: string; label?: string; summary?: string } | null,
  ): string {
    const v = `${agreement?.status ?? ''} ${agreement?.label ?? ''}`.toLowerCase();
    if (!v.trim()) return 'high';
    if (v.includes('critical') || v.includes('non')) return 'critical';
    if (v.includes('partial') || v.includes('gap')) return 'high';
    if (v.includes('compliant') || v.includes('aligned') || v.includes('agree')) return 'medium';
    return 'high';
  }

  private markStep(index: number, active: boolean): void {
    this.analysisSteps.forEach((s, i) => {
      if (i < index) {
        s.done = true;
        s.active = false;
      } else if (i === index) {
        s.active = active;
        if (!active && this.progress >= 100) s.done = true;
        if (!active && index > 0) s.done = true;
      }
    });
    if (!active) this.analysisSteps[index].done = true;
  }

  private resetSteps(): void {
    this.analysisSteps.forEach((s) => {
      s.done = false;
      s.active = false;
    });
    this.analysisSteps[1].label = 'Loading regulation clauses';
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private stopNdRunPolling(): void {
    if (this.ndRunPollTimer) {
      clearInterval(this.ndRunPollTimer);
      this.ndRunPollTimer = null;
    }
  }

  private async attachToNdAnalysisRun(runId: string): Promise<void> {
    this.analysisCompleteUiDone = false;
    this.showInlineGapReport = false;
    this.ndRunId = runId;
    this.isDemoRun = false;

    const [detailRes, statusRes] = await Promise.all([
      this.ndApi.getAnalysisRun(runId),
      this.ndApi.getAnalysisRunStatus(runId),
    ]);

    if (!detailRes.success || !detailRes.data) {
      this.toast.show(detailRes.message ?? 'Could not load analysis run', 'error');
      return;
    }

    const detail = detailRes.data as {
      run: {
        name: string;
        status: string;
        libraryId?: string | null;
        selectedPointsSnapshot: string;
        selectedInternalDocIds: string;
        selectedRegulationDocIds: string;
        totalPointsCount: number;
        processedPointsCount: number;
        dualVerifyFailedCount?: number;
      };
      points: AnalysisPoint[];
    };

    this.ndRunLibraryId = detail.run.libraryId ? String(detail.run.libraryId) : null;
    this.ndRunDualVerifyFailedCount = detail.run.dualVerifyFailedCount ?? 0;

    const status = statusRes.success
      ? (statusRes.data as {
          status: string;
          totalPointsCount: number;
          processedPointsCount: number;
          points: AnalysisPoint[];
        })
      : null;

    const points = status?.points ?? detail.points;
    const isDemoRun = (detail.run.name ?? '').startsWith('[Demo]');
    if (isDemoRun) {
      this.isDemoRun = true;
      this.demoNdRunId = runId;
    } else {
      this.demoNdRunId = null;
    }
    this.applyNdRunState(runId, detail.run, points, status, isDemoRun);

    const snapshot = this.parseJsonArray(detail.run.selectedPointsSnapshot);
    const govPoints: GovPoint[] = snapshot.map((raw) => {
      const snap = raw as Record<string, unknown>;
      const pointNumber = String(snap['pointNumber'] ?? snap['point_id'] ?? '');
      return {
        point_id: pointNumber,
        title: String(snap['pointTitle'] ?? snap['title'] ?? ''),
        text: String(snap['pointContent'] ?? snap['text'] ?? ''),
        section: String(snap['pageReference'] ?? snap['section'] ?? pointNumber),
      };
    }).filter((p) => p.point_id);

    if (govPoints.length) {
      this.applyGovPoints(govPoints, detail.run.name, this.sessionSelectedPointIds);
    }

    const regIds = this.parseJsonArray(detail.run.selectedRegulationDocIds).map(String);
    const internalIds = this.parseJsonArray(detail.run.selectedInternalDocIds).map(String);

    this.refreshRegulations(() => {
      this.selectedRegIds.clear();
      for (const id of regIds) {
        const doc = this.regulationDocs.find(
          (d) => d.id === id || d.ndStoredDocumentId === id,
        );
        if (doc) this.selectedRegIds.add(doc.id);
      }
      if (!this.selectedRegIds.size && this.regulationDocs.length) {
        const tfs =
          this.regulationDocs.find(
            (d) => (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase(),
          ) ?? this.regulationDocs[0];
        if (tfs) this.selectedRegIds.add(tfs.id);
      }
      this.syncSelectedDocs();
    });

    this.refreshComplianceDocs(() => {
      this.selectedComplianceIds.clear();
      for (const id of internalIds) {
        const doc = this.complianceDocs.find((d) => d.id === id);
        if (doc) {
          this.selectedComplianceIds.add(doc.id);
          this.selectComplianceDoc(doc, { silent: true, preserveAnalysisState: true });
        }
      }
    });

    this.pointsCollapsed = true;

    await this.onNdRunContextLoaded(detail);

    const runStatus = (status?.status ?? detail.run.status ?? '').toLowerCase();
    const processedCount = status?.processedPointsCount ?? detail.run.processedPointsCount ?? 0;
    const totalCount = status?.totalPointsCount ?? detail.run.totalPointsCount ?? 0;
    const activelyProcessing = this.applyNdRunState(
      runId,
      detail.run,
      points,
      status,
      isDemoRun,
    );

    if (activelyProcessing) {
      if (runStatus === 'running') {
        this.pollNdRun(runId);
      }
      this.onRunResumeAttached();
    } else if (this.analysisState === 'running') {
      this.onRunResumeAttached();
    } else {
      this.ndRunWorkflowStatus = runStatus;
    }
  }

  /** Hook for ND shells to restore library/regulation UI when opening ?run=. */
  protected async onNdRunContextLoaded(_detail: {
    run: {
      libraryId?: string | null;
      selectedPointsSnapshot: string;
    };
    points: AnalysisPoint[];
  }): Promise<void> {}

  /** Hook for shells (e.g. analyse-v8) to scroll/focus when resuming an in-progress run. */
  protected onRunResumeAttached(): void {}

  /**
   * ND shell only — starts NdAnalysisProcessor and polls DB status.
   * Legacy {@link runAnalysis} (dual-verify-kafka jobs) is unchanged for /old/*.
   */
  protected async launchNdAnalysisRun(runId: string, selectedIds: string[]): Promise<boolean> {
    this.stopDemoRun();
    this.isDemoRun = false;
    this.demoNdRunId = null;
    this.analysisCompleteUiDone = false;
    this.showInlineGapReport = false;
    this.stopPolling();
    this.sessionId = null;
    this.ndRunId = runId;
    this.analysisState = 'running';
    this.pointsCollapsed = true;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.sessionSelectedPointIds = new Set(selectedIds);
    this.selectedDetailPointId = null;
    for (const id of selectedIds) this.sessionPointStatus.set(id, 'queued');
    this.error = '';
    this.progress = 8;
    this.progressDone = 0;
    this.progressTotal = selectedIds.length;
    this.findingsPreview = [];
    this.resetSteps();
    this.markStep(0, true);
    this.analysisSteps[1].label = `Loading regulation clauses (${this.govPoints.length} found)`;

    const res = await this.ndApi.startAnalysisRun(runId);
    if (!res.success) {
      this.analysisState = 'idle';
      this.pointsCollapsed = false;
      this.error = res.message ?? 'Failed to start analysis';
      this.toast.show(this.error, 'error', 5000);
      return false;
    }

    this.toast.show('Analysis started', 'success', 2000);
    this.markStep(0, false);
    this.markStep(1, true);
    this.progress = 25;
    this.activeSessions.refresh();
    this.pollNdRun(runId);
    return true;
  }

  private isNdAnalyseRoute(): boolean {
    return this.router.url.includes('/nd/analyse-v8');
  }

  private applyNdRunState(
    runId: string,
    run: {
      status: string;
      totalPointsCount: number;
      processedPointsCount: number;
      dualVerifyFailedCount?: number;
    },
    points: AnalysisPoint[],
    status: { status: string; totalPointsCount: number; processedPointsCount: number } | null,
    isDemoRun = false,
  ): boolean {
    this.sessionId = null;
    this.progressTotal = status?.totalPointsCount ?? run.totalPointsCount ?? points.length;
    this.progressDone = status?.processedPointsCount ?? run.processedPointsCount ?? 0;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.selected.clear();
    this.sessionSelectedPointIds = new Set<string>();

    for (const p of points) {
      const mapped = this.mapNdAnalysisPoint(p);
      if (!mapped.pointId) continue;
      this.sessionPointStatus.set(mapped.pointId, this.normalizeStoredPointStatus(mapped));
      this.sessionPointResults.set(mapped.pointId, mapped);
      this.sessionSelectedPointIds.add(mapped.pointId);
      this.selected.add(mapped.pointId);
    }

    const first =
      points.find((p) => {
        const snap = parsePointSnapshot(p.pointSnapshot);
        return p.landingAiStatus === 'completed' || p.dualVerifyStatus === 'completed';
      }) ?? points[0];
    if (first) {
      const snap = parsePointSnapshot(first.pointSnapshot);
      this.selectedDetailPointId = snap.pointNumber || first.regulationPointId || first.id;
    }

    const runStatus = (status?.status ?? run.status ?? '').toLowerCase();
    const allPointsProcessed =
      this.progressTotal > 0 && this.progressDone >= this.progressTotal;
    const inFlight = runStatus === 'draft' || runStatus === 'running';
    const hasLandingPendingOrFailed = points.some(
      (p) => p.landingAiStatus === 'pending' || p.landingAiStatus === 'failed',
    );

    this.ndRunDualVerifyFailedCount = run.dualVerifyFailedCount ?? 0;

    const needsExecutionView = inFlight || !allPointsProcessed || hasLandingPendingOrFailed;
    const activelyProcessing =
      runStatus === 'running' || runStatus === 'processing' || runStatus === 'draft';

    if (needsExecutionView) {
      this.analysisState = 'running';
      this.showInlineGapReport = false;
      this.analysisCompleteUiDone = false;
      this.progress =
        this.progressTotal > 0
          ? Math.min(
              activelyProcessing ? 95 : 100,
              Math.max(10, Math.round((this.progressDone / this.progressTotal) * 100)),
            )
          : 10;
      this.resetSteps();
      if (this.progressDone > 0) {
        this.markStep(0, true);
        this.markStep(1, true);
      }
      this.ndRunWorkflowStatus = runStatus;
      if (!activelyProcessing) this.stopNdRunPolling();
      this.syncSelectionToGovPoints();
      return activelyProcessing;
    }

    this.analysisState = 'complete';
    this.progress = 100;
    if (isDemoRun) this.demoNdRunId = runId;
    this.ndRunWorkflowStatus = runStatus;
    this.stopNdRunPolling();
    this.onAnalysisComplete();
    this.syncSelectionToGovPoints();
    return false;
  }

  private mapNdAnalysisPoint(p: AnalysisPoint): SessionPoint {
    const snap = parsePointSnapshot(p.pointSnapshot);
    const pointId = snap.pointNumber || p.regulationPointId || p.id;
    const landingMessage = this.parseNdAiPayload(p.landingAiResult).message;
    const google = this.parseNdAiPayload(p.googleAiResult);
    const llmMessage = google.message;
    const agreement = google.agreement;

    let status = 'queued';
    if (p.dualVerifyStatus === 'completed' || p.finalStatus) status = 'completed';
    else if (p.landingAiStatus === 'failed') status = 'failed';
    else if (
      landingMessage &&
      (p.googleAiStatus === 'failed' || p.dualVerifyStatus === 'failed')
    ) {
      status = 'completed';
    } else if (p.landingAiStatus === 'running' || p.dualVerifyStatus === 'running') status = 'running';
    else if (p.landingAiStatus === 'completed' && p.dualVerifyStatus !== 'completed') status = 'running';
    else if (p.landingAiStatus === 'completed') status = 'completed';

    return {
      id: p.id,
      pointId,
      pointTitle: snap.pointTitle ?? undefined,
      status,
      landingMessage: landingMessage || undefined,
      llmMessage: llmMessage || undefined,
      agreementJson: agreement,
      errorMessage: p.landingAiError ?? p.googleAiError ?? undefined,
    };
  }

  private parseNdAiPayload(raw?: string | null): { message: string; agreement?: SessionPoint['agreementJson'] } {
    if (!raw) return { message: '' };
    try {
      const parsed = JSON.parse(raw) as { message?: string; agreement?: SessionPoint['agreementJson'] };
      return { message: parsed.message ?? '', agreement: parsed.agreement };
    } catch {
      return { message: raw };
    }
  }

  protected parseJsonArray(value: string | undefined): unknown[] {
    if (!value) return [];
    try {
      const parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  private pollNdRun(runId: string): void {
    this.stopNdRunPolling();
    this.ndRunPollTimer = setInterval(() => {
      void this.ndApi.getAnalysisRunStatus(runId).then((res) => {
        if (!res.success || !res.data) return;
        const data = res.data as {
          status: string;
          totalPointsCount: number;
          processedPointsCount: number;
          points: AnalysisPoint[];
        };
        this.applyNdRunState(runId, data, data.points, data);
      });
    }, 2000);
  }

  private apiErrorMessage(e: HttpErrorResponse, fallback = 'Request failed'): string {
    const body = e?.error as { message?: string; Message?: string } | string | null;
    if (typeof body === 'string' && body.trim()) return body;
    if (body && typeof body === 'object') {
      if (body.message?.trim()) return body.message;
      if (body.Message?.trim()) return body.Message;
    }
    return e?.message?.trim() || fallback;
  }
}
