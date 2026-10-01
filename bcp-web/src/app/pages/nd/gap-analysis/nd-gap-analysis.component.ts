import {
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  inject,
  Input,
  OnChanges,
  OnDestroy,
  OnInit,
  Output,
  signal,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { catchError, distinctUntilChanged, map } from 'rxjs/operators';
import {
  isRegulPipelineHybridWorkflow,
  isRegulWorkflow,
  v5CompliantScopeNote,
} from '../../../../lib/nd/regul-fields';
import { NdStepTrackerService, type NdStep } from '../../../services/nd/nd-step-tracker.service';
import { NdPipelinePanelService } from '../../../services/nd/nd-pipeline-panel.service';
import {
  exportGapAnalysisExcelFromPoints,
  exportGapAnalysisPdfFromPoints,
  exportRegulGapAnalysisExcelFromPoints,
  gapAnalysisExportColumns,
  type GapAnalysisExportConfirm,
  type GapAnalysisExportSelection,
} from '../../../../lib/nd/export/gap-analysis-export';
import { NdExportOptionsDialogComponent } from '../../../components/nd/nd-export-options-dialog.component';
import { NdReviewSummaryPanelComponent } from '../../../components/nd/nd-review-summary-panel.component';
import {
  NdReportSummaryStackComponent,
  type ReportSummaryFilterId,
} from '../../../components/nd/nd-report-summary-stack.component';
import { NdClauseRailCardComponent } from '../../../components/nd/nd-clause-rail-card.component';
import {
  buildSeededActionPlansForGap,
  type SeededActionPlan,
} from '../../../../lib/nd/action-plan-seed';
import { capGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { isAnalysisRunResultsReady } from '../../../../lib/nd/analysis-run-status';
import { normalizeGapRisk } from '../../../../lib/nd/doc-analysis-ready';
import {
  buildGapEvidencePrepMarquee,
  buildGapEvidenceRerunMarquee,
  gapEvidencePrepStepLabel,
  gapEvidenceRerunProgressRows,
  gapEvidenceRerunUiStatusLabel,
  isGapEvidenceRerunInFlight,
  mergeActivityMarquee,
  summarizeGapEvidenceRerunProgress,
  type GapEvidencePrepStep,
  type GapEvidenceRerunUiStatus,
} from '../../../../lib/nd/gap-evidence-activity';
import {
  GAP_EVIDENCE_LOCAL_ENGINE,
  gapEvidencePrepDetailFromLocalRow,
  gapEvidencePrepInProgress,
  gapEvidenceNotStartedDetail,
  gapEvidencePrepStepFromLocalRow,
  runGapEvidenceLocalPipeline,
} from '../../../../lib/nd/gap-evidence-local-pipeline';
import {
  gapStateKey,
  indexGapStates,
  patchDerivedGapStatusesForPoint,
  recomputeAutoClauseStatus,
  rollupClause,
  type ClauseRollup,
  type GapState,
} from '../../../../lib/nd/gap-state';
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
import type { AnalysisPoint, PointGapAttachment, PointSnapshot } from '../../../../lib/nd/types';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdWorkspaceNavService } from '../../../services/nd/nd-workspace-nav.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../../components/nd/nd-point-sort-controls.component';
import {
  NdRunReviewPanelComponent,
  type RunReviewPanelMode,
  type RunReviewSubmitEvent,
} from '../../../components/nd/nd-run-review-panel.component';
import { NdRunHistoryPanelComponent } from '../../../components/nd/nd-run-history-panel.component';
import { DualVerifyResultCardComponent } from '../../../components/dual-verify-result-card/dual-verify-result-card.component';
import type { ActionPlanHistoryEntry, InternalDocument, ResultsData } from '../../../../lib/nd/types';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import { countCapGapsForAnalysisPoint, countDisplayGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { compareText, type SortDir } from '../../../../lib/nd/list-utils';
import { sortByPointKey, type PointSortMode } from '../../../../lib/nd/point-sort';
import { complianceSeverityLabel,
  COMPLIANCE_SEVERITY_LABELS,
  resolveAnalysisPointSeverity,
  resolveDisplayConfidence,
  type ComplianceSeverity,
} from '../../../../lib/nd/point-compliance-status';
import { policySnippetFromAnalysisPoint } from '../../../../lib/nd/analysis-point-rail-meta';
import { buildClauseRailCardFields } from '../../../../lib/nd/clause-rail-card-display';
import { parseReferenceComplianceBlock } from '../../../../lib/ai-lab/parse-reference-response';
import { internalDocCatalogFromRunDetail } from '../../../../lib/nd/run-internal-docs';
import type { PolicyDocCatalogEntry } from '../../../../lib/nd/policy-doc-resolve';
import { reviewsForPoint, type ActionItemReviewEntry, type ActionItemReviewStatus, countSavedReviewProgress } from '../../../../lib/nd/action-item-review';
import { tempCommentsForPoint, type TempPointReviewComment, type TempReviewCommentsChangeEvent } from '../../../../lib/nd/temp-point-review-comment';
import { ndNewAnalysisRoute } from '../../../../lib/nd/demo-analysis-routes';
import {
  actionPlansForPoint,
  actionPlanPriorityLabel,
  formatActionPlanDate,
  normalizeActionPlanPriority,
  normalizeActionPlanStatus,
  toDateInputValue,
  type ActionPlanEntry,
  type ActionPlanPriority,
  type ActionPlanReviewEntry,
  type ActionPlanStatus,
} from '../../../../lib/nd/action-plan';
import {
  canAddActionItemReviews,
  canUploadGapEvidence as canUploadGapEvidenceForRun,
  gapEvidenceUploadDisabledHint,
  isReviewRole,
  reviewDisabledHint,
  reviewWorkspaceLink,
  runReviewPhaseFromStatus,
  runReviewSubmitModeForViewer,
  workflowSubmitTargetsForViewer,
  canFinalizeWorkflowRun,
  attachmentCountsByPoint,
  type WorkflowSubmitTarget,
} from '../../../../lib/nd/nd-review-run-helpers';
import { computeRunGapStats, type RunGapStatsSummary } from '../../../../lib/nd/run-gap-stats';
import { buildNdGapListItems, ndComplianceSummaryFromPoints } from '../../../../lib/nd/nd-run-display';
import type { NdRunReviewBody } from '../../../services/nd/nd-api.service';
import { NdPageHeaderActionsService } from '../../../services/nd/nd-page-header-actions.service';
import type { RunReviewDraft } from '../../../../lib/nd/run-review';

/** Seeded TFS × IMPTFS combined compliance session (32 points). */
const SEEDED_COMPLIANCE_SESSION = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';

const EMPTY_GAP_ATTACHMENTS: PointGapAttachment[] = [];
const EMPTY_ACTION_REVIEWS: ActionItemReviewEntry[] = [];
const EMPTY_TEMP_COMMENTS: TempPointReviewComment[] = [];

@Component({
  selector: 'app-nd-gap-analysis',
  standalone: true,
  imports: [FormsModule, RouterLink, NgTemplateOutlet, NdStatusBadgeComponent, DualVerifyResultCardComponent, NdGapPointDetailComponent, NdPointSortControlsComponent, NdRunReviewPanelComponent, NdRunHistoryPanelComponent, NdExportOptionsDialogComponent, NdReviewSummaryPanelComponent, NdReportSummaryStackComponent, NdClauseRailCardComponent],
  templateUrl: './nd-gap-analysis.component.html',
  styleUrl: './nd-gap-analysis.component.scss',
})
export class NdGapAnalysisComponent implements OnInit, OnChanges, OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly ndApi = inject(NdApiService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);
  private readonly cdr = inject(ChangeDetectorRef);
  readonly auth = inject(NdAuthService);
  private readonly pageHeaderActions = inject(NdPageHeaderActionsService);
  private readonly stepTracker = inject(NdStepTrackerService);
  private readonly pipelinePanel = inject(NdPipelinePanelService);
  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  /** Open-gap point ids queued for report-level evidence re-run — drives poll completion. */
  private evidenceRerunWatchPointIds: Set<string> | null = null;
  /** Fixed at rerun start — stable "Points selected" total. */
  evidenceRerunWatchTotal = 0;
  evidenceRerunStatusFilter: 'all' | GapEvidenceRerunUiStatus = 'all';
  private pipelinePanelActivatedHere = false;

  /** Embedded below the analyse-v8 columns: no page header, run supplied via input. */
  @Input() embedMode = false;
  @Input() embedRunId: string | null = null;
  /** Parent bumps when run results refresh (e.g. analysis just completed). */
  @Input() embedReloadToken = 0;
  /** When set, shows gap-analysis layout as maker/checker/reviewer workspace (used by review routes). */
  @Input() reviewWorkspaceMode: 'none' | 'maker' | 'checker' | 'reviewer' = 'none';
  @Output() runStatusChange = new EventEmitter<string>();

  runReviewSubmitting = false;
  runReviewError = '';

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

  activeFilter: 'all' | GapSeverity | 'with_gaps' = 'all';
  pointSort: PointSortMode = 'number';
  pointSortDir: SortDir = 'asc';
  viewMode: 'cards' | 'list' = 'list';
  selectedItemId: string | null = null;
  ndRunId: string | null = null;
  ndRunStatus = '';
  ndRunWorkflowEngine: string | null = null;

  /** "provider / model" that judged this run - shown for the hybrid V5 engine only. */
  get ndRunLlmLabel(): string {
    if (!isRegulPipelineHybridWorkflow(this.ndRunWorkflowEngine)) return '';
    const run = this.ndRunData?.run;
    const model = run?.regulLlmModel?.trim();
    if (!model) return '';
    const provider = run?.regulLlmProvider?.trim();
    return provider ? `${provider} / ${model}` : model;
  }

  /** Scope note for compliant clauses; new analysis page (V5) only, empty for every other engine. */
  get ndCompliantNote(): string {
    if (!isRegulPipelineHybridWorkflow(this.ndRunWorkflowEngine)) return '';
    const names = this.ndPolicyDocCatalog.map((d) =>
      (d.title?.trim() || d.originalFileName?.trim() || '').replace(/\.(pdf|docx?)$/i, ''),
    );
    return v5CompliantScopeNote(names);
  }

  get isNdRegulWorkflow(): boolean {
    return isRegulWorkflow(this.ndRunWorkflowEngine);
  }

  get showEvidenceRerunProgressPanel(): boolean {
    return this.reportEvidenceRerunning && !!this.evidenceRerunWatchPointIds?.size;
  }

  get evidenceRerunProgressCounts() {
    if (!this.ndRunData || !this.evidenceRerunWatchPointIds?.size) return null;
    return summarizeGapEvidenceRerunProgress(this.ndRunData.points ?? [], this.evidenceRerunWatchPointIds);
  }

  get evidenceRerunProgressRowsFiltered() {
    if (!this.ndRunData || !this.evidenceRerunWatchPointIds?.size) return [];
    const rows = gapEvidenceRerunProgressRows(this.ndRunData.points ?? [], this.evidenceRerunWatchPointIds);
    if (this.evidenceRerunStatusFilter === 'all') return rows;
    return rows.filter((r) => r.status === this.evidenceRerunStatusFilter);
  }

  get evidenceRerunPhaseHint(): string {
    const phase = (this.ndRunData?.run.regulPipelinePhase ?? '').toLowerCase();
    switch (phase) {
      case 'retrieval':
        return 'Retrieving relevant policy sections from gap evidence…';
      case 'forward':
        return 'Judging each open clause against uploaded evidence…';
      case 'parsing':
        return 'Preparing gap evidence documents…';
      default:
        return 'Workers update each open clause — counts refresh every few seconds.';
    }
  }

  readonly gapEvidenceRerunUiStatusLabel = gapEvidenceRerunUiStatusLabel;

  setEvidenceRerunStatusFilter(filter: 'all' | GapEvidenceRerunUiStatus): void {
    this.evidenceRerunStatusFilter = filter;
    this.cdr.markForCheck();
  }

  ndRunData: ResultsData | null = null;
  ndPolicyDocId: string | null = null;
  ndPolicyDocCatalog: PolicyDocCatalogEntry[] = [];
  ndRegulationDocId: string | null = null;
  ndRegulationDocName = '';
  reportEvidenceBusy = false;
  reportEvidenceRerunning = false;
  /** Header marquee: document prepare and/or gap re-analysis progress. */
  gapActivityMarquee = '';
  evidencePrepLabels: Record<string, string> = {};
  evidencePrepSteps: Record<string, GapEvidencePrepStep> = {};
  /** Per-document parse/extract pipeline when user clicks Prepare. */
  reportEvidencePreparingIds = new Set<string>();
  private evidenceRerunPollTimer: ReturnType<typeof setInterval> | null = null;
  private evidencePrepPollTimer: ReturnType<typeof setInterval> | null = null;
  ndSearchQuery = '';
  workflowLoading = false;
  editingPointId: string | null = null;
  capSavingPointId: string | null = null;
  history: ActionPlanHistoryEntry[] = [];
  historyPointId: string | null = null;
  runHistoryOpen = false;
  ndDetailError = '';
  evidenceUploadingPointId: string | null = null;
  evidenceRerunningPointId: string | null = null;
  evidenceUploadingActionIndex: number | null = null;
  evidenceRerunningActionIndex: number | null = null;
  evidenceDeletingAttachmentId: string | null = null;
  reportEvidenceDeletingId: string | null = null;
  savingActionReviewIndex: number | null = null;
  savingReviewId: string | null = null;

  readonly complianceLabels = COMPLIANCE_SEVERITY_LABELS;

  readonly filters: { id: 'all' | GapSeverity | 'with_gaps'; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'with_gaps', label: 'With gaps' },
    { id: 'compliant', label: COMPLIANCE_SEVERITY_LABELS.compliant },
    { id: 'partial_compliant', label: COMPLIANCE_SEVERITY_LABELS.partial_compliant },
    { id: 'non_compliant', label: COMPLIANCE_SEVERITY_LABELS.non_compliant },
  ];

  items: GapItemData[] = [];
  filteredItemsList: GapItemData[] = [];
  reportByPointId = new Map<string, DualVerifyReportItem>();
  private analysisPointByKey = new Map<string, AnalysisPoint>();
  private attachmentCountByPointId = new Map<string, number>();
  private attachmentsByPointId = new Map<string, PointGapAttachment[]>();
  private snapshotByPointId = new Map<string, PointSnapshot>();
  private reviewsByPointId = new Map<string, ActionItemReviewEntry[]>();
  private tempCommentsByPointId = new Map<string, TempPointReviewComment[]>();
  private actionPlansByPointId = new Map<string, ActionPlanEntry[]>();
  /** One seed attempt per component instance — the API is the real duplicate guard. */
  private actionPlanSeedAttempted = false;
  /** Action plan whose reviews are shown in the side panel. */
  reviewPanelPlan: ActionPlanEntry | null = null;
  panelReviewDraftText = '';
  panelEditingReviewId: string | null = null;
  panelReviewDraftOpen = false;
  savingPanelReview = false;
  /** Set when arriving from the overview priority drill-down; highlights the matching gaps. */
  actionPlanFocusPriority: ActionPlanPriority | null = null;
  actionPlanFocusStatus: ActionPlanStatus | null = null;
  private actionPlanFocusApplied = false;
  /** Analysis point to open on load, set by inbox links. */
  private focusPointId: string | null = null;
  /** CAP gap and action within that point, so an inbox link lands on one action card. */
  private focusGapIndex: number | null = null;
  private focusPlanId: string | null = null;

  /** Shown while finalize builds the corrected copy of the internal document. */
  finalizeProgressMessage = '';
  showExportDialog = false;
  /** Shell header export format — Excel opens the column picker; PDF exports immediately. */
  headerExportFormat: 'xlsx' | 'pdf' = 'xlsx';
  exportDialogColumns: string[] = [];
  exportDialogHasActionPlans = false;
  exportDialogHasReviews = false;
  private lastLoadKey = '';
  private loadGeneration = 0;
  private pendingLoadRunId: string | null = null;
  private pendingLoadPromise: Promise<void> | null = null;
  /** Display id of the row whose detail panel is open — at most one. */
  readonly expandedItemId = signal<string | null>(null);

  @ViewChild('findingsSection') findingsSection?: ElementRef<HTMLElement>;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    if (this.embedMode || this.reviewWorkspaceMode !== 'none') {
      this.applyActionPlanFocusFromQuery(this.route.snapshot.queryParamMap);
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

    this.route.queryParamMap
      .pipe(
        map((params) => {
          const session = params.get('session');
          const saved = params.get('saved');
          const runId = params.get('run');
          const section = params.get('section');
          const focus = params.get('focus');
          const apPriority = params.get('apPriority');
          const apStatus = params.get('apStatus');
          const point = params.get('point');
          const plan = params.get('plan');
          const gap = params.get('gap');
          this.focusPointId = point;
          this.focusPlanId = plan;
          this.focusGapIndex = gap && Number.isFinite(Number(gap)) ? Number(gap) : null;
          const riskTier = params.get('riskTier');
          this.riskTierFilter =
            riskTier === 'critical' || riskTier === 'high'
              ? 'high'
              : riskTier === 'medium' || riskTier === 'low'
                ? (riskTier as ActionPlanPriority)
                : null;
          return {
            loadKey: [session ?? '', saved ?? '', runId ?? '', section ?? '', focus ?? '', apPriority ?? '', apStatus ?? '', point ?? '', plan ?? '', gap ?? '', riskTier ?? ''].join('|'),
            filter: params.get('filter'),
            session,
            saved,
            runId,
            section,
            focus,
            apPriority,
            apStatus,
          };
        }),
        distinctUntilChanged((a, b) => a.loadKey === b.loadKey),
      )
      .subscribe(({ filter, session, saved, runId, section, focus, apPriority, apStatus, loadKey }) => {
      this.applyActionPlanFocusParams(apPriority, apStatus);
      if (filter) {
        const normalized =
          filter === 'all'
            ? 'all'
            : filter === 'with_gaps'
              ? 'with_gaps'
              : normalizeGapSeverity(filter);
        if (this.filters.some((f) => f.id === normalized)) {
          this.activeFilter = normalized;
        }
      }

      if (loadKey === this.lastLoadKey) {
        return;
      }
      this.lastLoadKey = loadKey;
      this.loadFromQuery(session, saved, section, focus, runId);
    });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.embedMode && this.reviewWorkspaceMode === 'none') return;
    const runChange = changes['embedRunId'];
    const tokenChange = changes['embedReloadToken'];
    if (
      this.embedRunId &&
      ((runChange && !runChange.firstChange) || (tokenChange && !tokenChange.firstChange))
    ) {
      this.loadFromQuery(null, null, null, null, this.embedRunId);
    }
  }

  get summary() {
    if (this.ndRunData?.points?.length) {
      return ndComplianceSummaryFromPoints(this.ndRunData.points);
    }
    return {
      compliant: this.items.filter((i) => i.severity === 'compliant').length,
      partialCompliant: this.items.filter((i) => i.severity === 'partial_compliant').length,
      nonCompliant: this.items.filter((i) => i.severity === 'non_compliant').length,
    };
  }

  get filteredItems(): GapItemData[] {
    return this.filteredItemsList;
  }

  /**
   * Risk drill-down from the overview cards. The overview counts high/medium/low gaps,
   * so opening one of those cards should land on exactly those gaps.
   */
  riskTierFilter: ActionPlanPriority | null = null;

  private itemHasGapRisk(item: GapItemData, tier: ActionPlanPriority): boolean {
    const point = this.analysisPointForGap(item);
    if (!point?.id) return false;
    return capGapsForAnalysisPoint(point, this.isNdRegulWorkflow).some((gap) => {
      const state = this.gapStates.get(gapStateKey(point.id!, gap.index));
      return (state?.risk ?? normalizeGapRisk(gap.priority)) === tier;
    });
  }

  get riskTierFilterLabel(): string {
    if (!this.riskTierFilter) return '';
    return actionPlanPriorityLabel(this.riskTierFilter);
  }

  clearRiskTierFilter(): void {
    this.riskTierFilter = null;
    this.refreshFilteredItems();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { riskTier: null },
      queryParamsHandling: 'merge',
    });
  }

  private refreshFilteredItems(): void {
    let list = this.items;
    if (this.activeFilter === 'with_gaps') {
      list = list.filter(
        (i) =>
          (i.gapCount ?? 0) > 0 ||
          i.severity === 'partial_compliant' ||
          i.severity === 'non_compliant',
      );
    } else if (this.activeFilter !== 'all') {
      list = list.filter((i) => i.severity === this.activeFilter);
    }
    // Drill-down from an overview risk card: keep only clauses carrying that risk.
    if (this.riskTierFilter) {
      list = list.filter((i) => this.itemHasGapRisk(i, this.riskTierFilter!));
    }
    const q = this.ndSearchQuery.trim().toLowerCase();
    if (q) {
      list = list.filter(
        (i) =>
          i.title.toLowerCase().includes(q) ||
          i.section.toLowerCase().includes(q) ||
          i.regulatoryText.toLowerCase().includes(q),
      );
    }
    this.filteredItemsList = sortByPointKey(
      list,
      this.pointSort,
      this.pointSortDir,
      (i) => i.section,
      (i) => i.severity,
    );
    if (this.viewMode === 'list') {
      this.ensureListSelection();
    }
  }

  onSearchQueryChange(): void {
    this.refreshFilteredItems();
    this.cdr.markForCheck();
  }

  onPointSortChange(event: { sort: 'number' | 'status'; dir: SortDir }): void {
    this.pointSort = event.sort;
    this.pointSortDir = event.dir;
    this.refreshFilteredItems();
    this.cdr.markForCheck();
  }

  severityLabel = gapSeverityLabel;

  reportItemForGap(item: GapItemData): DualVerifyReportItem | null {
    const key = item.section.replace(/^§/, '');
    return this.reportByPointId.get(key) ?? null;
  }

  /**
   * True once every gap sharing this item's clause number is compliant — a clause can have
   * several gaps (several NdAnalysisPoint rows with the same section/point number), and it
   * only reads as compliant overall when none of its siblings still have an open gap.
   */
  clauseFullyResolved(item: GapItemData): boolean {
    const siblings = this.items.filter((i) => i.section === item.section);
    if (siblings.length === 0) return false;
    return siblings.every((i) => i.severity === 'compliant');
  }

  analysisPointForGap(item: GapItemData): AnalysisPoint | null {
    if (!this.ndRunData) return null;
    // `section` is the clause/point-number key, which multiple gaps under the same clause
    // share — resolving by it alone returns whichever point happened to be indexed last.
    // `pointId` is this row's own NdAnalysisPoint id, so prefer it when present.
    if (item.pointId) {
      const byId = this.analysisPointByKey.get(item.pointId);
      if (byId) return byId;
    }
    const rawKey = item.section.replace(/^§/, '').trim();
    const candidates = [
      rawKey,
      item.section.trim(),
      rawKey ? `§${rawKey}` : '',
    ].filter(Boolean);
    for (const key of candidates) {
      const fromKey = this.analysisPointByKey.get(key);
      if (fromKey) return fromKey;
    }

    const report = this.reportByPointId.get(rawKey);
    if (report?.pointId) {
      const fromReport = this.analysisPointByKey.get(report.pointId);
      if (fromReport) return fromReport;
    }

    const titleNorm = item.title.trim().toLowerCase();
    if (titleNorm) {
      const fromTitle = this.analysisPointByKey.get(`title:${titleNorm}`);
      if (fromTitle) return fromTitle;
    }

    for (const point of this.ndRunData.points) {
      const snap = parsePointSnapshot(point.pointSnapshot);
      const num = (snap.pointNumber ?? '').trim().replace(/^§/, '');
      if (num && num === rawKey) return point;
    }
    return null;
  }

  private rebuildNdRunIndexes(data: ResultsData): void {
    this.analysisPointByKey.clear();
    this.attachmentCountByPointId.clear();
    this.attachmentsByPointId.clear();
    this.snapshotByPointId.clear();
    this.reviewsByPointId.clear();
    this.tempCommentsByPointId.clear();
    this.actionPlansByPointId.clear();

    for (const attachment of data.pointAttachments ?? []) {
      const count = this.attachmentCountByPointId.get(attachment.analysisPointId) ?? 0;
      this.attachmentCountByPointId.set(attachment.analysisPointId, count + 1);
      const list = this.attachmentsByPointId.get(attachment.analysisPointId);
      if (list) list.push(attachment);
      else this.attachmentsByPointId.set(attachment.analysisPointId, [attachment]);
    }

    for (const point of data.points) {
      const snap = parsePointSnapshot(point.pointSnapshot);
      if (point.id) this.snapshotByPointId.set(point.id, snap);
      const pid = (snap.pointNumber || point.regulationPointId || point.id || '').trim();
      if (pid) {
        this.analysisPointByKey.set(pid, point);
        const bare = pid.replace(/^§/, '');
        if (bare) this.analysisPointByKey.set(bare, point);
        if (bare && bare !== pid) this.analysisPointByKey.set(`§${bare}`, point);
      }
      if (point.id) this.analysisPointByKey.set(point.id, point);
      const title = (snap.pointTitle ?? '').trim().toLowerCase();
      if (title) this.analysisPointByKey.set(`title:${title}`, point);
    }

    for (const point of data.points) {
      if (!point.id) continue;
      this.reviewsByPointId.set(
        point.id,
        reviewsForPoint(data.actionItemReviews, point.id),
      );
      this.tempCommentsByPointId.set(
        point.id,
        tempCommentsForPoint(data.tempReviewComments ?? [], point.id),
      );
      this.actionPlansByPointId.set(
        point.id,
        actionPlansForPoint(data.actionPlans ?? [], point.id),
      );
    }

    void this.seedDefaultActionPlans(data);
    void this.syncGapRoster(data);
  }

  // ------------------------------------------------------------- gap state

  /** Risk and resolve state per gap, keyed by `${pointId}:${gapIndex}`. */
  gapStates = new Map<string, GapState>();
  private gapRosterSynced = false;

  /**
   * Gaps are parsed out of each clause's CAP text, so the API only learns which gaps
   * exist when a client tells it. Registering them is what lets a gap carry its own
   * risk and resolve state instead of the clause carrying it for all of them.
   */
  private async syncGapRoster(data: ResultsData): Promise<void> {
    if (!this.ndRunId || this.gapRosterSynced) return;
    if (!isAnalysisRunResultsReady(data.run.status)) return;
    this.gapRosterSynced = true;

    const items: { analysisPointId: string; gapIndex: number; risk: string }[] = [];
    for (const point of data.points) {
      if (!point.id) continue;
      for (const gap of capGapsForAnalysisPoint(point, this.isNdRegulWorkflow)) {
        items.push({
          analysisPointId: point.id,
          gapIndex: gap.index,
          risk: normalizeGapRisk(gap.priority),
        });
      }
    }
    if (items.length) await this.ndApi.syncRunGaps(this.ndRunId, items);
    await this.reloadGapStates();
  }

  async reloadGapStates(): Promise<void> {
    if (!this.ndRunId) return;
    const res = await this.ndApi.getRunGaps(this.ndRunId);
    if (!res.success || !res.data) return;
    this.gapStates = indexGapStates(res.data);
    this.cdr.markForCheck();
  }

  /** After any gap/action edit: apply local rollups immediately, then confirm with the server. */
  onWorkStateChanged(updatedPlan?: ActionPlanEntry): void {
    if (updatedPlan) {
      this.mergeActionPlanEntry(updatedPlan);
      this.applyLocalPointWorkState(updatedPlan.analysisPointId);
      this.syncListItemsFromPoints();
      this.cdr.markForCheck();
    }
    void this.refreshWorkStateFromServer();
  }

  async onGapStateChanged(): Promise<void> {
    this.syncListItemsFromPoints();
    this.cdr.markForCheck();
    await this.refreshWorkStateFromServer();
  }

  private async refreshWorkStateFromServer(): Promise<void> {
    await Promise.all([this.reloadGapStates(), this.reloadActionPlans(), this.reloadPointStatuses()]);
    this.syncListItemsFromPoints();
    this.cdr.markForCheck();
  }

  private mergeActionPlanEntry(updated: ActionPlanEntry): void {
    if (!this.ndRunData) return;
    const plans = [...(this.ndRunData.actionPlans ?? [])];
    const idx = plans.findIndex((p) => p.id === updated.id);
    if (idx >= 0) {
      const prev = plans[idx];
      plans[idx] = {
        ...prev,
        ...updated,
        reviews: updated.reviews ?? prev.reviews,
        reviewCount: updated.reviewCount ?? prev.reviewCount,
      };
    } else {
      plans.push(updated);
    }
    this.ndRunData = { ...this.ndRunData, actionPlans: plans };
    this.actionPlansByPointId.set(
      updated.analysisPointId,
      actionPlansForPoint(plans, updated.analysisPointId),
    );
  }

  private applyLocalPointWorkState(pointId: string): void {
    if (!this.ndRunData) return;
    const point = this.ndRunData.points.find((p) => p.id === pointId);
    if (!point) return;

    const gapIndexes = this.gapIndexesForPoint(point);
    const plans = this.ndRunData.actionPlans ?? [];
    this.gapStates = patchDerivedGapStatusesForPoint(pointId, gapIndexes, plans, this.gapStates);

    const refreshed = recomputeAutoClauseStatus(point, gapIndexes, plans, this.gapStates);
    if (refreshed !== point) {
      const points = this.ndRunData.points.map((p) => (p.id === pointId ? refreshed : p));
      this.ndRunData = { ...this.ndRunData, points };
      this.repointAnalysisPointInIndexes(refreshed);
    }
  }

  private repointAnalysisPointInIndexes(point: AnalysisPoint): void {
    if (!point.id) return;
    this.analysisPointByKey.set(point.id, point);
    const snap = this.snapshotByPointId.get(point.id) ?? parsePointSnapshot(point.pointSnapshot);
    const pid = (snap.pointNumber || point.regulationPointId || point.id || '').trim();
    if (pid) {
      this.analysisPointByKey.set(pid, point);
      const bare = pid.replace(/^§/, '');
      if (bare) this.analysisPointByKey.set(bare, point);
      if (bare && bare !== pid) this.analysisPointByKey.set(`§${bare}`, point);
    }
    const title = (snap.pointTitle ?? '').trim().toLowerCase();
    if (title) this.analysisPointByKey.set(`title:${title}`, point);
  }

  /** Keep clause rail badges in sync when finalStatus or rollups change without a full reload. */
  private syncListItemsFromPoints(): void {
    if (!this.ndRunData) return;
    let changed = false;
    for (const item of this.items) {
      const ndPoint = this.analysisPointForGap(item);
      if (!ndPoint) continue;
      const severity = resolveAnalysisPointSeverity(ndPoint);
      if (severity) {
        const next = normalizeGapSeverity(severity);
        if (item.severity !== next) {
          item.severity = next;
          changed = true;
        }
      }
    }
    if (changed) this.refreshFilteredItems();
  }

  /**
   * Refresh each point's compliance verdict after an action or gap changes. Resolving the
   * last open action on a clause's last open gap can flip the clause to compliant on the
   * server (the auto rule in NdGapStatusResolver), but reloadActionPlans/reloadGapStates
   * only touch their own slices of ndRunData — without this, the clause badge stays on its
   * old verdict until the whole page is reloaded.
   */
  private async reloadPointStatuses(): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;
    const res = await this.ndApi.getPointStatuses(this.ndRunId);
    if (!res.success || !res.data) return;

    const byId = new Map(res.data.map((p) => [p.id, p]));
    const points = this.ndRunData.points.map((p) => {
      const fresh = p.id ? byId.get(p.id) : undefined;
      if (!fresh) return p;
      return {
        ...p,
        finalStatus: fresh.finalStatus,
        finalStatusSource: fresh.finalStatusSource,
        aiFinalStatus: fresh.aiFinalStatus,
      };
    });
    this.ndRunData = { ...this.ndRunData, points };

    for (const p of points) {
      if (p.id && byId.has(p.id)) this.repointAnalysisPointInIndexes(p);
    }

    this.syncListItemsFromPoints();
    this.cdr.markForCheck();
  }

  // ------------------------------------------------------------- rollups

  /** Gap indexes a clause is showing, read from the same CAP text the cards render. */
  private gapIndexesForPoint(point: AnalysisPoint | null): number[] {
    if (!point?.id) return [];
    return capGapsForAnalysisPoint(point, this.isNdRegulWorkflow).map((g) => g.index);
  }

  /** Gap/action/review tallies for one clause, shown beside it in the clause list. */
  clauseRollupFor(item: GapItemData): ClauseRollup | null {
    const point = this.analysisPointForGap(item);
    if (!point?.id) return null;
    return rollupClause(
      point.id,
      this.gapIndexesForPoint(point),
      this.actionPlansFor(point.id),
      this.gapStates,
    );
  }

  /** Whole-report tallies, shown in the report header and on the review page. */
  get runRollup(): ClauseRollup {
    const plans = this.ndRunData?.actionPlans ?? [];
    const total = {
      gaps: 0,
      resolvedGaps: 0,
      pendingGaps: 0,
      actions: plans.length,
      resolvedActions: plans.filter((p) => normalizeActionPlanStatus(p.status) === 'resolved').length,
      pendingActions: 0,
      reviews: plans.reduce((sum, p) => sum + (p.reviewCount ?? p.reviews?.length ?? 0), 0),
    };
    total.pendingActions = total.actions - total.resolvedActions;

    for (const point of this.ndRunData?.points ?? []) {
      if (!point.id) continue;
      const rollup = rollupClause(
        point.id,
        this.gapIndexesForPoint(point),
        this.actionPlansFor(point.id),
        this.gapStates,
      );
      total.gaps += rollup.gaps;
      total.resolvedGaps += rollup.resolvedGaps;
      total.pendingGaps += rollup.pendingGaps;
    }
    return total;
  }

  /**
   * Give every gap a first-draft action the maker can edit. Tops up gaps that don't
   * carry any action yet rather than requiring the run to start with zero — gap
   * numbering can change (e.g. a regul-workflow fix landing after a run was first
   * seeded), and gaps added after the first pass would otherwise never get a draft.
   */
  private async seedDefaultActionPlans(data: ResultsData): Promise<void> {
    if (!this.ndRunId || this.actionPlanSeedAttempted) return;
    if (!isAnalysisRunResultsReady(data.run.status)) return;

    this.actionPlanSeedAttempted = true;

    const covered = new Set(
      (data.actionPlans ?? []).map((p) => `${p.analysisPointId}:${p.gapIndex ?? 0}`),
    );

    const items: SeededActionPlan[] = [];
    for (const point of data.points) {
      if (!point.id) continue;
      if (resolveAnalysisPointSeverity(point) === 'compliant') continue;
      for (const gap of capGapsForAnalysisPoint(point, this.isNdRegulWorkflow)) {
        if (covered.has(`${point.id}:${gap.index}`)) continue;
        items.push(...buildSeededActionPlansForGap(point.id, gap));
      }
    }
    if (!items.length) return;

    const res = await this.ndApi.seedActionPlans(this.ndRunId, items);
    if (res.success && res.data && res.data.seeded > 0) await this.reloadActionPlans();
  }

  actionPlansFor(pointId?: string | null): ActionPlanEntry[] {
    return (pointId && this.actionPlansByPointId.get(pointId)) || [];
  }

  /** Every role may maintain action plans on a run they can open. */
  get canEditActionPlans(): boolean {
    return Boolean(this.ndRunId && this.ndRunData);
  }

  get canReviewActionPlans(): boolean {
    const role = this.auth.getRole();
    return role === 'super_admin' || role === 'checker' || role === 'reviewer';
  }

  /** Reload just the action plans after an inline edit, without refetching the whole run. */
  async reloadActionPlans(): Promise<void> {
    if (!this.ndRunId) return;
    const res = await this.ndApi.getActionPlans(this.ndRunId);
    if (!res.success || !res.data) return;

    const plans = res.data;
    if (this.ndRunData) this.ndRunData = { ...this.ndRunData, actionPlans: plans };
    this.actionPlansByPointId.clear();
    for (const point of this.ndRunData?.points ?? []) {
      if (point.id) this.actionPlansByPointId.set(point.id, actionPlansForPoint(plans, point.id));
    }
    if (this.reviewPanelPlan) {
      this.reviewPanelPlan = plans.find((p) => p.id === this.reviewPanelPlan!.id) ?? null;
    }
    this.cdr.markForCheck();
  }

  formatDate(iso?: string | null): string {
    return formatActionPlanDate(iso);
  }

  get newAnalysisLink(): string[] {
    return ndNewAnalysisRoute();
  }

  /** True when the gap carries at least one action plan matching the priority drill-down. */
  itemHasFocusedActionPlan(item: GapItemData): boolean {
    if (!this.actionPlanFocusPriority) return false;
    const point = this.analysisPointForGap(item);
    if (!point?.id) return false;
    return this.actionPlansFor(point.id).some((plan) => {
      if (normalizeActionPlanPriority(plan.priority) !== this.actionPlanFocusPriority) return false;
      if (!this.actionPlanFocusStatus) return true;
      return normalizeActionPlanStatus(plan.status) === this.actionPlanFocusStatus;
    });
  }

  get focusedActionPlanCount(): number {
    if (!this.actionPlanFocusPriority) return 0;
    return this.items.filter((item) => this.itemHasFocusedActionPlan(item)).length;
  }

  get actionPlanFocusLabel(): string {
    return this.actionPlanFocusPriority ? actionPlanPriorityLabel(this.actionPlanFocusPriority) : '';
  }

  clearActionPlanFocus(): void {
    this.actionPlanFocusPriority = null;
    this.actionPlanFocusStatus = null;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { apPriority: null, apStatus: null },
      queryParamsHandling: 'merge',
    });
  }

  /**
   * Inbox deep links land before the 94-point rail has finished rendering, and the
   * progressive gap-count enrichment that follows (enrichGapCountsFrom) keeps shifting
   * row heights for a few frames after that — a `smooth` scroll started too early gets
   * cut short by that layout churn, landing short of the target. Poll for the row, then
   * jump straight there once things have had a moment to settle.
   */
  private scrollToFocusedActionPlanGap(itemId: string, attempt = 0): void {
    if (this.actionPlanFocusApplied) return;
    const el = document.querySelector<HTMLElement>(`[data-gap-item-id="${itemId}"]`);
    if (el) {
      this.actionPlanFocusApplied = true;
      setTimeout(() => el.scrollIntoView({ behavior: 'auto', block: 'center' }), 300);
      return;
    }
    if (attempt >= 20) {
      this.actionPlanFocusApplied = true;
      return;
    }
    setTimeout(() => this.scrollToFocusedActionPlanGap(itemId, attempt + 1), 150);
  }

  /** Deep-link targets only apply to the point the inbox link named. */
  focusGapIndexFor(pointId: string): number | null {
    return this.focusPointId === pointId ? this.focusGapIndex : null;
  }

  focusPlanIdFor(pointId: string): string | null {
    return this.focusPointId === pointId ? this.focusPlanId : null;
  }

  openActionPlanReviews(plan: ActionPlanEntry): void {
    this.reviewPanelPlan = plan;
    this.cancelPanelReview();
    this.cdr.markForCheck();
  }

  closeActionPlanReviews(): void {
    this.reviewPanelPlan = null;
    this.cancelPanelReview();
    this.cdr.markForCheck();
  }

  canMutateActionPlanReview(review: ActionPlanReviewEntry): boolean {
    if (!this.canReviewActionPlans) return false;
    if (this.auth.getRole() === 'super_admin') return true;
    const uid = this.auth.profile()?.id;
    return !!uid && review.reviewerId === uid;
  }

  startPanelReview(): void {
    this.panelReviewDraftOpen = true;
    this.panelEditingReviewId = null;
    this.panelReviewDraftText = '';
  }

  startPanelReviewEdit(reviewId: string, comment: string): void {
    this.panelReviewDraftOpen = true;
    this.panelEditingReviewId = reviewId;
    this.panelReviewDraftText = comment;
  }

  cancelPanelReview(): void {
    this.panelReviewDraftOpen = false;
    this.panelEditingReviewId = null;
    this.panelReviewDraftText = '';
  }

  async submitPanelReview(plan: ActionPlanEntry): Promise<void> {
    const comment = this.panelReviewDraftText.trim();
    if (!comment || !this.ndRunId) return;

    this.savingPanelReview = true;
    const res = this.panelEditingReviewId
      ? await this.ndApi.updateActionPlanReview(this.ndRunId, plan.id, this.panelEditingReviewId, comment)
      : await this.ndApi.addActionPlanReview(this.ndRunId, plan.id, comment);
    this.savingPanelReview = false;

    if (!res.success) {
      this.toast.show(res.message ?? 'Could not save the review.', 'error');
      this.cdr.markForCheck();
      return;
    }

    this.cancelPanelReview();
    await this.reloadActionPlans();
  }

  async removePanelReview(plan: ActionPlanEntry, reviewId: string): Promise<void> {
    if (!this.ndRunId || !confirm('Delete this review?')) return;
    this.savingPanelReview = true;
    const res = await this.ndApi.deleteActionPlanReview(this.ndRunId, plan.id, reviewId);
    this.savingPanelReview = false;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not delete the review.', 'error');
      this.cdr.markForCheck();
      return;
    }
    await this.reloadActionPlans();
  }

  private applyActionPlanFocusFromQuery(params: { get(name: string): string | null }): void {
    this.applyActionPlanFocusParams(params.get('apPriority'), params.get('apStatus'));
    // Inbox deep links (and the checker/reviewer redirect above, which forwards them)
    // carry point/plan/gap too — embed/review-workspace mode skips the full
    // queryParamMap subscription below, so it has to pick these up here instead.
    this.focusPointId = params.get('point');
    this.focusPlanId = params.get('plan');
    const gap = params.get('gap');
    this.focusGapIndex = gap && Number.isFinite(Number(gap)) ? Number(gap) : null;
  }

  private applyActionPlanFocusParams(apPriority: string | null, apStatus: string | null): void {
    this.actionPlanFocusPriority = apPriority ? normalizeActionPlanPriority(apPriority) : null;
    this.actionPlanFocusStatus = apStatus === 'pending' || apStatus === 'resolved' ? apStatus : null;
    this.actionPlanFocusApplied = false;
  }

  private itemIndex(item: GapItemData): number {
    const section = item.section.trim();
    const bySection = this.items.findIndex((i) => i.section.trim() === section);
    if (bySection >= 0) return bySection;
    return this.items.findIndex((i) => i.id === item.id);
  }

  /** Stable @for track key. */
  trackGapItem(item: GapItemData): string {
    return item.id;
  }

  isRowExpanded(item: GapItemData): boolean {
    return this.expandedItemId() === item.id;
  }

  toggleItem(item: GapItemData): void {
    const id = item.id;
    this.expandedItemId.update((current) => (current === id ? null : id));
    this.syncExpandedFlags();
    this.persistSoon();
  }

  expandGapItem(event: Event, item: GapItemData): void {
    event.stopPropagation();
    event.preventDefault();
    if (this.expandedItemId() !== item.id) {
      this.expandedItemId.set(item.id);
      this.syncExpandedFlags();
      this.persistSoon();
    }
  }

  private syncExpandedFlags(): void {
    const openId = this.expandedItemId();
    for (const row of this.items) {
      row.expanded = openId != null && row.id === openId;
    }
  }

  private applyExpandedSelection(
    items: GapItemData[],
    section: string | null,
    focus: string | null,
    overlays: Record<string, GapDraftOverlay>,
  ): void {
    let id: string | null = null;

    // Inbox deep link: open the exact gap that carries the action the user clicked.
    if (this.focusPointId) {
      const target = items.find((item) => this.analysisPointForGap(item)?.id === this.focusPointId);
      if (target) {
        this.expandedItemId.set(target.id);
        this.selectedItemId = target.id;
        this.syncExpandedFlags();
        this.scrollToFocusedActionPlanGap(target.id);
        return;
      }
    }

    if (this.actionPlanFocusPriority && !section && !focus) {
      id = items.find((item) => this.itemHasFocusedActionPlan(item))?.id ?? null;
      if (id) {
        this.expandedItemId.set(id);
        this.selectedItemId = id;
        this.syncExpandedFlags();
        this.scrollToFocusedActionPlanGap(id);
        return;
      }
    }

    if (section) {
      const key = section.trim();
      const withSection = key.startsWith('§') ? key : `§${key}`;
      id =
        items.find(
          (item) =>
            item.id === key ||
            item.section.trim() === key ||
            item.section.trim() === withSection,
        )?.id ?? null;
    } else if (focus) {
      const f = focus.toLowerCase();
      id =
        items.find(
          (item) =>
            item.title.toLowerCase().includes(f) || item.section.toLowerCase().includes(f),
        )?.id ?? null;
    } else {
      id =
        items.find((item) => {
          const overlayKey = item.section.replace(/^§/, '');
          return overlays[overlayKey]?.expanded;
        })?.id ?? null;
    }

    this.expandedItemId.set(id);
    this.syncExpandedFlags();
  }

  gapCountForItem(item: GapItemData): number {
    if (item.gapCount != null) return item.gapCount;
    const ndPoint = this.analysisPointForGap(item);
    if (ndPoint) {
      return countDisplayGapsForAnalysisPoint(
        ndPoint,
        this.attachmentCountByPointId.get(ndPoint.id) ?? 0,
      );
    }
    return 0;
  }

  gapPointDisplayNum(item: GapItemData): string {
    return item.section.replace(/^§/, '').trim();
  }

  /** Standard clause card fields for list rail and card view (shared layout with analyse-regul). */
  gapClauseCardModel(item: GapItemData): {
    clauseNum: string;
    heading: string;
    titleLine: string;
    clauseText: string;
    confidence: string;
  } {
    const meta = this.gapPointRailMeta(item);
    const ndPoint = this.analysisPointForGap(item);
    const snap = ndPoint ? this.snapshotForPoint(ndPoint) : null;
    const titleCandidate = (item.title ?? '').trim() || (snap?.pointTitle ?? '').trim();
    let rawText = (item.regulatoryText ?? '').trim();
    if (!rawText && snap) {
      rawText = (snap.pointContent ?? snap.pointTitle ?? '').trim();
    }
    const fields = buildClauseRailCardFields({
      clauseNum: this.gapPointDisplayNum(item),
      title: titleCandidate,
      clauseText: rawText,
    });
    return {
      clauseNum: fields.clauseNum,
      heading: fields.heading,
      titleLine: fields.titleLine,
      clauseText: fields.clauseText,
      confidence: meta.confidence,
    };
  }

  gapPointRailMeta(item: GapItemData): { policySnippet: string; confidence: string } {
    const ndPoint = this.analysisPointForGap(item);
    if (ndPoint) {
      const snap = this.snapshotForPoint(ndPoint);
      return {
        policySnippet: policySnippetFromAnalysisPoint(ndPoint, snap.pointContent),
        confidence: resolveDisplayConfidence(ndPoint),
      };
    }
    const legacy = this.listRowMeta(item);
    const policySnippet =
      legacy.policySnippet &&
      legacy.policySnippet !== '—' &&
      legacy.policySnippet !== '(See detail panel)'
        ? legacy.policySnippet
        : '—';
    return { policySnippet, confidence: legacy.confidence };
  }

  openGapPointInfo(event: Event, item: GapItemData): void {
    event.stopPropagation();
    this.openPdf('reg', item);
  }

  attachmentsForPoint(pointId: string): PointGapAttachment[] {
    return this.attachmentsByPointId.get(pointId) ?? EMPTY_GAP_ATTACHMENTS;
  }

  private pointHasGapEvidence(pointId: string, actionIndex?: number): boolean {
    return this.attachmentsForPoint(pointId).some((att) =>
      att.actionIndex == null || (actionIndex != null && att.actionIndex === actionIndex),
    );
  }

  get reportGapAttachments(): PointGapAttachment[] {
    const seen = new Set<string>();
    const out: PointGapAttachment[] = [];
    for (const list of this.attachmentsByPointId.values()) {
      for (const att of list) {
        if (att.actionIndex != null) continue;
        if (seen.has(att.storedDocumentId)) continue;
        seen.add(att.storedDocumentId);
        out.push(att);
      }
    }
    return out;
  }

  get runReviewInitialDraft(): Partial<RunReviewDraft> | null {
    const reviews = this.ndRunData?.reviews ?? [];
    if (!reviews.length) return null;
    const last = reviews[reviews.length - 1];
    return {
      status: (last.reviewStatus as RunReviewDraft['status']) || 'pending',
      priority: typeof last.priority === 'number' ? last.priority : 50,
      responsibility: last.responsibility ?? '',
      dueDate: toDateInputValue(last.dueDate),
      comment: last.overallComment ?? '',
    };
  }

  savedReviewsForPoint(pointId: string): ActionItemReviewEntry[] {
    return this.reviewsByPointId.get(pointId) ?? EMPTY_ACTION_REVIEWS;
  }

  savedTempCommentsForPoint(pointId: string): TempPointReviewComment[] {
    return this.tempCommentsByPointId.get(pointId) ?? EMPTY_TEMP_COMMENTS;
  }

  get canEditTempReviewComments(): boolean {
    const role = this.auth.getRole();
    return role === 'super_admin' || role === 'checker' || role === 'reviewer' || role === 'maker';
  }

  onTempReviewCommentsChanged(event: TempReviewCommentsChangeEvent): void {
    this.tempCommentsByPointId.set(event.analysisPointId, event.comments);
    this.cdr.markForCheck();
  }

  get canReviewActionGaps(): boolean {
    const submit = this.runReviewSubmitMode;
    if (submit === 'checker' || submit === 'reviewer') return true;
    return canAddActionItemReviews(this.auth.getRole(), this.ndRunData?.run.status);
  }

  /** Run workflow phase — identical on every URL and for every role. */
  get runReviewPhase(): RunReviewPanelMode {
    return runReviewPhaseFromStatus(this.ndRunData?.run.status);
  }

  /** @deprecated Use runReviewPhase — same value, status-only (ignores review workspace route). */
  get effectiveReviewMode(): RunReviewPanelMode {
    return this.runReviewPhase;
  }

  /** Report submit panel mode for the signed-in viewer (super_admin uses the current phase). */
  get runReviewSubmitMode(): RunReviewPanelMode {
    return runReviewSubmitModeForViewer(this.runReviewPhase, this.auth.getRole());
  }

  /**
   * Page chrome for ND runs opened from All analysis (`?run=`) — matches checker/reviewer/correction
   * review routes without requiring a different URL.
   */
  get reportPageTitleMode(): RunReviewPanelMode {
    if (this.reviewWorkspaceMode !== 'none') return this.reviewWorkspaceMode;
    if (!this.ndRunId || !this.ndRunData) return 'none';
    const phase = this.runReviewPhase;
    if (phase === 'checker' || phase === 'reviewer') return phase;
    if (phase === 'maker' && this.ndRunData.run.status === 'pulled_back') return 'maker';
    return 'none';
  }

  /** Saved ND run opened as a full report (All analysis row, review routes, etc.). */
  get unifiedNdRunReport(): boolean {
    return Boolean(this.ndRunId && !this.embedMode);
  }

  /** Shell blue bar title — same labels as Pending review / Pending final review routes. */
  get shellReportTitle(): string | null {
    if (this.embedMode || !this.ndRunId) return null;
    switch (this.reportPageTitleMode) {
      case 'checker':
        return 'Pending review';
      case 'reviewer':
        return 'Pending final review';
      case 'maker':
        return 'Pending correction';
      default:
        break;
    }
    if (!this.unifiedNdRunReport) return null;
    const status = this.ndRunData?.run.status ?? '';
    if (status === 'reviewer_approved') return 'Finalized';
    return 'Analysis report';
  }

  /** Hide in-content h1 when the shell shows the report title (reviewer-style layout). */
  get hideDuplicateReportTitle(): boolean {
    if (this.embedMode) return false;
    return this.unifiedNdRunReport || this.reviewWorkspaceMode !== 'none';
  }

  get showWorkflowBar(): boolean {
    return false;
  }

  /** Label for the phase badge on the report header. */
  get reportPhaseLabel(): string {
    switch (this.runReviewPhase) {
      case 'checker':
        return 'In review — with checker';
      case 'reviewer':
        return 'Final review — with reviewer';
      case 'maker':
        return this.ndRunData?.run.status === 'reviewer_approved'
          ? 'Finalised'
          : 'Draft — with maker';
      default:
        return '';
    }
  }

  /** Upload, list, and rerun-all on every ND analysis run (same block as review workspaces). */
  get showGapAnalysisDocumentsPanel(): boolean {
    return Boolean(this.ndRunId);
  }

  /** Super admin: merged send/pullback targets for the current run status. */
  get superAdminWorkflowTargets(): WorkflowSubmitTarget[] {
    return workflowSubmitTargetsForViewer(
      this.runReviewPhase,
      this.auth.getRole(),
      this.ndRunData?.run.status,
    );
  }

  get canFinalizeWorkflowRunFlag(): boolean {
    return canFinalizeWorkflowRun(this.auth.getRole(), this.ndRunData?.run.status);
  }

  /** Panel mode — super admin uses the run's workflow phase when the form is shown. */
  get runReviewPanelMode(): RunReviewPanelMode {
    if (!this.showRunReviewForm) return 'maker';
    if (this.auth.getRole() === 'super_admin') {
      const phase = this.runReviewPhase;
      return phase !== 'none' ? phase : this.runReviewSubmitMode;
    }
    return this.runReviewSubmitMode;
  }

  /** Report-level review form when this viewer may submit at the current phase. */
  get showRunReviewForm(): boolean {
    if (this.auth.getRole() === 'super_admin' && this.ndRunId) {
      return this.superAdminWorkflowTargets.length > 0 || this.canFinalizeWorkflowRunFlag;
    }
    return this.runReviewSubmitMode !== 'none';
  }

  /** @deprecated Use showGapAnalysisDocumentsPanel / showRunReviewForm. */
  get showRunReviewPanel(): boolean {
    return this.showGapAnalysisDocumentsPanel && this.showRunReviewForm;
  }

  /**
   * Review summary block (gap/action stats + open actions) — same sequence as Pending final review
   * for every run opened from All analysis (`?run=`), not only checker/reviewer phases.
   */
  get showReviewSummary(): boolean {
    if (!this.ndRunData) return false;
    if (this.embedMode) return false;
    if (this.reviewWorkspaceMode !== 'none') {
      const phase = this.runReviewPhase;
      return phase === 'checker' || phase === 'reviewer';
    }
    return this.unifiedNdRunReport;
  }

  get allActionPlans(): ActionPlanEntry[] {
    return this.ndRunData?.actionPlans ?? [];
  }

  get clauseByPointId(): Map<string, string> {
    const map = new Map<string, string>();
    for (const point of this.ndRunData?.points ?? []) {
      if (!point.id) continue;
      const snap = this.snapshotForPoint(point);
      map.set(point.id, (snap.pointNumber ?? '').replace(/^§/, '').trim());
    }
    return map;
  }

  get reviewProgress(): { total: number; reviewed: number } | null {
    if (!this.ndRunData?.points.length) return null;
    const counts = attachmentCountsByPoint(this.ndRunData);
    return countSavedReviewProgress(
      this.ndRunData.points,
      this.ndRunData.actionItemReviews,
      counts,
      this.ndRunData.actionPlans,
    );
  }

  get reviewWorkspaceBackLink(): string[] | null {
    if (this.reviewWorkspaceMode === 'checker') return ['/nd/checker'];
    if (this.reviewWorkspaceMode === 'reviewer') return ['/nd/reviewer'];
    if (this.reviewWorkspaceMode === 'maker') return ['/nd/analysis-runs'];
    return null;
  }

  get reviewWorkspaceBackQueryParams(): Record<string, string> | null {
    if (this.reviewWorkspaceMode !== 'maker') return null;
    return this.auth.getRole() === 'maker' ? { mine: '1', correction: '1' } : { correction: '1' };
  }

  get hideHeaderSubmitForReview(): boolean {
    return this.showRunReviewForm && this.runReviewPanelMode === 'maker';
  }

  get canShowReviewPanel(): boolean {
    return isReviewRole(this.auth.getRole());
  }

  get gapReviewDisabledHint(): string {
    return reviewDisabledHint(this.auth.getRole(), this.ndRunData?.run.status);
  }

  get reviewWorkspaceRoute(): string[] | null {
    if (!this.ndRunId) return null;
    return reviewWorkspaceLink(this.auth.getRole(), this.ndRunId, this.ndRunData?.run.status);
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
    if (!this.ndRunId) return;
    this.savingActionReviewIndex = event.actionIndex;
    this.savingReviewId = event.reviewId ?? null;
    this.ndDetailError = '';
    const body = {
      status: event.status,
      comment: event.comment.trim() || undefined,
      responsibility: event.responsibility.trim() || undefined,
      dueDate: event.dueDate.trim() || undefined,
      priority: event.priority || undefined,
    };
    const res = event.reviewId
      ? await this.ndApi.updateActionItemReview(this.ndRunId, event.reviewId, body)
      : await this.ndApi.saveActionItemReview(this.ndRunId, {
          analysisPointId: pointId,
          actionIndex: event.actionIndex,
          ...body,
        });
    this.savingActionReviewIndex = null;
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show(event.reviewId ? 'Review updated' : 'Review saved', 'success');
      const row = res.data as ActionItemReviewEntry | undefined;
      if (row?.id) {
        this.upsertActionItemReview(row);
      } else {
        await this.reloadActionItemReviews();
      }
    } else {
      this.ndDetailError = res.message ?? 'Could not save review';
      this.toast.show(this.ndDetailError, 'error');
    }
  }

  async reorderActionItemReview(
    pointId: string,
    event: { reviewId: string; actionIndex: number; direction: 'up' | 'down' },
  ): Promise<void> {
    if (!this.ndRunId) return;
    this.savingReviewId = event.reviewId;
    this.savingActionReviewIndex = event.actionIndex;
    this.ndDetailError = '';
    const res = await this.ndApi.reorderActionItemReview(this.ndRunId, event.reviewId, event.direction);
    this.savingReviewId = null;
    this.savingActionReviewIndex = null;
    if (res.success) {
      await this.reloadActionItemReviews();
    } else {
      this.ndDetailError = res.message ?? 'Could not reorder review';
      this.toast.show(this.ndDetailError, 'error');
    }
    void pointId;
  }

  get canUploadGapEvidence(): boolean {
    return canUploadGapEvidenceForRun(this.auth.getRole(), this.ndRunData?.run.status);
  }

  get gapEvidenceUploadDisabledHint(): string {
    return gapEvidenceUploadDisabledHint(this.auth.getRole(), this.ndRunData?.run.status);
  }

  async onUploadGapEvidence(pointId: string, fileList: FileList, actionIndex?: number): Promise<void> {
    if (!this.ndRunId || !fileList.length) return;
    this.evidenceUploadingPointId = pointId;
    this.evidenceUploadingActionIndex = actionIndex ?? null;
    this.ndDetailError = '';
    const files = Array.from(fileList);
    const res = await this.ndApi.uploadPointGapAttachments(
      this.ndRunId,
      pointId,
      files,
      actionIndex,
    );
    this.evidenceUploadingPointId = null;
    this.evidenceUploadingActionIndex = null;
    if (res.success) {
      this.mergePointAttachments(pointId, res.data ?? [], actionIndex);
      this.toast.show(`Uploaded ${files.length} file(s) — parsing…`, 'success');
      const docIds = [...new Set((res.data ?? []).map((a) => a.storedDocumentId).filter(Boolean))];
      void this.prepareGapEvidenceDocs(docIds);
    } else {
      this.ndDetailError = res.message ?? 'Upload failed';
      this.toast.show(this.ndDetailError, 'error');
    }
  }

  private fileNameForStoredDoc(storedDocumentId: string): string | undefined {
    for (const list of this.attachmentsByPointId.values()) {
      const hit = list.find((a) => a.storedDocumentId === storedDocumentId);
      if (hit?.fileName) return hit.fileName;
    }
    return this.reportGapAttachments.find((a) => a.storedDocumentId === storedDocumentId)?.fileName;
  }

  private stopEvidencePrepPolling(): void {
    if (this.evidencePrepPollTimer) {
      clearInterval(this.evidencePrepPollTimer);
      this.evidencePrepPollTimer = null;
    }
  }

  private startEvidencePrepPolling(): void {
    if (this.evidencePrepPollTimer) return;
    this.evidencePrepPollTimer = setInterval(
      () => void this.refreshEvidencePrepLabelsForReportDocs(),
      2500,
    );
  }

  /** Load Azure parse / extract / index labels for attachments already on the report. */
  private async refreshEvidencePrepLabelsForReportDocs(): Promise<void> {
    const ids = [
      ...new Set(this.reportGapAttachments.map((a) => a.storedDocumentId).filter(Boolean)),
    ];
    if (!ids.length) {
      this.stopEvidencePrepPolling();
      return;
    }

    const res = await this.ndApi.localExtractStatusBatch(ids, GAP_EVIDENCE_LOCAL_ENGINE, {
      lite: true,
    });

    const nextLabels: Record<string, string> = { ...this.evidencePrepLabels };
    const nextSteps: Record<string, GapEvidencePrepStep> = { ...this.evidencePrepSteps };
    let anyInProgress = false;

    for (const id of ids) {
      if (this.reportEvidencePreparingIds.has(id)) {
        anyInProgress = true;
        continue;
      }
      const fileName = this.fileNameForStoredDoc(id);
      const row = res.success ? res.data?.[id] : undefined;
      let step: GapEvidencePrepStep;
      let detail: string | undefined;
      if (row) {
        step = gapEvidencePrepStepFromLocalRow(row);
        detail =
          step === 'not_started'
            ? gapEvidenceNotStartedDetail(row)
            : gapEvidencePrepDetailFromLocalRow(row, step);
      } else {
        const att = this.reportGapAttachments.find((a) => a.storedDocumentId === id);
        const ps = (att?.parseStatus ?? '').trim().toLowerCase();
        if (ps === 'failed') {
          step = 'failed';
        } else if (ps === 'parsed' || ps === 'completed') {
          const ex = (att?.sectionExtractStatus ?? '').trim().toLowerCase();
          if (ex === 'extracted' || ex === 'completed') step = 'ready';
          else if (ex === 'failed') step = 'failed';
          else step = 'extracting';
        } else if (ps === 'processing' || ps === 'pending') {
          step = 'parsing';
          detail = ps;
        } else {
          step = 'not_started';
          detail = gapEvidenceNotStartedDetail(undefined);
        }
      }
      if (gapEvidencePrepInProgress(step)) anyInProgress = true;
      nextSteps[id] = step;
      nextLabels[id] = gapEvidencePrepStepLabel(step, fileName, detail);
    }

    this.evidencePrepLabels = nextLabels;
    this.evidencePrepSteps = nextSteps;
    this.syncGapActivityMarquee();
    if (anyInProgress) this.startEvidencePrepPolling();
    else this.stopEvidencePrepPolling();
    this.cdr.markForCheck();
  }

  private setEvidencePrepStep(
    storedDocumentId: string,
    step: GapEvidencePrepStep,
    detail?: string,
  ): void {
    const fileName = this.fileNameForStoredDoc(storedDocumentId);
    this.evidencePrepSteps = { ...this.evidencePrepSteps, [storedDocumentId]: step };
    this.evidencePrepLabels = {
      ...this.evidencePrepLabels,
      [storedDocumentId]: gapEvidencePrepStepLabel(step, fileName, detail),
    };
    this.syncGapActivityMarquee();
    this.cdr.markForCheck();
  }

  onPrepareReportEvidence(storedDocumentId: string): void {
    if (!storedDocumentId || this.reportEvidencePreparingIds.has(storedDocumentId)) return;
    void this.prepareGapEvidenceDocs([storedDocumentId]);
  }

  isReportEvidencePreparing(storedDocumentId: string): boolean {
    return this.reportEvidencePreparingIds.has(storedDocumentId);
  }

  private syncGapActivityMarquee(): void {
    const prep = buildGapEvidencePrepMarquee(this.evidencePrepLabels);
    const rerun =
      this.reportEvidenceRerunning && this.ndRunData
        ? buildGapEvidenceRerunMarquee(
            this.ndRunData.run,
            this.ndRunData.points ?? [],
            this.evidenceRerunWatchPointIds,
          )
        : this.reportEvidenceRerunning
          ? 'Gap evidence re-analysis starting…'
          : null;
    this.gapActivityMarquee = mergeActivityMarquee(prep, rerun);
    this.syncEvidenceWorkflowChrome();
    this.syncShellPageHeader();
  }

  private evidenceWorkflowActive(): boolean {
    const prepBusy = Object.values(this.evidencePrepLabels).some(
      (l) => l && !/ready for gap re-analysis/i.test(l),
    );
    return this.reportEvidenceBusy || this.reportEvidenceRerunning || prepBusy;
  }

  private computeEvidenceTrackerSteps(): NdStep[] {
    const defs = [
      'Upload gap documents',
      'Azure parse & structural extract',
      'Index for retrieval',
      'Retrieve policy sections',
      'Re-judge open gaps',
      'Complete',
    ];
    const prepBusy = Object.values(this.evidencePrepLabels).some(
      (l) => l && !/ready for gap re-analysis/i.test(l),
    );
    const phase = (this.ndRunData?.run.regulPipelinePhase ?? '').toLowerCase();
    const runSt = (this.ndRunData?.run.status ?? '').toLowerCase();

    let current = 0;
    if (this.reportEvidenceRerunning) {
      if (phase === 'retrieval') current = 3;
      else if (phase === 'forward' || runSt === 'running') current = 4;
      else if (this.isEvidenceRerunInFlight()) current = 4;
      else current = 5;
    } else if (prepBusy || this.reportEvidenceBusy) {
      const labels = Object.values(this.evidencePrepLabels).join(' ').toLowerCase();
      if (/index|search index/.test(labels)) current = 2;
      else if (/structural|extract/.test(labels)) current = 1;
      else if (/azure|pars/.test(labels)) current = 1;
      else current = 0;
    } else {
      current = defs.length;
    }

    return defs.map((label, i) => ({
      label,
      state: i < current ? 'done' : i === current ? 'active' : 'pending',
    }));
  }

  private syncEvidenceWorkflowChrome(): void {
    if (this.embedMode) return;

    const active = this.evidenceWorkflowActive();
    if (active) {
      this.stepTracker.activate();
      this.stepTracker.setSteps(this.computeEvidenceTrackerSteps());
    } else {
      this.stepTracker.deactivate();
    }

    const showPanel =
      this.auth.canManageWorkspaces() && isRegulPipelineHybridWorkflow(this.ndRunWorkflowEngine);
    if (!showPanel) return;

    if (!active) {
      if (this.pipelinePanelActivatedHere) {
        this.pipelinePanel.deactivate();
        this.pipelinePanelActivatedHere = false;
      }
      return;
    }

    if (!this.pipelinePanelActivatedHere) {
      this.pipelinePanel.activate();
      this.pipelinePanelActivatedHere = true;
    }
    const docs = this.reportGapAttachments.map((a) => ({
      id: a.storedDocumentId,
      name: a.fileName,
    }));
    this.pipelinePanel.setDocs(docs);
    this.pipelinePanel.setRunActive(this.reportEvidenceRerunning);
    const prepBusy = Object.values(this.evidencePrepLabels).some(
      (l) => l && !/ready for gap re-analysis/i.test(l),
    );
    this.pipelinePanel.setPhase(
      this.ndRunData?.run.regulPipelinePhase ??
        (this.reportEvidenceRerunning ? 'forward' : prepBusy ? 'parsing' : null),
    );
  }

  private openGapPointIds(): string[] {
    return (this.ndRunData?.points ?? [])
      .filter((p) => {
        if (!p.id) return false;
        const fs = (p.finalStatus ?? '').toLowerCase();
        return fs === 'non_compliant' || fs === 'partial_compliant';
      })
      .map((p) => p.id!);
  }

  private isEvidenceRerunInFlight(): boolean {
    if (!this.ndRunData) return this.reportEvidenceRerunning;
    return isGapEvidenceRerunInFlight(
      this.ndRunData.run,
      this.ndRunData.points ?? [],
      this.evidenceRerunWatchPointIds,
    );
  }

  /** Export + history on the shell title bar; activity ticker in the header marquee. */
  private syncShellPageHeader(): void {
    this.pageHeaderActions.setMarquee(this.gapActivityMarquee?.trim() || null);
    if (this.embedMode || !this.ndRunId) {
      this.pageHeaderActions.clearActions();
      this.pageHeaderActions.setTitleOverride(null);
      return;
    }
    this.pageHeaderActions.setTitleOverride(this.shellReportTitle);
    this.pageHeaderActions.set({
      export: {
        label: this.exporting ? 'Exporting…' : 'Export',
        disabled: this.exporting || !this.items.length,
        run: () => this.openExportDialog(),
      },
      history: {
        run: () => this.openRunHistory(),
      },
    });
  }

  private openExportDialog(): void {
    this.exportXlsx();
  }

  private stopEvidenceRerunPolling(): void {
    if (this.evidenceRerunPollTimer) {
      clearInterval(this.evidenceRerunPollTimer);
      this.evidenceRerunPollTimer = null;
    }
  }

  private startEvidenceRerunPolling(watchPointIds?: string[]): void {
    this.stopEvidenceRerunPolling();
    if (watchPointIds?.length) {
      this.evidenceRerunWatchPointIds = new Set(watchPointIds);
    } else if (!this.evidenceRerunWatchPointIds?.size) {
      this.evidenceRerunWatchPointIds = new Set(this.openGapPointIds());
    }
    this.evidenceRerunWatchTotal = this.evidenceRerunWatchPointIds?.size ?? 0;
    this.evidenceRerunStatusFilter = 'all';
    this.reportEvidenceRerunning = true;
    this.syncGapActivityMarquee();
    this.evidenceRerunPollTimer = setInterval(() => void this.tickEvidenceRerunPoll(), 2000);
    void this.tickEvidenceRerunPoll();
  }

  private async tickEvidenceRerunPoll(): Promise<void> {
    if (!this.ndRunId) return;
    await this.loadNdRun(this.ndRunId, null, null);
    this.syncGapActivityMarquee();
    if (this.isEvidenceRerunInFlight()) {
      this.cdr.markForCheck();
      return;
    }
    this.reportEvidenceRerunning = false;
    this.evidenceRerunWatchPointIds = null;
    this.evidenceRerunWatchTotal = 0;
    this.stopEvidenceRerunPolling();
    this.syncGapActivityMarquee();
    this.toast.show('Gap evidence re-analysis complete', 'success');
    this.cdr.markForCheck();
  }

  private async prepareGapEvidenceDocs(storedDocumentIds: string[]): Promise<void> {
    if (!storedDocumentIds.length) return;
    const onlyReport = storedDocumentIds.every((id) =>
      this.reportGapAttachments.some((a) => a.storedDocumentId === id),
    );
    if (onlyReport && storedDocumentIds.length === this.reportGapAttachments.length) {
      this.reportEvidenceBusy = true;
    }
    for (const id of storedDocumentIds) {
      this.reportEvidencePreparingIds.add(id);
    }
    this.cdr.markForCheck();
    try {
      for (const id of storedDocumentIds) {
        this.setEvidencePrepStep(id, 'parsing');
        this.startEvidencePrepPolling();
        const result = await runGapEvidenceLocalPipeline(this.ndApi, id, (step, detail) => {
          this.setEvidencePrepStep(id, step, detail);
        });
        if (!result.ok) {
          this.setEvidencePrepStep(id, 'failed');
          this.toast.show(result.message, 'error');
        }
      }
      if (this.ndRunId) {
        await this.loadNdRun(this.ndRunId, null, null);
      } else {
        await this.refreshEvidencePrepLabelsForReportDocs();
      }
    } finally {
      for (const id of storedDocumentIds) {
        this.reportEvidencePreparingIds.delete(id);
      }
      this.reportEvidenceBusy = false;
      this.syncGapActivityMarquee();
      this.cdr.markForCheck();
    }
  }

  get canRerunReportWithEvidence(): boolean {
    return this.canUploadGapEvidence;
  }

  async onRerunAllGaps(): Promise<void> {
    if (!this.ndRunId || this.reportEvidenceRerunning) return;
    this.ndDetailError = '';
    const watchIds = this.openGapPointIds();
    if (!watchIds.length) {
      this.toast.show('No open gaps to re-run on this report.', 'error');
      return;
    }
    this.evidenceRerunWatchPointIds = new Set(watchIds);
    this.gapActivityMarquee = 'Gap evidence re-analysis starting…';
    this.cdr.markForCheck();
    try {
      const res = await this.ndApi.rerunRunWithEvidence(this.ndRunId);
      if (res.success) {
        const queued = res.data?.queued ?? watchIds.length;
        if (queued === 0) {
          this.evidenceRerunWatchPointIds = null;
          this.gapActivityMarquee = '';
          this.toast.show(res.message ?? 'No open gaps to re-run', 'error');
          return;
        }
        this.toast.show(res.message ?? 'Rerunning analysis for all gaps…', 'success');
        this.startEvidenceRerunPolling(watchIds);
      } else {
        this.ndDetailError = res.message ?? 'Rerun failed';
        this.toast.show(this.ndDetailError, 'error');
        this.gapActivityMarquee = '';
      }
    } catch {
      this.gapActivityMarquee = '';
      this.reportEvidenceRerunning = false;
    } finally {
      this.cdr.markForCheck();
    }
  }

  async onReportEvidenceSelected(filesOrEvent: FileList | Event): Promise<void> {
    const files = filesOrEvent instanceof FileList
      ? Array.from(filesOrEvent)
      : Array.from((filesOrEvent.target as HTMLInputElement).files ?? []);
    if (!files.length || !this.ndRunId) return;

    this.reportEvidenceBusy = true;
    this.cdr.markForCheck();
    try {
      const upload = await this.ndApi.uploadRunGapEvidence(this.ndRunId, files);
      if (!upload.success) {
        this.toast.show(upload.message ?? 'Upload failed', 'error');
        this.reportEvidenceBusy = false;
        return;
      }
      for (const item of upload.data ?? []) {
        for (const att of item.attachments ?? []) {
          this.mergePointAttachments(att.analysisPointId, [
            {
              ...att,
              parseStatus: att.parseStatus ?? item.parseStatus,
              sizeBytes: att.sizeBytes ?? item.sizeBytes,
            },
          ]);
        }
      }
      const docIds = [...new Set((upload.data ?? []).map((item) => item.storedDocumentId).filter(Boolean))];
      for (const id of docIds) {
        const name = files.find((_, i) => upload.data?.[i]?.storedDocumentId === id)?.name;
        this.evidencePrepLabels = {
          ...this.evidencePrepLabels,
          [id]: gapEvidencePrepStepLabel('uploading', name ?? this.fileNameForStoredDoc(id)),
        };
      }
      this.syncGapActivityMarquee();
      this.toast.show(`Uploaded ${files.length} file(s) — preparing documents…`, 'success');
      void this.prepareGapEvidenceDocs(docIds);
    } catch {
      this.reportEvidenceBusy = false;
    } finally {
      this.cdr.markForCheck();
    }
  }

  async onDeleteGapEvidence(pointId: string, attachmentId: string): Promise<void> {
    if (!this.ndRunId) return;
    this.evidenceDeletingAttachmentId = attachmentId;
    this.cdr.markForCheck();
    try {
      const res = await this.ndApi.deletePointGapAttachment(this.ndRunId, pointId, attachmentId);
      if (res.success) {
        this.removePointAttachment(pointId, attachmentId);
      } else {
        this.toast.show(res.message ?? 'Could not remove file', 'error');
      }
    } finally {
      this.evidenceDeletingAttachmentId = null;
      this.cdr.markForCheck();
    }
  }

  async onDeleteReportEvidence(storedDocumentId: string): Promise<void> {
    if (!this.ndRunId) return;
    this.reportEvidenceDeletingId = storedDocumentId;
    this.cdr.markForCheck();
    try {
      const res = await this.ndApi.deleteRunGapEvidence(this.ndRunId, storedDocumentId);
      if (res.success) {
        this.removeReportAttachment(storedDocumentId);
      } else {
        this.toast.show(res.message ?? 'Could not remove file', 'error');
      }
    } finally {
      this.reportEvidenceDeletingId = null;
      this.cdr.markForCheck();
    }
  }

  private mergePointAttachments(
    pointId: string,
    uploaded: PointGapAttachment[],
    fallbackActionIndex?: number,
  ): void {
    if (!uploaded.length) return;
    const mapped = uploaded.map((att) => ({
      ...att,
      analysisPointId: att.analysisPointId || pointId,
      actionIndex: att.actionIndex ?? fallbackActionIndex ?? null,
      createdAt: att.createdAt || new Date().toISOString(),
    }));
    const prev = this.attachmentsByPointId.get(pointId) ?? [];
    const ids = new Set(prev.map((a) => a.id));
    const next = [...prev, ...mapped.filter((a) => a.id && !ids.has(a.id))];
    this.attachmentsByPointId.set(pointId, next);
    if (this.ndRunData) {
      const existing = this.ndRunData.pointAttachments ?? [];
      const existingIds = new Set(existing.map((a) => a.id));
      this.ndRunData = {
        ...this.ndRunData,
        pointAttachments: [...existing, ...mapped.filter((a) => a.id && !existingIds.has(a.id))],
      };
    }
    this.cdr.markForCheck();
  }

  private removePointAttachment(pointId: string, attachmentId: string): void {
    const prev = this.attachmentsByPointId.get(pointId) ?? [];
    this.attachmentsByPointId.set(
      pointId,
      prev.filter((a) => a.id !== attachmentId),
    );
    if (this.ndRunData?.pointAttachments) {
      this.ndRunData = {
        ...this.ndRunData,
        pointAttachments: this.ndRunData.pointAttachments.filter((a) => a.id !== attachmentId),
      };
    }
    this.cdr.markForCheck();
  }

  private removeReportAttachment(storedDocumentId: string): void {
    for (const [pointId, list] of this.attachmentsByPointId) {
      this.attachmentsByPointId.set(
        pointId,
        list.filter((a) => a.storedDocumentId !== storedDocumentId),
      );
    }
    if (this.ndRunData?.pointAttachments) {
      this.ndRunData = {
        ...this.ndRunData,
        pointAttachments: this.ndRunData.pointAttachments.filter((a) => a.storedDocumentId !== storedDocumentId),
      };
    }
    this.cdr.markForCheck();
  }

  async onRerunWithEvidence(pointId: string, mode: 'full' | 'dual'): Promise<void> {
    await this.rerunGapEvidenceInternal(pointId, mode);
  }

  async onRerunGapEvidence(
    pointId: string,
    payload: { actionIndex: number; mode: 'full' | 'dual' },
  ): Promise<void> {
    await this.rerunGapEvidenceInternal(pointId, payload.mode, payload.actionIndex);
  }

  private async rerunGapEvidenceInternal(
    pointId: string,
    mode: 'full' | 'dual',
    actionIndex?: number,
  ): Promise<void> {
    if (!this.ndRunId) return;
    this.evidenceRerunningPointId = pointId;
    this.evidenceRerunningActionIndex = actionIndex ?? null;
    this.ndDetailError = '';
    this.cdr.markForCheck();
    const hasEvidence = this.pointHasGapEvidence(pointId, actionIndex);
    const opts = { evidenceOnly: hasEvidence, actionIndex };
    try {
      const res =
        mode === 'dual'
          ? await this.ndApi.rerunDualVerify(this.ndRunId, pointId, opts)
          : await this.ndApi.rerunPoint(this.ndRunId, pointId, opts);
      if (res.success) {
        this.toast.show('Rerunning analysis for this gap…', 'success');
        if (hasEvidence) {
          this.startEvidenceRerunPolling([pointId]);
        } else {
          await this.loadNdRun(this.ndRunId, null, null);
        }
      } else {
        this.ndDetailError = res.message ?? 'Rerun failed';
        this.toast.show(this.ndDetailError, 'error');
      }
    } finally {
      this.evidenceRerunningPointId = null;
      this.evidenceRerunningActionIndex = null;
      this.cdr.markForCheck();
    }
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
      status = (() => {
        const sev = resolveAnalysisPointSeverity(ndPoint);
        return sev ? complianceSeverityLabel(sev) : 'Pending';
      })();
      confidence = resolveDisplayConfidence(ndPoint);
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

  snapshotForPoint(point: AnalysisPoint): PointSnapshot {
    return this.snapshotByPointId.get(point.id) ?? parsePointSnapshot(point.pointSnapshot);
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
    // `items` drops points still missing saved output (buildNdGapListItems), which
    // undercounts right after a run finishes but before every point has synced —
    // the raw point list from the run is the true total.
    const n = this.ndRunData?.points.length ?? this.items.length;
    return `${this.sourceLabel} — ${n} finding${n === 1 ? '' : 's'}`;
  }

  get canDeleteSession(): boolean {
    return !!this.deletableSessionId && !!this.deletableSessionKind && !this.deletingSession;
  }

  async renameRun(): Promise<void> {
    if (!this.ndRunId) return;
    const next = prompt('Rename this analysis', this.sourceLabel);
    if (next === null) return;
    const trimmed = next.trim();
    if (!trimmed || trimmed === this.sourceLabel) return;
    const res = await this.ndApi.renameAnalysisRun(this.ndRunId, trimmed);
    if (res.success) {
      this.sourceLabel = trimmed;
      if (this.ndRunData) this.ndRunData.run.name = trimmed;
      this.toast.show('Analysis renamed', 'success');
      this.cdr.markForCheck();
    } else {
      this.toast.show(res.message ?? 'Could not rename analysis', 'error');
    }
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
    if (!this.ndRunId || !this.ndRunData) return false;
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    const status = this.ndRunData.run.status;
    return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(status);
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
    if (status === 'pulled_back') return 'Returned to maker for correction — edit action plans and resubmit.';
    return '';
  }

  async submitNdReview(draft?: RunReviewDraft): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;
    this.workflowLoading = true;
    this.runReviewError = '';
    const body = draft ? this.buildRunReviewBody(draft) : undefined;
    const res =
      this.ndRunData.run.status === 'pulled_back'
        ? await this.ndApi.resubmitForReview(this.ndRunId, body)
        : await this.ndApi.submitForReview(this.ndRunId, body);
    this.workflowLoading = false;
    if (res.success) {
      this.toast.show('Sent to checker for review', 'success');
      this.workspaceNav.requestNavBadgeRefresh();
      if (this.reviewWorkspaceMode === 'maker') {
        void this.router.navigate(['/nd/analysis-runs'], {
          queryParams: this.reviewWorkspaceBackQueryParams ?? { correction: '1' },
        });
        return;
      }
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.runReviewError = res.message ?? 'Could not submit for review';
      this.toast.show(this.runReviewError, 'error');
    }
  }

  async submitNdReviewFromPanel(draft?: RunReviewDraft): Promise<void> {
    await this.submitNdReview(draft);
  }

  /**
   * Every gap needs a complete action (text, responsible role, target date) before the
   * maker can send the report on — otherwise the checker/reviewer inherit blanks they
   * can't fill in themselves.
   */
  private incompleteGapActionPlans(): string[] {
    // Keyed by clause, not by plan row: a clause can carry several action-plan records
    // (several gaps, or duplicate rows from an earlier bug), and listing every row
    // separately turned this into an unreadable wall of the same clause repeated dozens
    // of times. One line per clause, union of everything missing across its rows.
    const missingByClause = new Map<string, Set<string>>();
    for (const plan of this.allActionPlans) {
      const gaps: string[] = [];
      if (!plan.actionPlan?.trim()) gaps.push('action');
      const hasResponsibility =
        !!plan.responsibilityName?.trim() || !!plan.responsibilityDepartmentId || !!plan.responsibilityUserId
        || !!(plan.assignees && plan.assignees.length);
      if (!hasResponsibility) gaps.push('responsible role');
      if (!plan.targetDate?.trim()) gaps.push('target date');
      if (!gaps.length) continue;
      const clause = this.clauseByPointId.get(plan.analysisPointId) || 'a clause';
      const existing = missingByClause.get(clause);
      if (existing) gaps.forEach((g) => existing.add(g));
      else missingByClause.set(clause, new Set(gaps));
    }
    return [...missingByClause.entries()].map(
      ([clause, gaps]) => `${clause} (missing ${[...gaps].join(', ')})`,
    );
  }

  async onRunReviewSubmit(event: RunReviewSubmitEvent): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;

    if (event.action === 'submit') {
      // Incomplete gaps (missing a responsible role, target date, or action text) no longer
      // block the maker from submitting — for a run with dozens of gaps, requiring every one
      // filled in before the checker even sees the report was impractical. Surface it as a
      // heads-up only.
      if (this.runReviewPanelMode === 'maker') {
        const missing = this.incompleteGapActionPlans();
        if (missing.length) {
          const shown = missing.slice(0, 15);
          const suffix = missing.length > shown.length ? `; and ${missing.length - shown.length} more` : '';
          this.toast.show(
            `Submitting with ${missing.length} incomplete gap${missing.length === 1 ? '' : 's'} — ${shown.join('; ')}${suffix}`,
            'warning',
            6000,
          );
        }
      }
      this.runReviewSubmitting = true;
      await this.submitNdReviewFromPanel(event.draft);
      this.runReviewSubmitting = false;
      return;
    }

    // Reviewing every point is deliberately optional: a checker may pass the report on,
    // and a reviewer may finalize, with gaps still pending.
    this.runReviewSubmitting = true;
    this.runReviewError = '';
    const body = this.buildRunReviewBody(event.draft);
    let res;
    switch (event.action) {
      case 'approve':
        res = await this.ndApi.approveAnalysis(this.ndRunId, body);
        break;
      case 'pullback':
        res = await this.ndApi.pullBackAnalysis(this.ndRunId, body);
        break;
      case 'finalize': {
        this.finalizeProgressMessage = 'Generating corrected internal document…';
        this.cdr.markForCheck();
        const finalizeRes = await this.ndApi.finalizeAnalysis(this.ndRunId, body);
        this.finalizeProgressMessage = '';
        if (finalizeRes.success) {
          const docs = finalizeRes.data?.correctedDocuments ?? [];
          this.toast.show(
            docs.length
              ? `Finalized. ${docs.map((d) => `${d.title} v${d.version}`).join(', ')} saved to the document library.`
              : 'Report finalized.',
            'success',
          );
        }
        res = finalizeRes;
        break;
      }
      case 'pullback_to_checker':
        res = await this.ndApi.pullBackToChecker(this.ndRunId, body);
        break;
      case 'pullback_to_maker':
        res = await this.ndApi.pullBackToMaker(this.ndRunId, body);
        break;
      default:
        this.runReviewSubmitting = false;
        return;
    }

    this.runReviewSubmitting = false;
    if (res.success) {
      this.workspaceNav.requestNavBadgeRefresh();
      const role = this.auth.getRole();
      const leaveToQueue =
        role !== 'super_admin' &&
        ((this.reviewWorkspaceMode === 'checker' && role === 'checker') ||
          (this.reviewWorkspaceMode === 'reviewer' && role === 'reviewer'));
      if (leaveToQueue && this.reviewWorkspaceMode === 'checker') {
        void this.router.navigate(['/nd/checker']);
      } else if (leaveToQueue && this.reviewWorkspaceMode === 'reviewer') {
        void this.router.navigate(['/nd/reviewer']);
      } else {
        await this.loadNdRun(this.ndRunId, null, null);
      }
    } else {
      this.runReviewError = res.message ?? 'Could not complete review action';
    }
  }

  private buildRunReviewBody(draft: RunReviewDraft): NdRunReviewBody {
    return {
      overallComment: draft.comment.trim() || undefined,
      reviewStatus: draft.status,
      priority: draft.priority,
      responsibility: draft.responsibility.trim() || undefined,
      dueDate: draft.dueDate.trim() || undefined,
    };
  }

  async deleteActionItemReview(pointId: string, reviewId: string): Promise<void> {
    if (!this.ndRunId) return;
    this.savingReviewId = reviewId;
    const res = await this.ndApi.deleteActionItemReview(this.ndRunId, reviewId);
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show('Review deleted', 'success');
      this.removeActionItemReview(reviewId, pointId);
    } else {
      this.ndDetailError = res.message ?? 'Could not delete review';
      this.toast.show(this.ndDetailError, 'error');
    }
  }

  private upsertActionItemReview(entry: ActionItemReviewEntry): void {
    if (!this.ndRunData) return;
    const list = [...(this.ndRunData.actionItemReviews ?? [])];
    const idx = list.findIndex((r) => r.id === entry.id);
    if (idx >= 0) list[idx] = entry;
    else list.unshift(entry);
    this.ndRunData = { ...this.ndRunData, actionItemReviews: list };
    this.reviewsByPointId.set(entry.analysisPointId, reviewsForPoint(list, entry.analysisPointId));
    this.cdr.markForCheck();
  }

  private removeActionItemReview(reviewId: string, pointId: string): void {
    if (!this.ndRunData) return;
    const list = (this.ndRunData.actionItemReviews ?? []).filter((r) => r.id !== reviewId);
    this.ndRunData = { ...this.ndRunData, actionItemReviews: list };
    this.reviewsByPointId.set(pointId, reviewsForPoint(list, pointId));
    this.cdr.markForCheck();
  }

  private async reloadActionItemReviews(): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;
    const res = await this.ndApi.getResults(this.ndRunId);
    if (!res.success || !res.data) return;
    const data = res.data as ResultsData;
    this.ndRunData = { ...this.ndRunData, actionItemReviews: data.actionItemReviews ?? [] };
    this.reviewsByPointId.clear();
    for (const point of this.ndRunData.points) {
      if (!point.id) continue;
      this.reviewsByPointId.set(
        point.id,
        reviewsForPoint(this.ndRunData.actionItemReviews, point.id),
      );
    }
    this.cdr.markForCheck();
  }

  openNdResultsEditor(): void {
    if (this.ndRunId) void this.router.navigate(['/nd/gap-analysis'], { queryParams: { run: this.ndRunId } });
  }

  openRunHistory(): void {
    if (!this.ndRunId) return;
    this.runHistoryOpen = true;
  }

  closeRunHistory(): void {
    this.runHistoryOpen = false;
  }

  get runHistoryName(): string {
    return this.ndRunData?.run.name ?? this.sourceLabel ?? 'Analysis run';
  }

  get runHistoryStats(): RunGapStatsSummary | null {
    if (!this.ndRunData) return null;
    return computeRunGapStats(
      this.ndRunData.points,
      this.ndRunData.actionItemReviews,
      attachmentCountsByPoint(this.ndRunData),
      this.ndRunData.actionPlans,
    );
  }

  openCheckerQueue(): void {
    void this.router.navigate(['/nd/checker']);
  }

  openReviewerQueue(): void {
    void this.router.navigate(['/nd/reviewer']);
  }

  setFilter(id: 'all' | GapSeverity | 'with_gaps'): void {
    this.activeFilter = id;
    this.refreshFilteredItems();
    this.ensureListSelection();
    this.cdr.markForCheck();
  }

  get reportSummaryActiveFilter(): ReportSummaryFilterId {
    if (this.activeFilter === 'all' || this.activeFilter === 'with_gaps') return 'all';
    return this.activeFilter;
  }

  filterFromSummaryCard(filter: ReportSummaryFilterId): void {
    if (filter === 'all') return;
    const next: 'all' | GapSeverity | 'with_gaps' =
      this.activeFilter === filter ? 'all' : filter;
    this.setFilter(next);
    if (!this.embedMode) {
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { filter: next === 'all' ? null : next },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    }
    this.scrollToFindings();
  }

  isSummaryCardActive(severity: GapSeverity): boolean {
    return this.activeFilter === severity;
  }

  private scrollToFindings(): void {
    setTimeout(() => {
      this.findingsSection?.nativeElement?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 0);
  }

  setViewMode(mode: 'cards' | 'list'): void {
    this.viewMode = mode;
    if (mode === 'list') this.ensureListSelection();
    this.cdr.markForCheck();
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
    this.cdr.markForCheck();
  }

  get selectedGapItem(): GapItemData | null {
    if (!this.selectedItemId) return null;
    return this.filteredItems.find((i) => i.id === this.selectedItemId) ?? null;
  }

  collapseAllItems(): void {
    this.expandedItemId.set(null);
    this.syncExpandedFlags();
    this.persistSoon();
  }

  expandAllItems(): void {
    const first = this.filteredItemsList[0] ?? this.items[0];
    if (!first) return;
    this.expandedItemId.set(first.id);
    this.syncExpandedFlags();
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
    const openUrl = (url: string) => {
      const full = event.page ? `${url}#page=${event.page}` : url;
      window.open(full, '_blank', 'noopener');
    };
    this.api.getDocumentSignedUrl(event.docId).subscribe({
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
    const res = await this.ndApi.getRegulationDocumentFileUrl(docId);
    if (res.success && res.data?.url) {
      const url = page ? `${res.data.url}#page=${page}` : res.data.url;
      window.open(url, '_blank', 'noopener');
      return;
    }
    this.toast.show(res.message ?? 'Could not open PDF', 'error');
  }

  /** Excel goes through the column picker; the dialog calls back into runXlsxExport. */
  exportXlsx(): void {
    if (this.exporting) return;
    const points = this.analysisPointsForExport();
    if (!points.length) {
      this.toast.show('No analysis results to export', 'info');
      return;
    }
    const plans = this.exportOptions().actionPlans;
    this.exportDialogColumns = gapAnalysisExportColumns(points, {
      regul: !!this.ndRunWorkflowEngine && isRegulWorkflow(this.ndRunWorkflowEngine),
      withAiModel: !!this.ndRunLlmLabel,
    });
    this.exportDialogHasActionPlans = plans.length > 0;
    this.exportDialogHasReviews = plans.some((p) => (p.reviews ?? []).length > 0);
    this.showExportDialog = true;
    this.cdr.markForCheck();
  }

  closeExportDialog(): void {
    this.showExportDialog = false;
    this.cdr.markForCheck();
  }

  async runExportConfirm(confirm: GapAnalysisExportConfirm): Promise<void> {
    this.showExportDialog = false;
    this.headerExportFormat = confirm.format === 'pdf' ? 'pdf' : 'xlsx';
    if (confirm.format === 'pdf') {
      await this.exportPdf();
      return;
    }
    const { format: _format, ...selection } = confirm;
    await this.runXlsxExport(selection);
  }

  async runXlsxExport(selection: GapAnalysisExportSelection): Promise<void> {
    if (this.exporting) return;
    const points = this.analysisPointsForExport();
    if (!points.length) return;

    this.exporting = true;
    this.cdr.markForCheck();
    try {
      const options = {
        ...this.exportOptions(),
        llmLabel: this.ndRunLlmLabel,
        compliantNote: this.ndCompliantNote,
        selection,
      };
      if (this.ndRunWorkflowEngine && isRegulWorkflow(this.ndRunWorkflowEngine)) {
        await exportRegulGapAnalysisExcelFromPoints(points, undefined, undefined, options);
      } else {
        await exportGapAnalysisExcelFromPoints(points, undefined, undefined, options);
      }
      this.toast.show('Exported gap analysis Excel file', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exporting = false;
      this.syncShellPageHeader();
      this.cdr.markForCheck();
    }
  }

  async exportPdf(): Promise<void> {
    if (this.exporting) return;
    const points = this.analysisPointsForExport();
    if (!points.length) {
      this.toast.show('No analysis results to export', 'info');
      return;
    }
    this.exporting = true;
    this.cdr.markForCheck();
    try {
      await exportGapAnalysisPdfFromPoints(points, {
        runName: this.sourceLabel || 'Gap Analysis Report',
        subtitle: this.subtitle,
        ...this.exportOptions(),
      });
      this.toast.show('Exported gap analysis PDF', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exporting = false;
      this.syncShellPageHeader();
      this.cdr.markForCheck();
    }
  }

  /** Regulation document name, action plans and the clause lookup shared by both exports. */
  private exportOptions(): {
    regulationDocumentName: string;
    actionPlans: ActionPlanEntry[];
    clauseByPointId: Map<string, string>;
  } {
    return {
      regulationDocumentName: this.ndRegulationDocName,
      actionPlans: this.allActionPlans,
      clauseByPointId: this.clauseByPointId,
    };
  }

  private analysisPointsForExport(): AnalysisPoint[] {
    if (!this.ndRunData?.points?.length) return [];
    const keys = new Set(
      this.items.map((i) => i.section.replace(/^§/, '').trim().toLowerCase()),
    );
    const matched = this.ndRunData.points.filter((p) => {
      const snap = parsePointSnapshot(p.pointSnapshot);
      const candidates = [snap.pointNumber, p.regulationPointId, p.id, snap.regulationPointId].filter(
        Boolean,
      ) as string[];
      return candidates.some((c) => keys.has(c.trim().toLowerCase()));
    });
    if (matched.length) return matched;
    return this.ndRunData.points.filter(
      (p) => p.landingAiResult?.trim() || p.googleAiResult?.trim(),
    );
  }

  ngOnDestroy(): void {
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.stopEvidenceRerunPolling();
    this.stopEvidencePrepPolling();
    this.stepTracker.deactivate();
    if (this.pipelinePanelActivatedHere) {
      this.pipelinePanel.deactivate();
      this.pipelinePanelActivatedHere = false;
    }
    this.pageHeaderActions.clear();
    this.persistDrafts();
  }

  private loadFromQuery(
    session: string | null,
    saved: string | null,
    section: string | null,
    focus: string | null,
    runId: string | null,
  ): void {
    // A reload of the SAME run (embed re-fires on a token bump, poll-driven refresh, etc.)
    // must update in place — wiping `items`/`loading` here blanked the whole panel back to
    // the spinner on every refresh, which read as "loads, then clears, then loads again"
    // even though nothing had actually changed yet.
    const isRefreshOfSameRun = !!runId && runId === this.ndRunId && this.items.length > 0;
    if (!isRefreshOfSameRun) {
      this.loading = true;
      this.items = [];
      this.filteredItemsList = [];
      this.expandedItemId.set(null);
    }
    this.loadError = null;
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
    this.ndRunWorkflowEngine = null;

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

  private loadNdRun(
    runId: string,
    section: string | null,
    focus: string | null,
  ): Promise<void> {
    if (this.pendingLoadRunId === runId && this.pendingLoadPromise) {
      return this.pendingLoadPromise;
    }
    this.pendingLoadRunId = runId;
    this.pendingLoadPromise = this.executeLoadNdRun(runId, section, focus).finally(() => {
      if (this.pendingLoadRunId === runId) {
        this.pendingLoadRunId = null;
        this.pendingLoadPromise = null;
      }
    });
    return this.pendingLoadPromise;
  }

  private async executeLoadNdRun(
    runId: string,
    section: string | null,
    focus: string | null,
  ): Promise<void> {
    const generation = ++this.loadGeneration;
    const liteRunPromise = this.ndApi.getAnalysisRun(runId, { lite: true });
    try {
      const res = await this.ndApi.getResults(runId);
      if (generation !== this.loadGeneration) return;

      if (!res.success || !res.data) {
        this.loadError = res.message ?? 'Could not load analysis results.';
        this.toast.show(this.loadError, 'error');
        return;
      }

      const data = res.data as ResultsData;
      this.ndRunData = data;
      // Flip off loading as soon as real data lands — the status badge already renders off
      // ndRunData, so leaving `loading` true through the rest of this method (role redirects,
      // index rebuilding, metadata fetch) made the subtitle show "Loading analysis results…"
      // for a beat after the badge and rollup counts were already visible.
      this.loading = false;
      this.ndRunWorkflowEngine = data.run.workflowEngine ?? null;
      this.rebuildNdRunIndexes(data);
      this.ndRunStatus = data.run.status;

      this.runStatusChange.emit(data.run.status);
      this.sourceLabel = data.run.name || 'Analysis run';

      if (generation !== this.loadGeneration) return;
      this.applyNdRunData(data, section, focus);

      if (this.reportEvidenceRerunning && !this.isEvidenceRerunInFlight()) {
        this.reportEvidenceRerunning = false;
        this.evidenceRerunWatchPointIds = null;
        this.stopEvidenceRerunPolling();
        this.syncGapActivityMarquee();
      }

      void this.loadRunMetadata(runId, generation, liteRunPromise);
    } catch {
      if (generation !== this.loadGeneration) return;
      this.loadError = 'Could not load analysis results. Check your connection and try again.';
      this.toast.show(this.loadError, 'error');
    } finally {
      if (generation === this.loadGeneration) {
        this.loading = false;
        this.cdr.markForCheck();
      }
    }
  }

  private async loadRunMetadata(
    runId: string,
    generation: number,
    liteRunPromise?: Promise<{ success: boolean; data?: unknown; message?: string }>,
  ): Promise<void> {
    const runRes = liteRunPromise ?? this.ndApi.getAnalysisRun(runId, { lite: true });
    const resolved = await runRes;
    if (generation !== this.loadGeneration) return;

    this.ndPolicyDocId =
      resolved.success && resolved.data
        ? this.firstDocIdFromRunDetail(resolved.data, 'selectedInternalDocIds')
        : null;
    this.ndRegulationDocId =
      resolved.success && resolved.data
        ? this.firstDocIdFromRunDetail(resolved.data, 'selectedRegulationDocIds')
        : null;
    void this.loadRegulationDocName(this.ndRegulationDocId, generation);

    if (!this.ndRunWorkflowEngine && resolved.success && resolved.data && typeof resolved.data === 'object') {
      this.ndRunWorkflowEngine =
        (resolved.data as { workflowEngine?: string | null }).workflowEngine ?? null;
    }

    void this.loadPolicyDocCatalog(resolved, generation);
  }

  /** Regulation document title — first column of the Excel export. */
  private async loadRegulationDocName(docId: string | null, generation: number): Promise<void> {
    if (!docId) {
      this.ndRegulationDocName = '';
      return;
    }
    const res = await this.ndApi.getRegulationDocument(docId);
    if (generation !== this.loadGeneration) return;
    const doc = (res.data ?? null) as { title?: string; fileName?: string; originalFileName?: string } | null;
    this.ndRegulationDocName = doc?.title?.trim() || doc?.originalFileName?.trim() || doc?.fileName?.trim() || '';
    this.cdr.markForCheck();
  }

  private async loadPolicyDocCatalog(runRes: { success: boolean; data?: unknown }, generation: number): Promise<void> {
    const internalRes = await this.ndApi.getInternalDocuments();
    if (generation !== this.loadGeneration) return;
    this.ndPolicyDocCatalog =
      runRes.success && runRes.data
        ? internalDocCatalogFromRunDetail(runRes.data, (internalRes.data ?? []) as InternalDocument[])
        : [];
    this.cdr.markForCheck();
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

  private applyNdRunData(
    data: ResultsData,
    section: string | null,
    focus: string | null,
  ): void {
    const overlays = this.sessionKey ? loadGapDrafts(this.sessionKey) : {};
    const { items, pointIds } = buildNdGapListItems(data.points, overlays);

    this.pointIds = pointIds;
    this.reportByPointId = new Map();
    this.items = items;
    this.applyExpandedSelection(items, section, focus, overlays);
    this.refreshFilteredItems();
    if (this.viewMode === 'list') {
      this.ensureListSelection();
    }
    this.loading = false;
    this.loadError = items.length
      ? null
      : 'No saved findings in this session — the run may have been cancelled, failed, or never finished.';
    this.syncShellPageHeader();
    this.cdr.markForCheck();

    void this.refreshEvidencePrepLabelsForReportDocs();
    requestAnimationFrame(() => this.enrichGapCountsFrom(0));
  }

  private enrichGapCountsFrom(startIndex: number): void {
    if (!this.ndRunData || startIndex >= this.items.length) return;
    const chunkSize = 8;
    const end = Math.min(startIndex + chunkSize, this.items.length);

    for (let i = startIndex; i < end; i++) {
      const item = this.items[i];
      const ndPoint = this.analysisPointForGap(item);
      if (!ndPoint) continue;
      const gapCount = countDisplayGapsForAnalysisPoint(
        ndPoint,
        this.attachmentCountByPointId.get(ndPoint.id) ?? 0,
      );
      const severity = resolveAnalysisPointSeverity(ndPoint);
      if (severity) item.severity = normalizeGapSeverity(severity);
      item.gapCount = gapCount;
    }

    if (end < this.items.length) {
      requestAnimationFrame(() => this.enrichGapCountsFrom(end));
      return;
    }

    this.refreshFilteredItems();
    this.cdr.markForCheck();
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

    if (this.ndRunData) {
      items = items.map((item) => {
        const ndPoint = this.analysisPointForGap(item);
        if (!ndPoint) return item;
        const severity = resolveAnalysisPointSeverity(ndPoint);
        if (!severity) return item;
        const gapCount = countDisplayGapsForAnalysisPoint(
          ndPoint,
          this.attachmentCountByPointId.get(ndPoint.id) ?? 0,
        );
        return {
          ...item,
          severity,
          gapCount: gapCount > 0 ? gapCount : item.gapCount,
        };
      });
    }

    this.items = items;
    this.applyExpandedSelection(items, section, focus, overlays);
    this.refreshFilteredItems();
    this.loading = false;
    this.loadError = items.length
      ? null
      : 'No saved findings in this session — the run may have been cancelled, failed, or never finished.';
    this.cdr.markForCheck();

    if (this.ndRunData) {
      requestAnimationFrame(() => this.enrichGapCountsFrom(0));
    }
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
        expanded: this.expandedItemId() === item.id,
      };
    });
    saveGapDrafts(this.sessionKey, overlays);
  }
}
