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
import { isRegulWorkflow } from '../../../../lib/nd/regul-fields';
import { exportGapAnalysisExcelFromPoints, exportGapAnalysisPdfFromPoints, exportRegulGapAnalysisExcelFromPoints } from '../../../../lib/nd/export/gap-analysis-export';
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
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdWorkspaceTabsComponent, type NdWorkspaceTab } from '../../../components/nd/nd-workspace-tabs.component';
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
  resolveAnalysisPointSeverity,
  resolveDisplayConfidence,
} from '../../../../lib/nd/point-compliance-status';
import {
  policySnippetFromAnalysisPoint,
} from '../../../../lib/nd/analysis-point-rail-meta';
import { parseReferenceComplianceBlock } from '../../../../lib/ai-lab/parse-reference-response';
import { internalDocCatalogFromRunDetail } from '../../../../lib/nd/run-internal-docs';
import type { PolicyDocCatalogEntry } from '../../../../lib/nd/policy-doc-resolve';
import { reviewsForPoint, type ActionItemReviewEntry, type ActionItemReviewStatus, validateSavedActionReviewsComplete, countSavedReviewProgress } from '../../../../lib/nd/action-item-review';
import { tempCommentsForPoint, type TempPointReviewComment, type TempReviewCommentsChangeEvent } from '../../../../lib/nd/temp-point-review-comment';
import { canAddActionItemReviews, isReviewRole, reviewDisabledHint, reviewWorkspaceLink, attachmentCountsByPoint } from '../../../../lib/nd/nd-review-run-helpers';
import { computeRunGapStats, type RunGapStatsSummary } from '../../../../lib/nd/run-gap-stats';
import { buildNdGapListItems, ndComplianceSummaryFromPoints } from '../../../../lib/nd/nd-run-display';
import type { NdRunReviewBody } from '../../../services/nd/nd-api.service';
import type { RunReviewDraft } from '../../../../lib/nd/run-review';

/** Seeded TFS × IMPTFS combined compliance session (32 points). */
const SEEDED_COMPLIANCE_SESSION = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';

const EMPTY_GAP_ATTACHMENTS: PointGapAttachment[] = [];
const EMPTY_ACTION_REVIEWS: ActionItemReviewEntry[] = [];
const EMPTY_TEMP_COMMENTS: TempPointReviewComment[] = [];

@Component({
  selector: 'app-nd-gap-analysis',
  standalone: true,
  imports: [FormsModule, RouterLink, NgTemplateOutlet, NdStatusBadgeComponent, NdWorkspaceTabsComponent, DualVerifyResultCardComponent, NdGapPointDetailComponent, NdPointSortControlsComponent, NdRunReviewPanelComponent, NdRunHistoryPanelComponent],
  templateUrl: './nd-gap-analysis.component.html',
  styleUrl: './nd-gap-analysis.component.scss',
})
export class NdGapAnalysisComponent implements OnInit, OnChanges, OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly ndApi = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);
  readonly auth = inject(NdAuthService);
  private saveTimer: ReturnType<typeof setTimeout> | null = null;

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

  get isNdRegulWorkflow(): boolean {
    return isRegulWorkflow(this.ndRunWorkflowEngine);
  }

  ndRunData: ResultsData | null = null;
  ndPolicyDocId: string | null = null;
  ndPolicyDocCatalog: PolicyDocCatalogEntry[] = [];
  ndRegulationDocId: string | null = null;
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
  savingActionReviewIndex: number | null = null;
  savingReviewId: string | null = null;

  readonly filters: { id: 'all' | GapSeverity | 'with_gaps'; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'with_gaps', label: 'With gaps' },
    { id: 'compliant', label: 'Compliance' },
    { id: 'partial_compliant', label: 'Partial compliance' },
    { id: 'non_compliant', label: 'Non-compliance' },
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
          return {
            loadKey: [session ?? '', saved ?? '', runId ?? '', section ?? '', focus ?? ''].join('|'),
            filter: params.get('filter'),
            session,
            saved,
            runId,
            section,
            focus,
          };
        }),
        distinctUntilChanged((a, b) => a.loadKey === b.loadKey),
      )
      .subscribe(({ filter, session, saved, runId, section, focus, loadKey }) => {
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

  analysisPointForGap(item: GapItemData): AnalysisPoint | null {
    if (!this.ndRunData) return null;
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
    }
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
    if (this.effectiveReviewMode === 'checker' || this.effectiveReviewMode === 'reviewer') return true;
    return canAddActionItemReviews(this.auth.getRole(), this.ndRunData?.run.status);
  }

  get effectiveReviewMode(): RunReviewPanelMode {
    if (this.reviewWorkspaceMode === 'maker') return 'maker';
    if (this.reviewWorkspaceMode === 'checker') return 'checker';
    if (this.reviewWorkspaceMode === 'reviewer') return 'reviewer';
    const role = this.auth.getRole();
    const status = this.ndRunData?.run.status ?? '';
    if (status === 'pulled_back' && (role === 'maker' || role === 'super_admin')) return 'maker';
    if (status === 'submitted_for_review' && (role === 'checker' || role === 'super_admin')) return 'checker';
    if (status === 'checker_approved' && (role === 'reviewer' || role === 'super_admin')) return 'reviewer';
    if (
      (role === 'maker' || role === 'super_admin') &&
      ['completed', 'dual_verify_failed', 'landing_ai_complete'].includes(status)
    ) {
      return 'maker';
    }
    return 'none';
  }

  get showReviewWorkspaceTabs(): boolean {
    return this.reviewWorkspaceMode !== 'none';
  }

  get reviewWorkspaceTabActive(): NdWorkspaceTab {
    if (this.reviewWorkspaceMode === 'maker') return 'pending_correction';
    if (this.reviewWorkspaceMode === 'checker') return 'pending_review';
    if (this.reviewWorkspaceMode === 'reviewer') return 'pending_final_review';
    return 'all_analysis';
  }

  get showRunReviewPanel(): boolean {
    return this.effectiveReviewMode !== 'none' && !!this.ndRunId && !!this.ndRunData;
  }

  get reviewProgress(): { total: number; reviewed: number } | null {
    if (!this.ndRunData?.points.length) return null;
    const counts = attachmentCountsByPoint(this.ndRunData);
    return countSavedReviewProgress(this.ndRunData.points, this.ndRunData.actionItemReviews, counts);
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
    return this.showRunReviewPanel && this.effectiveReviewMode === 'maker';
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
      await this.loadNdRun(this.ndRunId, null, null);
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
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.ndDetailError = res.message ?? 'Could not reorder review';
      this.toast.show(this.ndDetailError, 'error');
    }
    void pointId;
  }

  get canUploadGapEvidence(): boolean {
    return this.canEditNdCap;
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
      this.toast.show(`Uploaded ${files.length} file(s)`, 'success');
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.ndDetailError = res.message ?? 'Upload failed';
      this.toast.show(this.ndDetailError, 'error');
    }
  }

  async onDeleteGapEvidence(pointId: string, attachmentId: string): Promise<void> {
    if (!this.ndRunId) return;
    const res = await this.ndApi.deletePointGapAttachment(this.ndRunId, pointId, attachmentId);
    if (res.success) {
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.toast.show(res.message ?? 'Could not remove file', 'error');
    }
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
    const opts = { evidenceOnly: true, actionIndex };
    const res =
      mode === 'dual'
        ? await this.ndApi.rerunDualVerify(this.ndRunId, pointId, opts)
        : await this.ndApi.rerunPoint(this.ndRunId, pointId, opts);
    this.evidenceRerunningPointId = null;
    this.evidenceRerunningActionIndex = null;
    if (res.success) {
      this.toast.show('Rerunning analysis for this gap…', 'success');
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.ndDetailError = res.message ?? 'Rerun failed';
      this.toast.show(this.ndDetailError, 'error');
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

  async submitNdReview(): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;
    this.workflowLoading = true;
    this.runReviewError = '';
    const res =
      this.ndRunData.run.status === 'pulled_back'
        ? await this.ndApi.resubmitForReview(this.ndRunId)
        : await this.ndApi.submitForReview(this.ndRunId);
    this.workflowLoading = false;
    if (res.success) {
      this.toast.show('Sent to checker for review', 'success');
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

  async submitNdReviewFromPanel(_draft?: RunReviewDraft): Promise<void> {
    await this.submitNdReview();
  }

  async onRunReviewSubmit(event: RunReviewSubmitEvent): Promise<void> {
    if (!this.ndRunId || !this.ndRunData) return;

    if (event.action === 'submit') {
      this.runReviewSubmitting = true;
      await this.submitNdReviewFromPanel(event.draft);
      this.runReviewSubmitting = false;
      return;
    }

    const attachmentCounts = attachmentCountsByPoint(this.ndRunData);
    const validation = validateSavedActionReviewsComplete(
      this.ndRunData.points,
      this.ndRunData.actionItemReviews,
      attachmentCounts,
    );
    if (!validation.ok && (event.action === 'approve' || event.action === 'finalize')) {
      this.runReviewError = validation.message ?? 'Save a review on each action before submitting.';
      return;
    }

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
      case 'finalize':
        res = await this.ndApi.finalizeAnalysis(this.ndRunId, body);
        break;
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
      if (this.reviewWorkspaceMode === 'checker') {
        void this.router.navigate(['/nd/checker']);
      } else if (this.reviewWorkspaceMode === 'reviewer') {
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
      await this.loadNdRun(this.ndRunId, null, null);
    } else {
      this.ndDetailError = res.message ?? 'Could not delete review';
      this.toast.show(this.ndDetailError, 'error');
    }
    void pointId;
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

  filterFromSummaryCard(severity: GapSeverity): void {
    const next: 'all' | GapSeverity = this.activeFilter === severity ? 'all' : severity;
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

  async exportXlsx(): Promise<void> {
    if (this.exporting) return;
    const points = this.analysisPointsForExport();
    if (!points.length) {
      this.toast.show('No analysis results to export', 'info');
      return;
    }
    this.exporting = true;
    try {
      if (this.ndRunWorkflowEngine && isRegulWorkflow(this.ndRunWorkflowEngine)) {
        await exportRegulGapAnalysisExcelFromPoints(points);
      } else {
        await exportGapAnalysisExcelFromPoints(points);
      }
      this.toast.show('Exported gap analysis Excel file', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exporting = false;
    }
  }

  exportPdf(): void {
    if (this.exporting) return;
    const points = this.analysisPointsForExport();
    if (!points.length) {
      this.toast.show('No analysis results to export', 'info');
      return;
    }
    try {
      exportGapAnalysisPdfFromPoints(points, {
        runName: this.sourceLabel || 'Gap Analysis Report',
        subtitle: this.subtitle,
      });
      this.toast.show('Exported gap analysis PDF', 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    }
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
    this.filteredItemsList = [];
    this.expandedItemId.set(null);
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
      this.ndRunWorkflowEngine = data.run.workflowEngine ?? null;
      this.rebuildNdRunIndexes(data);
      this.ndRunStatus = data.run.status;

      if (!this.embedMode && this.reviewWorkspaceMode === 'none') {
        const status = data.run.status;
        const role = this.auth.getRole();
        if (status?.toLowerCase() === 'pulled_back' && role === 'maker') {
          void this.router.navigate(['/nd/correction/review', runId]);
          return;
        }
        const reviewRoute = reviewWorkspaceLink(role, runId, status);
        if (reviewRoute && (role === 'checker' || role === 'reviewer')) {
          void this.router.navigate(reviewRoute);
          return;
        }
      }

      this.runStatusChange.emit(data.run.status);
      this.sourceLabel = data.run.name || 'Analysis run';

      if (generation !== this.loadGeneration) return;
      this.applyNdRunData(data, section, focus);

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

    if (!this.ndRunWorkflowEngine && resolved.success && resolved.data && typeof resolved.data === 'object') {
      this.ndRunWorkflowEngine =
        (resolved.data as { workflowEngine?: string | null }).workflowEngine ?? null;
    }

    void this.loadPolicyDocCatalog(resolved, generation);
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
    this.cdr.markForCheck();

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
