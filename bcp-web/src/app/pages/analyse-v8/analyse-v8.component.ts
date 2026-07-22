import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRouteSnapshot, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { DualVerifyResultCardComponent } from '../../components/dual-verify-result-card/dual-verify-result-card.component';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../components/nd/nd-point-sort-controls.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { AnalyseBase } from '../shared/analyse-base';
import { startPanelResize, type PanelResizeKind } from '../shared/panel-resize';
import type { GovPoint } from '../../services/api.service';
import type {
  LibrarySummary,
  RegulationDocument,
  ActionPlanHistoryEntry,
  AnalysisPoint,
  PointGapAttachment,
  PointSnapshot,
  InternalDocument,
} from '../../../lib/nd/types';
import { parseReferenceComplianceBlock } from '../../../lib/ai-lab/parse-reference-response';
import type { GapSeverity, GapItemData } from '../../services/reguliq-store';
import { parsePointSnapshot } from '../../../lib/nd/utils';
import { countDisplayGapsForAnalysisPoint } from '../../../lib/nd/cap-gap-count';
import { reviewsForPoint, type ActionItemReviewEntry, type ActionItemReviewStatus } from '../../../lib/nd/action-item-review';
import { canAddActionItemReviews, isReviewRole, reviewDisabledHint } from '../../../lib/nd/nd-review-run-helpers';
import {
  resolveAnalysisPointSeverity,
  resolvePointComplianceLabel,
} from '../../../lib/nd/point-compliance-status';
import { type SortDir } from '../../../lib/nd/list-utils';
import { sortByPointKey, type PointSortMode } from '../../../lib/nd/point-sort';
import {
  buildLibraryPointHierarchy,
  buildLibraryStoredPointDisplay,
  formatStoredAnalyseMeta,
  mapLibrarySnapshotToSourced,
  prepareLibraryPointsForAnalysis,
  type GovPointDuplicateGroup,
  type LibraryHierarchyGroup,
  type LibraryPointDisplayRow,
  type LibraryPointDisplayTree,
  type SourcedGovPoint,
} from '../../../lib/library-points-utils';
import {
  formatChapterLabel,
  formatSectionGroupLabel,
} from '../../../lib/gov-point-filter';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdGapAnalysisComponent } from '../nd/gap-analysis/nd-gap-analysis.component';

type PointsSource = 'regulation' | 'library';

type ApiLibraryPoint = {
  regulationPointId: string;
  regulationDocumentId: string;
  displayOrder: number;
  pointSnapshot?: string | Record<string, unknown>;
};

const EMPTY_POINT_ATTACHMENTS: PointGapAttachment[] = [];
const EMPTY_POINT_REVIEWS: ActionItemReviewEntry[] = [];

@Component({
  selector: 'app-analyse-v8',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, InProgressNavButtonComponent, DualVerifyResultCardComponent, NdGapPointDetailComponent, NdPointSortControlsComponent, NdPointNumberTreeComponent, NdStatusBadgeComponent, NdGapAnalysisComponent],
  templateUrl: './analyse-v8.component.html',
  styleUrl: './analyse-v8.component.scss',
})
export class AnalyseV8Component extends AnalyseBase implements OnInit, OnDestroy {
  readonly versionLabel = 'V8 — Points on Top';
  readonly versionPath = '/analyse-v8';

  analysingPointSort: PointSortMode = 'number';
  analysingPointSortDir: SortDir = 'asc';

  @ViewChild('workspaceEl') workspaceEl?: ElementRef<HTMLElement>;
  @ViewChild('gapReportEl') gapReportEl?: ElementRef<HTMLElement>;

  private readonly ndAuth = inject(NdAuthService);
  workflowLoading = false;
  ndRunPointsByNumber = new Map<string, AnalysisPoint>();
  ndRunStatus = '';
  resultEditingPointId: string | null = null;
  resultCapSavingPointId: string | null = null;
  resultHistory: ActionPlanHistoryEntry[] = [];
  resultHistoryPointId: string | null = null;
  resultDetailError = '';
  ndPointAttachments: PointGapAttachment[] = [];
  ndActionItemReviews: ActionItemReviewEntry[] = [];
  evidenceUploadingPointId: string | null = null;
  savingActionReviewIndex: number | null = null;
  savingReviewId: string | null = null;
  evidenceRerunningPointId: string | null = null;
  evidenceUploadingActionIndex: number | null = null;
  evidenceRerunningActionIndex: number | null = null;
  private sessionPointCache = new Map<string, AnalysisPoint>();
  private snapshotByPointId = new Map<string, PointSnapshot>();
  private attachmentsByPointId = new Map<string, PointGapAttachment[]>();
  private reviewsByPointId = new Map<string, ActionItemReviewEntry[]>();

  setupRegSourcesPct = 58;
  setupRegInnerPct = 42;
  colLeftWidth = 340;
  colMidWidth = 280;

  isNdShell = false;
  pointsSource: PointsSource = 'regulation';
  libraries: LibrarySummary[] = [];
  selectedLibraryIds = new Set<string>();
  librarySearch = '';
  libraryLoading = false;
  libraryStoredCount = 0;
  libraryDuplicateGroups: GovPointDuplicateGroup[] = [];
  libraryHierarchy: LibraryHierarchyGroup[] = [];
  libraryDisplayTree: LibraryPointDisplayTree[] = [];
  libraryComparableCounts = new Map<string, number>();
  libraryDuplicatesExpanded = false;
  expandedLibraryIds = new Set<string>();
  expandedLibraryDocKeys = new Set<string>();
  expandedLibraryChapterKeys = new Set<string>();
  private librariesLoaded = false;
  private navSub?: Subscription;
  ndInternalParseStatus = new Map<string, string>();

  override readonly formatChapterLabel = formatChapterLabel;
  override readonly formatSectionGroupLabel = formatSectionGroupLabel;

  get showNdPointSources(): boolean {
    return this.isNdShell;
  }

  get filteredLibraries(): LibrarySummary[] {
    const q = this.librarySearch.trim().toLowerCase();
    if (!q) return this.libraries;
    return this.libraries.filter((lib) => lib.name.toLowerCase().includes(q));
  }

  override ngOnInit(): void {
    this.refreshNdShellState();
    super.ngOnInit();
    this.canonicalizeNdAnalysisUrl();
    if (this.isNdShell) void this.ensureLibrariesLoaded();
    if (this.activeNdRunId) void this.loadNdRunPoints(this.activeNdRunId);

    this.navSub = this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe(() => {
        const hadNdCatalog = this.useNdRegulationCatalog;
        this.refreshNdShellState();
        if (this.isNdShell) void this.ensureLibrariesLoaded();
        if (this.useNdRegulationCatalog !== hadNdCatalog) this.refreshRegulations();
      });
  }

  private refreshNdShellState(): void {
    this.isNdShell = this.isUnderNdRoute() || this.currentPathname().startsWith('/nd/');
    this.useNdRegulationCatalog = this.isNdShell;
  }

  private isUnderNdRoute(): boolean {
    let route: ActivatedRouteSnapshot | null = this.route.snapshot;
    while (route) {
      if (route.routeConfig?.path === 'nd') return true;
      route = route.parent;
    }
    return false;
  }

  private currentPathname(): string {
    const path =
      typeof window !== 'undefined' ? window.location.pathname : this.router.url.split('?')[0];
    return path || '';
  }

  private canonicalizeNdAnalysisUrl(): void {
    if (!this.isNdShell) return;
    if (!/^\/nd\/run-analysis\/?$/.test(this.currentPathname())) return;
    void this.router.navigate(['/nd/analyse-v8'], {
      replaceUrl: true,
      queryParamsHandling: 'merge',
    });
  }

  private async ensureLibrariesLoaded(): Promise<void> {
    if (this.librariesLoaded) return;
    await this.loadLibraries();
    this.librariesLoaded = true;
  }

  async loadLibraries(): Promise<void> {
    const res = await this.ndApi.getLibraries();
    if (res.success && res.data) this.libraries = res.data as LibrarySummary[];
  }

  setPointsSource(source: PointsSource): void {
    if (this.pointsSource === source) return;
    this.pointsSource = source;
    this.useLibraryPoints = source === 'library';
    this.error = '';

    if (source === 'regulation') {
      this.selectedLibraryIds.clear();
      this.libraryPrimaryRegDocId = null;
      this.librarySourceLabel = '';
      this.resetLibraryPointMeta();
      this.loadPointsForSelectedFiles();
      return;
    }

    this.selectedRegIds.clear();
    this.selectedRegDocs = [];
    this.rawGovPoints = [];
    this.govPoints = [];
    this.chapterGroups = [];
    this.selected.clear();
    this.govSourceLabel = '';
    this.resetLibraryPointMeta();
    if (this.selectedLibraryIds.size) void this.loadLibraryPoints();
  }

  toggleLibrary(lib: LibrarySummary): void {
    const next = new Set(this.selectedLibraryIds);
    if (next.has(lib.id)) next.delete(lib.id);
    else next.add(lib.id);
    this.selectedLibraryIds = next;
    void this.loadLibraryPoints();
  }

  isLibrarySelected(libId: string): boolean {
    return this.selectedLibraryIds.has(libId);
  }

  selectAllFilteredLibraries(): void {
    this.selectedLibraryIds = new Set(this.filteredLibraries.map((l) => l.id));
    void this.loadLibraryPoints();
  }

  clearLibrarySelection(): void {
    this.selectedLibraryIds = new Set();
    this.rawGovPoints = [];
    this.govPoints = [];
    this.chapterGroups = [];
    this.selected.clear();
    this.govSourceLabel = '';
    this.libraryPrimaryRegDocId = null;
    this.librarySourceLabel = '';
    this.resetLibraryPointMeta();
    this.error = '';
  }

  private resetLibraryPointMeta(): void {
    this.libraryStoredCount = 0;
    this.libraryDuplicateGroups = [];
    this.libraryHierarchy = [];
    this.libraryDisplayTree = [];
    this.libraryDuplicatesExpanded = false;
    this.expandedLibraryIds = new Set();
    this.expandedLibraryDocKeys = new Set();
    this.expandedLibraryChapterKeys = new Set();
  }

  get libraryDuplicateCount(): number {
    return this.libraryDuplicateGroups.reduce((n, g) => n + g.duplicates.length, 0);
  }

  toggleLibraryHierarchy(libKey: string): void {
    const next = new Set(this.expandedLibraryIds);
    if (next.has(libKey)) next.delete(libKey);
    else next.add(libKey);
    this.expandedLibraryIds = next;
  }

  isLibraryHierarchyExpanded(libKey: string): boolean {
    return this.expandedLibraryIds.has(libKey);
  }

  toggleLibraryDoc(docKey: string): void {
    const next = new Set(this.expandedLibraryDocKeys);
    if (next.has(docKey)) next.delete(docKey);
    else {
      next.add(docKey);
      const doc = this.libraryDisplayTree
        .flatMap((lib) => lib.documents)
        .find((d) => d.key === docKey);
      if (doc?.useChapters) {
        const chapterKeys = new Set(this.expandedLibraryChapterKeys);
        for (const ch of doc.chapters) {
          chapterKeys.add(this.libraryChapterKey(docKey, ch.chapter));
        }
        this.expandedLibraryChapterKeys = chapterKeys;
      }
    }
    this.expandedLibraryDocKeys = next;
  }

  isLibraryDocExpanded(docKey: string): boolean {
    return this.expandedLibraryDocKeys.has(docKey);
  }

  toggleLibraryDuplicates(): void {
    this.libraryDuplicatesExpanded = !this.libraryDuplicatesExpanded;
  }

  libraryChapterKey(docKey: string, chapter: string): string {
    return `${docKey}:${chapter}`;
  }

  toggleLibraryChapter(docKey: string, chapter: string): void {
    const key = this.libraryChapterKey(docKey, chapter);
    const next = new Set(this.expandedLibraryChapterKeys);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedLibraryChapterKeys = next;
  }

  isLibraryChapterExpanded(docKey: string, chapter: string): boolean {
    return this.expandedLibraryChapterKeys.has(this.libraryChapterKey(docKey, chapter));
  }

  showLibrarySectionBar(
    sections: { key: string }[],
    key: string,
    chapter: string,
  ): boolean {
    return sections.length > 1 || key !== chapter;
  }

  selectableLibraryPointId(row: LibraryPointDisplayRow): string | null {
    if (!row.forAnalysis) return null;
    const regId = row.point.regulationPointId ?? row.point.point_id;
    const match = this.govPoints.find(
      (g) => g.point_id === regId || (g as SourcedGovPoint).regulationPointId === regId,
    );
    return match?.point_id ?? regId;
  }

  libraryCountSummary(): string {
    if (!this.libraryStoredCount) return '';
    const parts = [`${this.libraryStoredCount} stored`];
    if (this.libraryDuplicateCount) parts.push(`${this.libraryDuplicateCount} exact duplicates merged`);
    parts.push(`${this.govPoints.length} compared in gap analysis`);
    return parts.join(' · ');
  }

  duplicateSourceLabel(p: SourcedGovPoint): string {
    return p.sourceLabel?.trim() || 'Another source';
  }

  libraryPointMeta(lib: LibrarySummary): string {
    const docs = lib.documentCount ?? 0;
    const stored = lib.pointCount ?? 0;
    const analyse = this.libraryComparableCounts.get(lib.id);
    const docPart = docs > 0 ? `${docs} doc${docs === 1 ? '' : 's'} · ` : '';
    return `${docPart}${formatStoredAnalyseMeta(stored, analyse)}`;
  }

  libraryPointsFootnote(): string {
    if (!this.selectedLibraryIds.size || !this.govPoints.length) return '';
    const parts: string[] = [];
    if (this.libraryStoredCount > 0) parts.push(`${this.libraryStoredCount} stored`);
    if (this.libraryDuplicateCount > 0) parts.push(`${this.libraryDuplicateCount} duplicates merged`);
    parts.push(`${this.govPoints.length} compared in gap analysis`);
    parts.push('section headers shown for context only');
    return parts.join(' · ');
  }

  protected override onNdRunSaved(runId: string): void {
    void this.loadNdRunPoints(runId).then(() => {
      this.ndRunStatus = this.ndRunWorkflowStatus || 'completed';
    });
  }

  get selectedDetailAnalysisPoint(): AnalysisPoint | null {
    if (!this.selectedDetailPointId) return null;
    return this.analysisPointForPointId(this.selectedDetailPointId);
  }

  analysisPointForPointId(pointId: string): AnalysisPoint | null {
    const saved = this.ndRunPointsByNumber.get(pointId);
    if (saved) return saved;
    const cached = this.sessionPointCache.get(pointId);
    if (cached) return cached;
    const built = this.buildSessionAnalysisPoint(pointId);
    if (built) this.sessionPointCache.set(pointId, built);
    return built;
  }

  pointSnapshotForPointId(pointId: string): PointSnapshot | null {
    const point = this.analysisPointForPointId(pointId);
    if (!point) return null;
    const cached = this.snapshotByPointId.get(point.id);
    if (cached) return cached;
    const snap = parsePointSnapshot(point.pointSnapshot);
    this.snapshotByPointId.set(point.id, snap);
    return snap;
  }

  gapCountForPointId(pointId: string): number {
    const point = this.analysisPointForPointId(pointId);
    if (!point) return 0;
    return countDisplayGapsForAnalysisPoint(point, this.attachmentsForPoint(pointId).length);
  }

  gapCountForGapItem(item: GapItemData): number {
    const fromPoint = this.gapCountForPointId(this.gapItemPointId(item));
    if (fromPoint > 0) return fromPoint;
    return item.gapCount ?? 0;
  }

  override getPointGapSeverity(pointId: string): GapSeverity | null {
    const saved = this.ndRunPointsByNumber.get(pointId);
    if (saved) return resolveAnalysisPointSeverity(saved);
    return super.getPointGapSeverity(pointId);
  }

  get sortedAnalysingRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    return sortByPointKey(
      this.selectedCoverageRows,
      this.analysingPointSort,
      this.analysingPointSortDir,
      (row) => row.pointId,
      (row) => this.getPointGapSeverity(row.pointId) ?? '',
    );
  }

  onAnalysingPointSortChange(event: { sort: 'number' | 'status'; dir: SortDir }): void {
    this.analysingPointSort = event.sort;
    this.analysingPointSortDir = event.dir;
  }

  complianceLabelForPointId(pointId: string): string {
    const point = this.analysisPointForPointId(pointId);
    if (!point) return '';
    return resolvePointComplianceLabel(point);
  }

  get selectedPointSnapshot() {
    if (!this.selectedDetailPointId) return null;
    return this.pointSnapshotForPointId(this.selectedDetailPointId);
  }

  get canEditResultCap(): boolean {
    if (!this.activeNdRunId) return false;
    const role = this.ndAuth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return !['submitted_for_review', 'checker_approved', 'reviewer_approved'].includes(this.ndRunStatus);
  }

  get canReviewActionGaps(): boolean {
    return canAddActionItemReviews(this.ndAuth.getRole(), this.ndRunStatus);
  }

  get canShowReviewPanel(): boolean {
    return isReviewRole(this.ndAuth.getRole());
  }

  get gapReviewDisabledHint(): string {
    return reviewDisabledHint(this.ndAuth.getRole(), this.ndRunStatus);
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
    const runId = this.activeNdRunId;
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!runId || !ndPoint) return;
    this.savingActionReviewIndex = event.actionIndex;
    this.savingReviewId = event.reviewId ?? null;
    this.resultDetailError = '';
    const body = {
      status: event.status,
      comment: event.comment.trim() || undefined,
      responsibility: event.responsibility.trim() || undefined,
      dueDate: event.dueDate.trim() || undefined,
      priority: event.priority || undefined,
    };
    const res = event.reviewId
      ? await this.ndApi.updateActionItemReview(runId, event.reviewId, body)
      : await this.ndApi.saveActionItemReview(runId, {
          analysisPointId: ndPoint.id,
          actionIndex: event.actionIndex,
          ...body,
        });
    this.savingActionReviewIndex = null;
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show(event.reviewId ? 'Review updated' : 'Review saved', 'success');
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Could not save review';
      this.toast.show(this.resultDetailError, 'error');
    }
  }

  async deleteActionItemReview(pointId: string, reviewId: string): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    this.savingReviewId = reviewId;
    const res = await this.ndApi.deleteActionItemReview(runId, reviewId);
    this.savingReviewId = null;
    if (res.success) {
      this.toast.show('Review deleted', 'success');
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Could not delete review';
      this.toast.show(this.resultDetailError, 'error');
    }
    void pointId;
  }

  async reorderActionItemReview(
    pointId: string,
    event: { reviewId: string; actionIndex: number; direction: 'up' | 'down' },
  ): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    this.savingReviewId = event.reviewId;
    this.savingActionReviewIndex = event.actionIndex;
    this.resultDetailError = '';
    const res = await this.ndApi.reorderActionItemReview(runId, event.reviewId, event.direction);
    this.savingReviewId = null;
    this.savingActionReviewIndex = null;
    if (res.success) {
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Could not reorder review';
      this.toast.show(this.resultDetailError, 'error');
    }
    void pointId;
  }

  selectedPointComplianceLabel(): string {
    const point = this.selectedDetailAnalysisPoint;
    if (!point) return '';
    return resolvePointComplianceLabel(point);
  }

  pointComplianceSeverity(point: AnalysisPoint): GapSeverity {
    return resolveAnalysisPointSeverity(point);
  }

  async loadNdRunPoints(runId: string): Promise<void> {
    const res = await this.ndApi.getResults(runId);
    if (!res.success || !res.data) return;
    const data = res.data as {
      run: { status: string };
      points: AnalysisPoint[];
      pointAttachments?: PointGapAttachment[];
      actionItemReviews?: ActionItemReviewEntry[];
    };
    this.ndRunStatus = data.run.status;
    this.ndPointAttachments = data.pointAttachments ?? [];
    this.ndActionItemReviews = data.actionItemReviews ?? [];
    this.ndRunPointsByNumber.clear();
    this.sessionPointCache.clear();
    this.snapshotByPointId.clear();
    this.attachmentsByPointId.clear();
    this.reviewsByPointId.clear();

    for (const attachment of this.ndPointAttachments) {
      const list = this.attachmentsByPointId.get(attachment.analysisPointId);
      if (list) list.push(attachment);
      else this.attachmentsByPointId.set(attachment.analysisPointId, [attachment]);
    }

    for (const p of data.points) {
      const snap = parsePointSnapshot(p.pointSnapshot);
      const num = snap.pointNumber?.trim();
      if (num) this.ndRunPointsByNumber.set(num, p);
      if (p.id) {
        this.snapshotByPointId.set(p.id, snap);
        this.reviewsByPointId.set(p.id, reviewsForPoint(this.ndActionItemReviews, p.id));
      }
    }
    this.syncInlineGapSeveritiesFromNdRun();
  }

  private syncInlineGapSeveritiesFromNdRun(): void {
    if (!this.inlineGapItems.length) return;
    for (const item of this.inlineGapItems) {
      const pointId = this.gapItemPointId(item);
      const saved = this.ndRunPointsByNumber.get(pointId) ?? this.analysisPointForPointId(pointId);
      if (!saved) continue;
      const attachmentCount = this.attachmentsByPointId.get(saved.id)?.length ?? 0;
      item.severity = resolveAnalysisPointSeverity(saved);
      item.gapCount = countDisplayGapsForAnalysisPoint(saved, attachmentCount);
    }
  }

  attachmentsForPoint(pointId: string): PointGapAttachment[] {
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!ndPoint) return EMPTY_POINT_ATTACHMENTS;
    return this.attachmentsByPointId.get(ndPoint.id) ?? EMPTY_POINT_ATTACHMENTS;
  }

  savedReviewsForPoint(pointId: string): ActionItemReviewEntry[] {
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!ndPoint) return EMPTY_POINT_REVIEWS;
    return this.reviewsByPointId.get(ndPoint.id) ?? EMPTY_POINT_REVIEWS;
  }

  get nonComplianceGapPoints(): { pointId: string; label: string; gapCount: number; severity: GapSeverity }[] {
    const rows: { pointId: string; label: string; gapCount: number; severity: GapSeverity }[] = [];
    for (const item of this.inlineGapItems) {
      const pointId = this.gapItemPointId(item);
      const ndPoint = this.analysisPointForPointId(pointId);
      if (!ndPoint) continue;
      const severity = resolveAnalysisPointSeverity(ndPoint);
      if (severity === 'compliant') continue;
      const gapCount = countDisplayGapsForAnalysisPoint(ndPoint, this.attachmentsForPoint(pointId).length);
      rows.push({
        pointId,
        label: item.section ? `§${item.section}` : pointId,
        gapCount,
        severity,
      });
    }
    return rows.sort((a, b) => a.label.localeCompare(b.label, undefined, { numeric: true }));
  }

  async onUploadGapEvidence(pointId: string, fileList: FileList, actionIndex?: number): Promise<void> {
    const runId = this.activeNdRunId;
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!runId || !ndPoint || !fileList.length) return;
    this.evidenceUploadingPointId = ndPoint.id;
    this.evidenceUploadingActionIndex = actionIndex ?? null;
    this.resultDetailError = '';
    const files = Array.from(fileList);
    const res = await this.ndApi.uploadPointGapAttachments(runId, ndPoint.id, files, actionIndex);
    this.evidenceUploadingPointId = null;
    this.evidenceUploadingActionIndex = null;
    if (res.success) {
      this.toast.show(`Uploaded ${files.length} file(s)`, 'success');
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Upload failed';
      this.toast.show(this.resultDetailError, 'error');
    }
  }

  async onDeleteGapEvidence(pointId: string, attachmentId: string): Promise<void> {
    const runId = this.activeNdRunId;
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!runId || !ndPoint) return;
    const res = await this.ndApi.deletePointGapAttachment(runId, ndPoint.id, attachmentId);
    if (res.success) {
      await this.loadNdRunPoints(runId);
    } else {
      this.toast.show(res.message ?? 'Could not remove file', 'error');
    }
  }

  async onRerunWithEvidence(pointId: string, mode: 'full' | 'dual'): Promise<void> {
    const runId = this.activeNdRunId;
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!runId || !ndPoint) return;
    this.evidenceRerunningPointId = ndPoint.id;
    this.evidenceRerunningActionIndex = null;
    this.resultDetailError = '';
    const opts = { evidenceOnly: true };
    const res =
      mode === 'dual'
        ? await this.ndApi.rerunDualVerify(runId, ndPoint.id, opts)
        : await this.ndApi.rerunPoint(runId, ndPoint.id, opts);
    this.evidenceRerunningPointId = null;
    if (res.success) {
      this.toast.show('Rerunning analysis for this point…', 'success');
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Rerun failed';
      this.toast.show(this.resultDetailError, 'error');
    }
  }

  async onRerunGapEvidence(
    pointId: string,
    payload: { actionIndex: number; mode: 'full' | 'dual' },
  ): Promise<void> {
    const runId = this.activeNdRunId;
    const ndPoint = this.analysisPointForPointId(pointId);
    if (!runId || !ndPoint) return;
    this.evidenceRerunningPointId = ndPoint.id;
    this.evidenceRerunningActionIndex = payload.actionIndex;
    this.resultDetailError = '';
    const opts = { evidenceOnly: true, actionIndex: payload.actionIndex };
    const res =
      payload.mode === 'dual'
        ? await this.ndApi.rerunDualVerify(runId, ndPoint.id, opts)
        : await this.ndApi.rerunPoint(runId, ndPoint.id, opts);
    this.evidenceRerunningPointId = null;
    this.evidenceRerunningActionIndex = null;
    if (res.success) {
      this.toast.show('Rerunning analysis for this gap…', 'success');
      await this.loadNdRunPoints(runId);
    } else {
      this.resultDetailError = res.message ?? 'Rerun failed';
      this.toast.show(this.resultDetailError, 'error');
    }
  }

  private buildSessionAnalysisPoint(pointId: string): AnalysisPoint | null {
    const session = this.sessionPointResults.get(pointId);
    if (!session?.landingMessage && !session?.llmMessage) return null;
    const gov = this.govPoints.find((g) => g.point_id === pointId);
    const landing = session.landingMessage ?? '';
    const llm = session.llmMessage ?? '';
    const cap =
      parseReferenceComplianceBlock(landing).correctiveAction?.trim() ||
      parseReferenceComplianceBlock(llm).correctiveAction?.trim() ||
      this.inlineGapItems.find((i) => this.gapItemPointId(i) === pointId)?.gaps?.trim() ||
      '';
    const sev = this.getPointGapSeverity(pointId);
    return {
      id: pointId,
      pointSnapshot: JSON.stringify({
        pointNumber: pointId,
        pointTitle: gov?.title ?? session.pointTitle ?? null,
        pointContent: gov?.text ?? null,
        regulationDocumentId: this.regulationPdfDocId,
      }),
      landingAiStatus: landing ? 'compliant' : 'failed',
      landingAiResult: landing ? JSON.stringify({ message: landing }) : null,
      googleAiStatus: llm ? 'compliant' : 'pending',
      googleAiResult: llm ? JSON.stringify({ message: llm }) : null,
      dualVerifyStatus: session.agreementJson ? 'passed' : 'skipped',
      finalStatus: sev,
      finalActionPlan: cap || null,
      originalAiActionPlan: cap || null,
    };
  }

  startResultCapEdit(point: AnalysisPoint): void {
    this.resultEditingPointId = point.id;
    this.closeResultCapHistory();
    this.resultDetailError = '';
  }

  cancelResultCapEdit(): void {
    this.resultEditingPointId = null;
  }

  private patchNdPointActionPlan(pointId: string, content: string): void {
    for (const [num, p] of this.ndRunPointsByNumber) {
      if (p.id === pointId) {
        this.ndRunPointsByNumber.set(num, { ...p, finalActionPlan: content });
        return;
      }
    }
  }

  async saveResultCap(pointId: string, content: string): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    this.resultCapSavingPointId = pointId;
    this.resultDetailError = '';
    const res = await this.ndApi.updateActionPlan(runId, pointId, content);
    this.resultCapSavingPointId = null;
    if (res.success) {
      this.patchNdPointActionPlan(pointId, content);
      this.resultEditingPointId = null;
      if (this.resultHistoryPointId === pointId) await this.loadResultCapHistory(pointId);
    } else {
      this.resultDetailError = res.message ?? 'Failed to save action plan';
    }
  }

  openResultCapHistory(pointId: string): void {
    this.resultEditingPointId = null;
    void this.loadResultCapHistory(pointId);
  }

  closeResultCapHistory(): void {
    this.resultHistoryPointId = null;
    this.resultHistory = [];
  }

  async toggleResultCapHistory(pointId: string): Promise<void> {
    if (this.resultHistoryPointId === pointId) this.closeResultCapHistory();
    else this.openResultCapHistory(pointId);
  }

  private async loadResultCapHistory(pointId: string): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    const res = await this.ndApi.getActionPlanHistory(runId, pointId);
    if (res.success && res.data) {
      this.resultHistory = res.data as ActionPlanHistoryEntry[];
      this.resultHistoryPointId = pointId;
    }
  }

  async restoreResultCapVersion(version: ActionPlanHistoryEntry): Promise<void> {
    if (!this.resultHistoryPointId || !this.activeNdRunId) return;
    const res = await this.ndApi.updateActionPlan(
      this.activeNdRunId,
      this.resultHistoryPointId,
      version.actionPlanContent,
      version.versionNumber,
    );
    if (res.success) {
      this.patchNdPointActionPlan(this.resultHistoryPointId, version.actionPlanContent);
      this.resultEditingPointId = null;
      await this.loadResultCapHistory(this.resultHistoryPointId);
    } else {
      this.resultDetailError = res.message ?? 'Failed to restore version';
    }
  }

  get canSubmitForChecker(): boolean {
    return false;
  }

  get showWorkflowStatusBadge(): boolean {
    return Boolean(this.activeNdRunId && this.ndRunWorkflowStatus);
  }

  async submitForChecker(): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    this.workflowLoading = true;
    const status = this.ndRunWorkflowStatus.toLowerCase();
    const res =
      status === 'pulled_back'
        ? await this.ndApi.resubmitForReview(runId)
        : await this.ndApi.submitForReview(runId);
    this.workflowLoading = false;
    if (res.success) {
      this.ndRunWorkflowStatus = 'submitted_for_review';
      this.ndRunStatus = 'submitted_for_review';
      this.toast.show('Submitted to checker for review', 'success', 4000);
    } else {
      this.toast.show(res.message ?? 'Could not submit for review', 'error', 5000);
    }
  }

  openGapReport(): void {
    this.openFullReport();
  }

  onEmbeddedRunStatusChange(status: string): void {
    this.ndRunWorkflowStatus = status;
  }

  protected override scrollToInlineGapReport(): void {
    setTimeout(() => {
      this.gapReportEl?.nativeElement?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 120);
  }

  protected override onAnalysisComplete(): void {
    super.onAnalysisComplete();
    this.scrollToInlineGapReport();
  }

  async loadLibraryPoints(): Promise<void> {
    if (!this.selectedLibraryIds.size) {
      this.clearLibrarySelection();
      return;
    }

    this.libraryLoading = true;
    this.loadingPoints = true;
    this.error = '';
    this.resetLibraryPointMeta();

    try {
      const docNames = await this.loadRegulationDocNames();
      const ids = [...this.selectedLibraryIds];
      const results = await Promise.all(ids.map((id) => this.ndApi.getLibrary(id)));
      const allRaw: SourcedGovPoint[] = [];
      const libraryNames: string[] = [];
      const regDocIds = new Set<string>();
      const perLibraryRaw = new Map<string, SourcedGovPoint[]>();

      for (let i = 0; i < results.length; i++) {
        const res = results[i];
        const libId = ids[i];
        const libMeta = this.libraries.find((l) => l.id === libId);
        if (!res.success || !res.data) continue;

        const data = res.data as Record<string, unknown>;
        const nested = data['library'] as Record<string, unknown> | undefined;
        const libraryName =
          String(data['name'] ?? nested?.['name'] ?? libMeta?.name ?? '').trim() ||
          'Regulation points library';
        libraryNames.push(libraryName);

        const rawPoints = (data['points'] ?? nested?.['points'] ?? []) as ApiLibraryPoint[];
        const libRaw: SourcedGovPoint[] = [];
        for (const p of rawPoints) {
          const snap = this.parseSnapshot(p.pointSnapshot);
          const docId = String(p.regulationDocumentId);
          regDocIds.add(docId);
          const sourced = mapLibrarySnapshotToSourced(
            { ...p, pointSnapshot: snap },
            {
              libraryId: libId,
              libraryName,
              docName: docNames.get(docId) ?? 'Regulation document',
            },
          );
          libRaw.push(sourced);
          allRaw.push(sourced);
        }
        perLibraryRaw.set(libId, libRaw);
      }

      for (const [libId, libRaw] of perLibraryRaw) {
        const preparedLib = prepareLibraryPointsForAnalysis(libRaw);
        this.libraryComparableCounts.set(libId, preparedLib.unique.length);
      }

      if (!allRaw.length) {
        this.error = 'Selected regulation points libraries have no points';
        this.rawGovPoints = [];
        this.govPoints = [];
        this.chapterGroups = [];
        this.selected.clear();
        return;
      }

      const prepared = prepareLibraryPointsForAnalysis(allRaw);
      this.rawGovPoints = allRaw;
      this.govPoints = prepared.unique;
      this.libraryStoredCount = prepared.storedCount;
      this.libraryDuplicateGroups = prepared.duplicateGroups;
      this.libraryHierarchy = buildLibraryPointHierarchy(allRaw, prepared.unique);
      this.libraryDisplayTree = buildLibraryStoredPointDisplay(allRaw, prepared.unique);
      this.chapterGroups = [];

      if (this.libraryDisplayTree.length) {
        this.expandedLibraryIds = new Set(this.libraryDisplayTree.map((lib) => lib.key));
        this.expandedLibraryDocKeys = new Set();
        this.expandedLibraryChapterKeys = new Set();
        for (const lib of this.libraryDisplayTree) {
          for (const doc of lib.documents) {
            this.expandedLibraryDocKeys.add(doc.key);
            if (doc.useChapters) {
              for (const ch of doc.chapters) {
                this.expandedLibraryChapterKeys.add(this.libraryChapterKey(doc.key, ch.chapter));
              }
            }
          }
        }
      }

      this.selected.clear();
      prepared.unique.forEach((p) => this.selected.add(p.point_id));

      this.libraryPrimaryRegDocId = [...regDocIds][0] ?? null;
      this.librarySourceLabel =
        libraryNames.length === 1
          ? libraryNames[0]
          : `${libraryNames.length} libraries: ${libraryNames.join(', ')}`;
      this.useLibraryPoints = true;
      this.govSourceLabel = this.libraryCountSummary();

      this.toast.show(
        `Loaded ${prepared.unique.length} points for gap analysis (${prepared.storedCount} stored in library)`,
        'success',
        3200,
      );
    } catch {
      this.error = 'Failed to load regulation points library';
    } finally {
      this.libraryLoading = false;
      this.loadingPoints = false;
    }
  }

  private async loadRegulationDocNames(): Promise<Map<string, string>> {
    const names = new Map<string, string>();
    const res = await this.ndApi.getRegulationDocuments();
    if (res.success && res.data) {
      for (const d of res.data as RegulationDocument[]) {
        names.set(d.id, d.name);
      }
    }
    return names;
  }

  private parseSnapshot(snapshot: ApiLibraryPoint['pointSnapshot']): Record<string, unknown> {
    if (!snapshot) return {};
    if (typeof snapshot === 'string') return parsePointSnapshot(snapshot) as Record<string, unknown>;
    return snapshot;
  }

  startTopSplit(which: 'section' | 'reg', event: MouseEvent): void {
    const container = (event.target as HTMLElement).closest('.setup-row-inner');
    const startVal = which === 'section' ? this.setupRegSourcesPct : this.setupRegInnerPct;
    startPanelResize(
      {
        kind: 'setup-split',
        startX: event.clientX,
        startY: event.clientY,
        startVal,
        containerWidth: container?.clientWidth ?? 0,
      },
      event,
      (_kind, value) => {
        if (which === 'section') {
          this.setupRegSourcesPct = Math.min(Math.max(value, 35), 75);
        } else {
          this.setupRegInnerPct = Math.min(Math.max(value, 28), 68);
        }
      },
      { 'setup-split': { min: 28, max: 75 } },
    );
  }

  startColResize(side: 'left' | 'right', event: MouseEvent): void {
    const kind: PanelResizeKind = side === 'left' ? 'col-left' : 'col-right';
    startPanelResize(
      {
        kind,
        startX: event.clientX,
        startY: event.clientY,
        startVal: side === 'left' ? this.colLeftWidth : this.colMidWidth,
      },
      event,
      (k, value) => {
        if (k === 'col-left') this.colLeftWidth = value;
        if (k === 'col-right') this.colMidWidth = value;
      },
    );
  }

  protected override async onNdRunContextLoaded(detail: {
    run: {
      libraryId?: string | null;
      selectedPointsSnapshot: string;
      selectedRegulationDocIds?: string;
    };
    points: AnalysisPoint[];
  }): Promise<void> {
    if (!this.isNdShell) return;

    const libId = detail.run.libraryId ? String(detail.run.libraryId) : null;
    if (libId) {
      this.pointsSource = 'library';
      this.useLibraryPoints = true;
      this.selectedLibraryIds = new Set([libId]);
      await this.ensureLibrariesLoaded();
      await this.loadLibraryPoints();
    } else {
      this.pointsSource = 'regulation';
      this.useLibraryPoints = false;
    }

    const snapshot = this.parseJsonArray(detail.run.selectedPointsSnapshot);
    const selectedNums = new Set<string>();
    for (const raw of snapshot) {
      const snap = raw as Record<string, unknown>;
      const num = String(snap['pointNumber'] ?? snap['pointId'] ?? '').trim();
      if (num) selectedNums.add(num);
    }
    if (selectedNums.size && this.govPoints.length) {
      this.selected.clear();
      for (const p of this.govPoints) {
        if (selectedNums.has(p.point_id)) this.selected.add(p.point_id);
      }
      this.sessionSelectedPointIds = new Set(selectedNums);
      this.syncSelectionToGovPoints();
    }
  }

  get showDualVerifyFailedBanner(): boolean {
    return this.showNdDualVerifyFailedBanner;
  }

  rerunAllDualVerifyFailed(): void {
    void this.retryAllNdDualVerifyFailed();
  }

  /** ND shell: type this word to confirm AI analysis (uses credits). */
  readonly ndRunConfirmPhrase = 'start';
  showNdRunConfirm = false;
  ndRunConfirmInput = '';
  ndRunConfirmTitle = 'Start analysis';
  ndRunConfirmHint = 'This run uses Landing AI and Google Gemini credits.';
  private pendingNdRunAction: (() => void | Promise<void>) | null = null;

  get ndRunConfirmReady(): boolean {
    return this.ndRunConfirmInput.trim().toLowerCase() === this.ndRunConfirmPhrase;
  }

  requestNdRunConfirm(
    title: string,
    hint: string,
    action: () => void | Promise<void>,
  ): void {
    if (!this.isNdShell) {
      void action();
      return;
    }
    this.ndRunConfirmTitle = title;
    this.ndRunConfirmHint = hint;
    this.ndRunConfirmInput = '';
    this.pendingNdRunAction = action;
    this.showNdRunConfirm = true;
  }

  confirmNdRun(): void {
    if (!this.ndRunConfirmReady) {
      this.toast.show(`Type "${this.ndRunConfirmPhrase}" to confirm`, 'error', 3000);
      return;
    }
    this.showNdRunConfirm = false;
    const action = this.pendingNdRunAction;
    this.pendingNdRunAction = null;
    this.ndRunConfirmInput = '';
    if (action) void action();
  }

  cancelNdRunConfirm(): void {
    this.showNdRunConfirm = false;
    this.pendingNdRunAction = null;
    this.ndRunConfirmInput = '';
  }

  onNdRunConfirmKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && this.ndRunConfirmReady) {
      event.preventDefault();
      this.confirmNdRun();
    }
  }

  runAnalysisAndScroll(): void {
    if (this.isNdShell) {
      this.requestNdRunConfirm(
        'Start analysis',
        'Type start to run Landing AI + dual verify on all selected points.',
        () => this.runNdShellAnalysis().then(() => this.scrollToWorkspace()),
      );
      return;
    }
    if (this.runBlockedReason) {
      this.runAnalysis();
      return;
    }
    this.runAnalysis();
    this.scrollToWorkspace();
  }

  override get canRun(): boolean {
    if (this.isNdShell) {
      return (
        (this.selectedComplianceIds.size > 0 || !!this.complianceFile) &&
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
    return super.canRun;
  }

  override get runBlockedReason(): string | null {
    if (this.isNdShell) {
      if (this.uploadingCompliance || this.attachingCompliance) {
        return 'Wait for the compliance document to finish attaching.';
      }
      if (this.selectedComplianceIds.size === 0 && !this.complianceFile) {
        return 'Select or upload at least one internal (policy) document.';
      }
      if (this.selected.size === 0) return 'Select at least one regulation point.';
      if (this.govPoints.length === 0) return 'Load regulation points first.';
      if (this.loadingPoints) return 'Regulation points are still loading.';
      if (this.uploadingReg) return 'Wait for the regulation upload to finish.';
      if (this.loadingCompliance) return 'Internal documents are still loading.';
      return null;
    }
    return super.runBlockedReason;
  }

  /** ND-only: persist run in nd_analysis_runs and execute via NdAnalysisProcessor (same Landing AI + Gemini stack). */
  private async runNdShellAnalysis(): Promise<void> {
    const blocked = this.runBlockedReason;
    if (blocked) {
      this.error = blocked;
      this.toast.show(blocked, 'error', 3000);
      return;
    }

    const selectedIds = this.comparableSelectedIds();
    if (!selectedIds.length) {
      this.error = 'Select at least one comparable regulation point.';
      this.toast.show(this.error, 'error', 3000);
      return;
    }

    if (this.ndRunId) {
      const statusRes = await this.ndApi.getAnalysisRunStatus(this.ndRunId);
      if (statusRes.success && statusRes.data) {
        const data = statusRes.data as {
          status: string;
          processedPointsCount: number;
          totalPointsCount: number;
        };
        const st = String(data.status).toLowerCase();
        const incomplete =
          data.totalPointsCount > 0 && data.processedPointsCount < data.totalPointsCount;
        if (st === 'draft' || incomplete || st === 'failed') {
          await this.launchNdAnalysisRun(this.ndRunId, selectedIds);
          return;
        }
      }
    }

    const createRes = await this.ndApi.createAnalysisRun(this.buildNdCreateRunPayload(selectedIds));
    if (!createRes.success || !createRes.data?.id) {
      this.error = createRes.message ?? 'Could not create analysis run';
      this.toast.show(this.error, 'error', 5000);
      return;
    }

    const runId = createRes.data.id;
    this.ndRunId = runId;
    await this.router.navigate(['/nd/analyse-v8'], {
      queryParams: { run: runId },
      replaceUrl: true,
    });
    await this.launchNdAnalysisRun(runId, selectedIds);
  }

  private buildNdCreateRunPayload(selectedIds: string[]): Record<string, unknown> {
    const selectedSnapshot = this.buildNdPointsSnapshot(selectedIds);
    const regIds = new Set<string>();
    for (const doc of this.selectedRegDocs) {
      if (doc.id) regIds.add(doc.id);
    }
    for (const snap of selectedSnapshot) {
      const docId = (snap as Record<string, unknown>)['regulationDocumentId'];
      if (typeof docId === 'string' && docId) regIds.add(docId);
    }
    if (!regIds.size && this.libraryPrimaryRegDocId) regIds.add(this.libraryPrimaryRegDocId);

    const intIds =
      this.selectedComplianceIds.size > 0
        ? [...this.selectedComplianceIds]
        : this.complianceDoc?.id
          ? [this.complianceDoc.id]
          : [];

    const complianceLabel = (
      this.complianceFileName ||
      this.complianceDoc?.originalFileName ||
      'Compliance'
    ).slice(0, 48);
    const regLabel = this.selectedRegLabel.slice(0, 120);
    const name = `${complianceLabel} × ${regLabel}`.slice(0, 240);

    const libraryId =
      this.useLibraryPoints && this.selectedLibraryIds.size === 1
        ? [...this.selectedLibraryIds][0]
        : null;

    return {
      name,
      description: null,
      libraryId,
      departmentId: null,
      selectedPointsSnapshot: selectedSnapshot,
      selectedInternalDocIds: intIds,
      selectedRegulationDocIds: [...regIds],
    };
  }

  private buildNdPointsSnapshot(selectedIds: string[]): unknown[] {
    const byPointId = new Map<string, SourcedGovPoint>();
    for (const p of this.rawGovPoints as SourcedGovPoint[]) {
      byPointId.set(p.point_id, p);
    }
    for (const p of this.govPoints) {
      if (!byPointId.has(p.point_id)) byPointId.set(p.point_id, p as SourcedGovPoint);
    }

    const docIdByPoint = new Map<string, string>();
    if (this.regulationDisplayUseDocGroups) {
      for (const doc of this.regulationDisplayDocs) {
        const rows = doc.useChapters
          ? doc.chapters.flatMap((ch) => ch.sections.flatMap((s) => s.rows))
          : doc.flatRows;
        for (const row of rows) {
          docIdByPoint.set(row.point.point_id, doc.docId);
        }
      }
    }

    return selectedIds.map((id) => {
      const p = byPointId.get(id) ?? (this.govPoints.find((g) => g.point_id === id) as SourcedGovPoint | undefined);
      if (!p) return { pointNumber: id };
      return {
        pointNumber: p.point_id,
        pointId: p.point_id,
        pointTitle: p.title ?? null,
        pointContent: p.text,
        pageReference: p.section ?? null,
        regulationPointId: p.regulationPointId ?? null,
        regulationDocumentId:
          p.docId ??
          docIdByPoint.get(id) ??
          this.selectedRegDocs[0]?.id ??
          this.libraryPrimaryRegDocId ??
          null,
      };
    });
  }

  runDemoAnalysisAndScroll(): void {
    if (this.isNdShell) {
      this.requestNdRunConfirm(
        'Start demo run',
        'Type start to play through saved demo results (no live AI on demo path).',
        () => {
          this.runDemoAnalysis();
          this.scrollToWorkspace();
        },
      );
      return;
    }
    this.runDemoAnalysis();
    this.scrollToWorkspace();
  }

  override onComplianceSelect(event: Event): void {
    if (!this.isNdShell) {
      super.onComplianceSelect(event);
      return;
    }

    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.uploadingCompliance = true;
    this.error = '';
    void this.ndApi.uploadInternalDocument(file).then((res) => {
      this.uploadingCompliance = false;
      if (res.success && res.data) {
        const row = res.data as { id: string; title?: string; originalFileName?: string; parseStatus?: string };
        this.ndInternalParseStatus.set(row.id, String(row.parseStatus ?? 'pending'));
        this.refreshComplianceDocs(() => {
          this.selectedComplianceIds.add(row.id);
          this.toast.show(`Uploaded ${file.name} — parse from Documents or at analysis time`, 'success', 3500);
        });
        return;
      }
      this.error = res.message ?? 'Upload failed';
      this.toast.show(this.error, 'error', 4000);
    }).catch(() => {
      this.uploadingCompliance = false;
      this.error = 'Upload failed';
      this.toast.show(this.error, 'error', 4000);
    });
  }

  override refreshComplianceDocs(after?: () => void): void {
    if (!this.isNdShell) {
      super.refreshComplianceDocs(after);
      return;
    }

    this.loadingCompliance = true;
    void this.ndApi.getInternalDocuments().then((res) => {
      this.loadingCompliance = false;
      const docs = (res.data ?? []) as InternalDocument[];
      this.ndInternalParseStatus = new Map(
        docs.map((d) => [d.id, String(d.parseStatus ?? 'pending')]),
      );
      this.complianceDocs = docs.map((d) => ({
        id: d.id,
        title: d.title,
        originalFileName: d.originalFileName,
        category: d.department ?? 'Compliance',
        version: d.version != null ? String(d.version) : '',
        pages: 0,
        uploaded: d.uploadedAt ?? d.uploaded,
        status: 'active',
        filter: 'document',
        fileType: 'PDF',
        docKind: 'document',
        storagePath: '',
        history: [],
        sizeBytes: d.sizeBytes ?? 0,
        fileHash: null,
      }));
      for (const id of [...this.selectedComplianceIds]) {
        if (!this.complianceDocs.some((doc) => doc.id === id)) {
          this.selectedComplianceIds.delete(id);
        }
      }
      after?.();
    }).catch(() => {
      this.loadingCompliance = false;
      this.complianceDocs = [];
      this.ndInternalParseStatus.clear();
      after?.();
    });
  }

  complianceParseLabel(docId: string): string {
    const status = (this.ndInternalParseStatus.get(docId) ?? 'pending').toLowerCase();
    if (status === 'parsed' || status === 'completed') return 'Parsed';
    if (status === 'processing') return 'Parsing…';
    if (status === 'failed') return 'Parse failed';
    return 'Not parsed';
  }

  get ndPolicyDocCatalog(): { id: string; title: string; originalFileName: string }[] {
    const ids = this.selectedComplianceIds.size
      ? [...this.selectedComplianceIds]
      : this.complianceDoc?.id
        ? [this.complianceDoc.id]
        : [];
    return ids.map((id) => {
      const doc = this.complianceDocs.find((d) => d.id === id);
      return {
        id,
        title: doc?.title ?? '',
        originalFileName: doc?.originalFileName ?? '',
      };
    });
  }

  override runRemainingPoints(): void {
    if (this.isNdShell) {
      this.requestNdRunConfirm(
        'Run remaining points',
        'Type start to process points that have not finished yet.',
        () => super.runRemainingPoints(),
      );
      return;
    }
    super.runRemainingPoints();
  }

  get selectedPointGovText(): string {
    if (!this.selectedDetailPointId) return '';
    const gov = this.govPoints.find((g) => g.point_id === this.selectedDetailPointId);
    return gov?.text?.trim() || gov?.title?.trim() || '';
  }

  private scrollToWorkspace(): void {
    window.setTimeout(() => {
      this.workspaceEl?.nativeElement?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 80);
  }

  protected override onRunResumeAttached(): void {
    this.scrollToWorkspace();
  }

  override ngOnDestroy(): void {
    this.navSub?.unsubscribe();
    document.body.classList.remove('panel-resizing');
    super.ngOnDestroy();
  }
}
