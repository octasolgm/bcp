import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRouteSnapshot, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../components/nd/nd-point-sort-controls.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { AnalyseBase, type PointPhaseDisplay } from '../shared/analyse-base';
import { analysisPointCoverageStatus } from '../../../lib/nd/analysis-point-mapper';
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
import { parsePointSnapshot, normalizePointSnapshotLabels, hydratePointSnapshotFromGov, resolveSnapshotDisplayNumber, resolveRunPointDisplayMeta, isUuidLike, type RunPointDisplayMeta } from '../../../lib/nd/utils';
import {
  formatChapterLabel,
  formatSectionGroupLabel,
  isJunkExtractPointId,
  resolveGovPointDisplayNumber,
} from '../../../lib/gov-point-filter';
import { ndComplianceSummaryFromPoints } from '../../../lib/nd/nd-run-display';
import { countDisplayGapsForAnalysisPoint } from '../../../lib/nd/cap-gap-count';
import { reviewsForPoint, type ActionItemReviewEntry, type ActionItemReviewStatus } from '../../../lib/nd/action-item-review';
import { canAddActionItemReviews, isReviewRole, reviewDisabledHint } from '../../../lib/nd/nd-review-run-helpers';
import {
  resolveAnalysisPointSeverity,
  resolvePointComplianceLabel,
  type ComplianceSeverity,
} from '../../../lib/nd/point-compliance-status';
import { type SortDir } from '../../../lib/nd/list-utils';
import { sortByPointKey, type PointSortMode } from '../../../lib/nd/point-sort';
import { sortByPointRef } from '../../../lib/nd/list-utils';
import {
  buildLibraryPointHierarchy,
  buildLibraryStoredPointDisplay,
  formatStoredAnalyseMeta,
  mapLibrarySnapshotToSourced,
  prepareLibraryPointsForAnalysis,
  type GovPointDuplicateGroup,
  type LibraryHierarchyGroup,
  type LibraryPointDisplayDoc,
  type LibraryPointDisplayChapter,
  type LibraryPointDisplayRow,
  type LibraryPointDisplayTree,
  type SourcedGovPoint,
} from '../../../lib/library-points-utils';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdGapAnalysisComponent } from '../nd/gap-analysis/nd-gap-analysis.component';
import { buildGapAnalysisExportRows } from '../../../lib/nd/export/gap-analysis-export-rows';
import { exportGapAnalysisExcelFromPoints, exportGapAnalysisPdfFromPoints, exportRegulGapAnalysisExcelFromPoints } from '../../../lib/nd/export/gap-analysis-export';

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
  selector: 'app-analyse-regul',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, InProgressNavButtonComponent, NdGapPointDetailComponent, NdPointSortControlsComponent, NdPointNumberTreeComponent, NdStatusBadgeComponent, NdGapAnalysisComponent],
  templateUrl: './analyse-regul.component.html',
  styleUrl: './analyse-regul.component.scss',
})
export class AnalyseRegulComponent extends AnalyseBase implements OnInit, OnDestroy {
  readonly versionLabel: string = 'V3 — Regul Workflow';
  readonly versionPath: string = '/analyse-regul';

  /** Workflow engine stored on ND analysis runs created from this page. */
  protected readonly regulWorkflowEngineId: string = 'regul_pipeline';
  /** Canonical ND route for this analysis page. */
  protected readonly regulAnalysisRoute: string = '/nd/analyse-regul';

  analysingPointSort: PointSortMode = 'number';
  analysingPointSortDir: SortDir = 'asc';
  analysingStatusFilter: 'all' | 'running' | 'queued' | 'failed' | 'completed' = 'all';
  pointDetailModalId: string | null = null;

  @ViewChild('workspaceEl') workspaceEl?: ElementRef<HTMLElement>;
  @ViewChild('gapReportEl') gapReportEl?: ElementRef<HTMLElement>;

  private readonly ndAuth = inject(NdAuthService);
  workflowLoading = false;
  /** Include Regul.ai qualitative assessment phase when running analysis. */
  enableQualitativeAssessment = false;
  regulWorkflowLlmSummary = '';
  /** Regul.ai-style clause review gate before Run analysis. */
  showRegulClauseReview = false;
  regulClausesConfirmed = false;
  regulClauseConfirmLoading = false;
  regulQualitativeAssessment: {
    status: string;
    result?: {
      overallRating?: string;
      overall_rating?: string;
      dimensions?: Array<{
        dimension: string;
        rating: string;
        commentary: string;
        examples?: string[];
      }>;
      strengths?: string[];
      improvementRecommendations?: string[];
      improvement_recommendations?: string[];
    };
    errorMessage?: string;
  } | null = null;
  readonly regulQualitativeDimensionLabels: Record<string, string> = {
    clarity_and_tone: 'Clarity & Tone',
    structure_and_navigation: 'Structure & Navigation',
    depth_of_implementation_detail: 'Depth of Implementation Detail',
    alignment_with_regulatory_language: 'Alignment with Regulatory Language',
    actionability_for_staff: 'Actionability for Staff',
  };
  regulClauseRows: Array<{
    pointId: string;
    pointNumber: string;
    pointTitle: string;
    pointContent: string;
  }> = [];
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
  private runPointMetaByAnalysisId = new Map<string, RunPointDisplayMeta>();
  private runPointMetaByGovKey = new Map<string, RunPointDisplayMeta>();
  private attachmentsByPointId = new Map<string, PointGapAttachment[]>();
  private reviewsByPointId = new Map<string, ActionItemReviewEntry[]>();

  setupRegSourcesPct = 58;
  setupRegInnerPct = 42;
  colLeftWidth = 340;
  colMidWidth = 280;

  isNdShell = false;
  pass2LlmSummary = '';
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
  /** Loaded from GET /nd/results — same source as gap-analysis. */
  private ndRunPointsList: AnalysisPoint[] = [];

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
    this.ndWorkflowEngine = this.regulWorkflowEngineId;
    this.refreshNdShellState();
    super.ngOnInit();
    this.canonicalizeNdAnalysisUrl();
    if (this.isNdShell) {
      void this.ensureLibrariesLoaded();
      void this.refreshRegulWorkflowLlmSummary();
    }
    if (this.activeNdRunId) void this.loadNdRunPoints(this.activeNdRunId);

    this.navSub = this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe(() => {
        const hadNdCatalog = this.useNdRegulationCatalog;
        this.refreshNdShellState();
        if (this.isNdShell) void this.ensureLibrariesLoaded();
        if (this.isNdShell) void this.refreshRegulWorkflowLlmSummary();
        if (this.useNdRegulationCatalog !== hadNdCatalog) this.refreshRegulations();
      });
  }

  private refreshNdShellState(): void {
    this.isNdShell = this.isUnderNdRoute() || this.currentPathname().startsWith('/nd/');
    this.useNdRegulationCatalog = this.isNdShell;
  }

  private async refreshRegulWorkflowLlmSummary(): Promise<void> {
    const res = await this.ndApi.getActiveRegulWorkflowLlm();
    if (!res.success || !res.data) {
      this.regulWorkflowLlmSummary = '';
      return;
    }
    this.regulWorkflowLlmSummary = `${res.data.providerLabel} · ${res.data.model}`;
  }

  protected regulRunConfirmHint(): string {
    const llm = this.regulWorkflowLlmSummary || 'admin-selected LLM';
    const qual = this.enableQualitativeAssessment
      ? 'Forward + reverse + qualitative assessment.'
      : 'Forward + reverse coverage (qualitative skipped).';
    return `Regul workflow using ${llm}. ${qual} Type start to confirm.`;
  }

  get needsComplianceDocumentSelection(): boolean {
    return this.selectedComplianceIds.size === 0 && !this.complianceFile;
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
    void this.router.navigate([this.regulAnalysisRoute], {
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

  get allFilteredLibrariesSelected(): boolean {
    const libs = this.filteredLibraries;
    return libs.length > 0 && libs.every((l) => this.selectedLibraryIds.has(l.id));
  }

  toggleAllFilteredLibraries(checked: boolean): void {
    if (checked) this.selectAllFilteredLibraries();
    else this.clearLibrarySelection();
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
    this.selected = new Set();
    this.govSourceLabel = '';
    this.libraryPrimaryRegDocId = null;
    this.librarySourceLabel = '';
    this.resetLibraryPointMeta();
    this.error = '';
  }

  get allPointsSelected(): boolean {
    const pool = this.selectableRegulationPoints();
    return pool.length > 0 && pool.every((p) => this.selected.has(p.point_id));
  }

  toggleAllPoints(checked: boolean): void {
    if (checked) this.selectAll();
    else this.clearSelection();
  }

  override selectAll(): void {
    if (this.pointsSource === 'regulation' && this.rawGovPoints.length) {
      this.selected = new Set(this.rawGovPoints.map((p) => p.point_id));
      return;
    }
    super.selectAll();
  }

  override clearSelection(): void {
    this.selected = new Set();
  }

  /**
   * Base class auto-selects every loaded point using `point_id`, but Regul checkboxes key off
   * `regulationPointId` (see selectableRegPointId). That mismatch made the panel footer show
   * "N selected" while every checkbox rendered unchecked. Start unselected instead so the count
   * always matches what's actually checked.
   */
  protected override applyGovPoints(
    points: GovPoint[],
    note: string,
    selectionOnly?: Set<string> | null,
    regulationDisplayDocs?: LibraryPointDisplayDoc[],
  ): void {
    super.applyGovPoints(points, note, selectionOnly, regulationDisplayDocs);
    if (this.pointsSource === 'regulation' && !selectionOnly?.size) {
      this.selected = new Set();
    }
  }

  override toggle(id: string): void {
    const canonical =
      this.pointsSource === 'regulation'
        ? this.resolveCanonicalRegPointId(id) ?? id
        : id;
    super.toggle(canonical);
  }

  override togglePointIds(ids: string[]): void {
    if (!ids.length) return;
    const next = new Set(this.selected);
    const all = ids.every((id) => next.has(id));
    for (const id of ids) {
      if (all) next.delete(id);
      else next.add(id);
    }
    this.selected = next;
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
    const regId = row.point.regulationPointId?.trim() || '';
    const num = (row.point.pointNumber ?? row.displayId ?? row.point.point_id)
      .trim()
      .replace(/\.$/, '');

    const match = this.govPoints.find((g) => {
      const sg = g as SourcedGovPoint;
      if (regId && (g.point_id === regId || sg.regulationPointId === regId)) return true;
      const gNum = (sg.pointNumber ?? g.section ?? '').trim().replace(/\.$/, '');
      return Boolean(num && gNum && gNum === num);
    });

    return match?.point_id ?? (regId || num || null);
  }

  /** Regulation doc points: allow selecting section headers (e.g. 3.9) as well as leaf clauses. */
  selectableRegPointId(row: LibraryPointDisplayRow): string | null {
    const p = row.point as SourcedGovPoint;
    const regId = p.regulationPointId?.trim();
    if (regId) return regId;
    const fromDisplay = this.resolveCanonicalRegPointId(p.point_id ?? row.displayId);
    if (fromDisplay) return fromDisplay;
    const id = p.point_id?.trim();
    return id || null;
  }

  /** Map UI id (clause number or UUID) to stored regulation point id used for runs. */
  private resolveCanonicalRegPointId(id: string): string | null {
    const key = id?.trim();
    if (!key) return null;
    const raw = this.rawGovPoints as SourcedGovPoint[];
    const direct = raw.find(
      (p) => p.point_id === key || (p.regulationPointId?.trim() ?? '') === key,
    );
    if (direct) return direct.point_id;

    const norm = key.replace(/\.$/, '');
    const byNum = raw.find((p) => {
      const num = (p.pointNumber ?? '').trim().replace(/\.$/, '');
      const legacy = (p.point_id ?? '').trim().replace(/\.$/, '');
      return (num.length > 0 && num === norm) || (!isUuidLike(legacy) && legacy === norm);
    });
    return byNum?.point_id ?? null;
  }

  private selectableRegulationPoints(): GovPoint[] {
    return this.rawGovPoints.length ? this.rawGovPoints : this.govPoints;
  }

  override comparableSelectedIds(): string[] {
    if (this.pointsSource === 'regulation' && this.rawGovPoints.length) {
      const ids = new Set<string>();
      for (const id of this.selected) {
        const canonical = this.resolveCanonicalRegPointId(id);
        if (canonical) ids.add(canonical);
      }
      return [...ids];
    }
    return super.comparableSelectedIds();
  }

  protected override syncSelectionToGovPoints(): void {
    if (this.pointsSource === 'regulation' && this.rawGovPoints.length) {
      const next = new Set<string>();
      for (const id of this.selected) {
        const canonical = this.resolveCanonicalRegPointId(id);
        if (canonical) next.add(canonical);
      }
      this.selected = next;
      if (this.sessionSelectedPointIds) {
        const sessionNext = new Set<string>();
        for (const id of this.sessionSelectedPointIds) {
          const canonical = this.resolveCanonicalRegPointId(id);
          if (canonical) sessionNext.add(canonical);
        }
        this.sessionSelectedPointIds = sessionNext;
      }
      if (this.progressTotal > this.rawGovPoints.length) {
        this.progressTotal =
          this.sessionSelectedPointIds?.size ?? this.comparableSelectedIds().length;
      }
      return;
    }
    super.syncSelectionToGovPoints();
  }

  override displayChapterAnalyseRows(ch: LibraryPointDisplayChapter): LibraryPointDisplayRow[] {
    if (this.pointsSource === 'library') return super.displayChapterAnalyseRows(ch);
    return ch.sections.flatMap((sec) => sec.rows);
  }

  override displaySectionAnalyseRows(
    sec: LibraryPointDisplayChapter['sections'][number],
  ): LibraryPointDisplayRow[] {
    if (this.pointsSource === 'library') return super.displaySectionAnalyseRows(sec);
    return sec.rows;
  }

  override get selectedCount(): number {
    if (this.pointsSource === 'regulation' && this.rawGovPoints.length) {
      return this.comparableSelectedIds().length;
    }
    return super.selectedCount;
  }

  get regulationPointPoolCount(): number {
    return this.pointsSource === 'regulation' && this.rawGovPoints.length
      ? this.rawGovPoints.length
      : this.govPoints.length;
  }

  override displayDocSelectedCount(doc: LibraryPointDisplayDoc): number {
    if (this.pointsSource === 'library') return super.displayDocSelectedCount(doc);
    if (doc.useChapters) {
      return doc.chapters.reduce((n, ch) => n + this.displayChapterSelectedCount(ch), 0);
    }
    return doc.flatRows.filter((r) => {
      const pid = this.selectableRegPointId(r);
      return pid != null && this.selected.has(pid);
    }).length;
  }

  override displayChapterSelectedCount(ch: LibraryPointDisplayChapter): number {
    if (this.pointsSource === 'library') return super.displayChapterSelectedCount(ch);
    return this.displayChapterAnalyseRows(ch).filter((r) => {
      const pid = this.selectableRegPointId(r);
      return pid != null && this.selected.has(pid);
    }).length;
  }

  get pointsCatalogGovPoints(): GovPoint[] {
    const source = this.rawGovPoints.length ? this.rawGovPoints : this.govPoints;
    return source.map((p) => ({
      point_id: p.point_id,
      title: p.title,
      text: p.text,
      section: p.section,
    }));
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
    for (const key of this.sessionLookupKeysForPointId(pointId)) {
      const saved = this.ndRunPointsByNumber.get(key);
      if (saved) {
        // Prefer session-built view when the saved row isn't scored yet but session has output
        if (!resolveAnalysisPointSeverity(saved)) {
          const session = this.resolveSessionPoint(pointId);
          if (session?.landingMessage || session?.llmMessage) {
            break;
          }
        }
        return saved;
      }
    }
    const cached = this.sessionPointCache.get(pointId);
    if (cached) return cached;
    for (const key of this.sessionLookupKeysForPointId(pointId)) {
      const cachedAlias = this.sessionPointCache.get(key);
      if (cachedAlias) {
        this.sessionPointCache.set(pointId, cachedAlias);
        return cachedAlias;
      }
    }
    const built = this.buildSessionAnalysisPoint(pointId);
    if (built) this.sessionPointCache.set(pointId, built);
    return built;
  }

  displayLabelForPoint(pointId: string): string {
    const label = this.analysingDisplayId(pointId);
    return label && !isUuidLike(label) ? label : '—';
  }

  get resultClauseLabel(): string {
    const id = this.selectedDetailPointId;
    if (!id) return '—';
    const meta = this.runPointMetaByGovKey.get(id);
    if (meta?.clause) return meta.clause;
    const ap = this.selectedDetailAnalysisPoint;
    if (ap) {
      const m = this.metaForAnalysisPoint(ap);
      if (m.clause) return m.clause;
    }
    const label = this.displayLabelForPoint(id);
    return label && label !== '—' ? label : '—';
  }

  override selectPointForDetail(pointId: string): void {
    super.selectPointForDetail(this.resolveDetailPointId(pointId));
  }

  private resolveDetailPointId(pointId: string): string {
    const meta = this.runPointMetaByGovKey.get(pointId);
    if (meta?.clause) return meta.clause;

    for (const m of this.runPointMetaByAnalysisId.values()) {
      if (m.analysisPointId === pointId || m.govKey === pointId) {
        if (m.clause) return m.clause;
      }
    }

    if (!isUuidLike(pointId)) return pointId;

    for (const p of this.ndRunPointsList) {
      if (p.regulationPointId === pointId || p.id === pointId) {
        const m = this.metaForAnalysisPoint(p);
        if (m.clause) return m.clause;
      }
    }

    const cat = this.govCatalog().find(
      (g) => (g as SourcedGovPoint).regulationPointId === pointId,
    );
    if (cat) {
      const num = resolveGovPointDisplayNumber(cat);
      if (num && !isUuidLike(num)) return num;
    }

    return pointId;
  }

  private isPointInRunScope(p: AnalysisPoint): boolean {
    if (!this.isRegulatoryAnalysisPoint(p)) return false;
    const scope = this.runScopeGovKeys();
    if (!scope.size) return true;
    const meta = this.metaForAnalysisPoint(p);
    return (
      scope.has(meta.govKey) ||
      scope.has(meta.clause) ||
      scope.has(p.id) ||
      (p.regulationPointId != null && scope.has(p.regulationPointId))
    );
  }

  private isRegulatoryAnalysisPoint(p: AnalysisPoint): boolean {
    if (!p.regulationPointId) return false;
    const clause = parsePointSnapshot(p.pointSnapshot).pointNumber?.trim() ?? '';
    if (clause.toUpperCase().startsWith('INT')) return false;
    return true;
  }

  protected override filterPointsForRunScope(points: AnalysisPoint[]): AnalysisPoint[] {
    return points.filter((p) => this.isRegulatoryAnalysisPoint(p));
  }

  /** Points in the current run that match checkbox selection (or active session scope). */
  private ndRunPointsInScope(): AnalysisPoint[] {
    if (!this.ndRunPointsList.length) return [];
    return this.ndRunPointsList.filter((p) => this.isPointInRunScope(p));
  }

  /** Selected regulatory clauses only — excludes INT reverse rows from the analysing list. */
  private ndRegulatoryPointsInScope(): AnalysisPoint[] {
    return this.ndRunPointsInScope().filter((p) => this.isRegulatoryAnalysisPoint(p));
  }

  private regulatoryGovKeysFromSnapshot(): Set<string> {
    const keys = new Set<string>();
    for (const raw of this.parseJsonArray(this.ndRunSelectedSnapshot)) {
      const snap = raw as Record<string, unknown>;
      for (const key of [
        snap['regulationPointId'],
        snap['pointId'],
        snap['pointNumber'],
        snap['point_id'],
      ]) {
        const s = String(key ?? '').trim();
        if (s && !s.toUpperCase().startsWith('INT')) keys.add(s);
      }
    }
    return keys;
  }

  private regulatoryRowsFromGovSelection(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
    displayId: string;
  }> {
    return this.coverageRows.filter(
      (r) =>
        r.selected &&
        !r.displayId?.toUpperCase().startsWith('INT') &&
        !r.pointId.toUpperCase().startsWith('INT'),
    );
  }

  private regulCoverageContext() {
    return {
      workflowEngine: this.ndWorkflowEngine || this.regulWorkflowEngineId,
      regulPipelinePhase: this.ndRegulPipelinePhase,
    };
  }

  private lastLoggedReverseDone = -1;

  protected override onNdRunPollMerged(data: {
    status: string;
    workflowEngine?: string;
    regulPipelinePhase?: string;
    regulClauseTotal?: number;
    regulClauseCompleted?: number;
    regulClauseFailed?: number;
    regulReverseSectionTotal?: number | null;
    regulReverseSectionCompleted?: number | null;
    regulReverseSectionFailed?: number | null;
    regulReverseSections?: Array<{ sectionRef: string; title: string; status: string }>;
    totalPointsCount: number;
    processedPointsCount: number;
  }): void {
    if (!this.isRegulPipelineRun()) return;
    this.syncRegulProgressSteps();
    const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
    const done = data.regulReverseSectionCompleted ?? 0;
    const total = data.regulReverseSectionTotal ?? 0;
    if (
      (phase === 'reverse' || phase === 'qualitative') &&
      done !== this.lastLoggedReverseDone
    ) {
      this.lastLoggedReverseDone = done;
      console.info(
        `[Regul] run ${this.ndRunId} phase=${phase} clauses=${data.regulClauseCompleted ?? data.processedPointsCount}/${data.regulClauseTotal ?? data.totalPointsCount} failed=${data.regulClauseFailed ?? 0} reverseSections=${done}/${total} intFindings=${this.regulReverseIntRows.length}`,
      );
    }
    this.cdr.markForCheck();
  }

  get showRegulReverseProgress(): boolean {
    if (!this.isRegulPipelineRun()) return false;
    const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
    return (
      this.isRegulPipelineInFlight() &&
      (phase === 'reverse' ||
        phase === 'qualitative' ||
        this.ndRegulReverseSections.length > 0 ||
        this.regulReverseIntRows.length > 0)
    );
  }

  get regulReverseProgressLabel(): string {
    const total = this.ndRegulReverseSectionTotal;
    const done = this.ndRegulReverseSectionCompleted;
    if (total > 0) {
      return `Reverse mapping internal sections (${done}/${total})`;
    }
    return 'Reverse coverage mapping';
  }

  get regulReverseSectionRows(): Array<{ sectionRef: string; title: string; status: string }> {
    return sortByPointRef(this.ndRegulReverseSections, (r) => r.sectionRef);
  }

  get regulReverseIntRows(): Array<{
    clause: string;
    title: string;
    severity: string;
    status: string;
  }> {
    return sortByPointRef(
      this.ndIntReversePoints().map((p) => {
        const meta = this.metaForAnalysisPoint(p);
        const severity = resolveAnalysisPointSeverity(p);
        return {
          clause: meta.clause || meta.govKey,
          title: meta.title,
          severity: severity ?? '',
          status: p.landingAiStatus,
        };
      }),
      (row) => row.clause,
    );
  }

  formatReverseSectionStatus(status: string): string {
    const map: Record<string, string> = {
      queued: 'Queued',
      pending: 'Queued',
      running: 'Running',
      completed: 'Done',
      failed: 'Failed',
    };
    return map[status.toLowerCase()] ?? status;
  }

  intRowSeverityLabel(severity: string): string {
    if (!severity) return '';
    return this.gapComplianceShortLabel(severity as GapSeverity);
  }

  private ndIntReversePoints(): AnalysisPoint[] {
    return this.ndRunPointsList.filter((p) => !this.isRegulatoryAnalysisPoint(p));
  }

  private syncRegulProgressSteps(): void {
    const phase = (this.ndRegulPipelinePhase || 'forward').toLowerCase();
    const running = this.isRegulPipelineInFlight();
    const labels = [
      'Parsing internal policy sections',
      'Forward judgment (per clause)',
      this.ndRegulReverseSectionTotal > 0
        ? `Reverse coverage mapping (${this.ndRegulReverseSectionCompleted}/${this.ndRegulReverseSectionTotal})`
        : 'Reverse coverage mapping',
      'Gap identification',
      phase === 'qualitative' ? 'Qualitative assessment' : 'Finalizing results',
    ];
    const activeByPhase: Record<string, number> = {
      parsing: 0,
      forward: 1,
      reverse: 2,
      qualitative: 4,
      done: 5,
    };
    const activeIdx = running ? (activeByPhase[phase] ?? 1) : 5;
    this.analysisSteps.forEach((step, i) => {
      step.label = labels[i] ?? step.label;
      step.done = !running || i < activeIdx;
      step.active = running && i === activeIdx;
    });
    if (!running) {
      this.progress = 100;
      return;
    }
    const forwardTotal = this.ndRegulatoryPointsInScope().length || this.progressTotal || 1;
    const forwardDone = this.ndRegulatoryPointsInScope().filter(
      (p) => p.landingAiStatus === 'completed',
    ).length;
    let pct = 12;
    if (phase === 'parsing') {
      pct = 10;
    } else if (phase === 'forward') {
      pct = 15 + Math.round((forwardDone / forwardTotal) * 35);
    } else if (phase === 'reverse') {
      const total = this.ndRegulReverseSectionTotal || 1;
      const done = this.ndRegulReverseSectionCompleted;
      pct = 50 + Math.round((done / total) * 35);
    } else if (phase === 'qualitative') {
      pct = 90;
    }
    this.progress = Math.min(95, Math.max(10, pct));
  }

  /** Gov keys for the clauses the user intends to analyze (checkboxes → snapshot → session). */
  private runScopeGovKeys(): Set<string> {
    const fromSnapshot = this.regulatoryGovKeysFromSnapshot();
    if (fromSnapshot.size > 0 && this.ndRunId && this.ndRunStatus !== 'draft') {
      return fromSnapshot;
    }
    const fromCheckboxes = this.comparableSelectedIds();
    if (
      fromCheckboxes.length > 0 &&
      (this.ndRunStatus === 'draft' || !this.ndRunId || this.analysisState === 'idle')
    ) {
      return new Set(fromCheckboxes);
    }
    const regulatory = this.ndRegulatoryPointsInScope();
    if (regulatory.length) {
      const keys = new Set<string>();
      for (const p of regulatory) {
        const meta = this.metaForAnalysisPoint(p);
        keys.add(meta.govKey);
        if (meta.clause) keys.add(meta.clause);
      }
      return keys;
    }
    if (this.sessionSelectedPointIds?.size) {
      return new Set(
        [...this.sessionSelectedPointIds].filter((k) => !k.toUpperCase().startsWith('INT')),
      );
    }
    const keys = new Set<string>();
    for (const p of this.ndRunPointsList) {
      if (!this.isRegulatoryAnalysisPoint(p)) continue;
      keys.add(this.metaForAnalysisPoint(p).govKey);
    }
    return keys;
  }

  private async ensureDraftRunMatchesSelection(selectedIds: string[]): Promise<boolean> {
    if (!this.ndRunId || this.ndRunStatus !== 'draft') return true;

    const runKeys = new Set(
      this.ndRunPointsList.map((p) => this.metaForAnalysisPoint(p).govKey),
    );
    const matches =
      selectedIds.length === this.ndRunPointsList.length &&
      selectedIds.every((id) => runKeys.has(id));

    if (matches) return true;

    const createRes = await this.ndApi.createAnalysisRun(this.buildNdCreateRunPayload(selectedIds));
    if (!createRes.success || !createRes.data?.id) {
      this.error = createRes.message ?? 'Could not create analysis run for selected clauses';
      this.toast.show(this.error, 'error', 5000);
      return false;
    }

    this.ndRunId = createRes.data.id;
    this.regulClausesConfirmed = false;
    this.showRegulClauseReview = false;
    await this.loadNdRunPoints(this.ndRunId);
    this.toast.show(
      `Run updated to ${selectedIds.length} selected clause(s)`,
      'success',
      3000,
    );
    return true;
  }

  override get runScopePointIds(): Set<string> | null {
    const keys = this.runScopeGovKeys();
    if (keys.size) return keys;
    return super.runScopePointIds;
  }

  override get analysingListRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
    displayId: string;
  }> {
    const regulatoryPoints = this.ndRegulatoryPointsInScope();
    if (!regulatoryPoints.length) {
      const govRows = this.regulatoryRowsFromGovSelection();
      if (govRows.length) return govRows;
      if (!this.ndRunPointsList.length) return super.analysingListRows;
      return [];
    }

    return regulatoryPoints.map((p) => {
      const meta = this.metaForAnalysisPoint(p);
      const govKey = meta.govKey;
      const status =
        this.resolveSessionPointStatus(govKey) ??
        this.resolveSessionPointStatus(p.id) ??
        this.resolveSessionPointStatus(p.regulationPointId ?? '') ??
        analysisPointCoverageStatus(p, this.ndRunStatus, this.regulCoverageContext());
      return {
        pointId: govKey,
        title: meta.title,
        status,
        selected: this.runScopeGovKeys().has(govKey),
        displayId: meta.clause,
      };
    });
  }

  override get analysingListDone(): number {
    if (this.isRegulPipelineInFlight()) {
      return this.ndRegulatoryPointsInScope().filter((p) => p.landingAiStatus === 'completed').length;
    }
    return super.analysingListDone;
  }

  override canShowPointRerunActions(pointId: string): boolean {
    if (this.isRegulPipelineInFlight()) return false;
    const ap = this.analysisPointForPointId(pointId);
    if (ap && !ap.regulationPointId) return false;
    return super.canShowPointRerunActions(pointId);
  }

  override formatCoverageStatus(status: string): string {
    if (status === 'running' && this.isRegulPipelineInFlight()) {
      const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
      if (phase === 'parsing') return 'Parsing docs…';
      if (phase === 'forward') return 'Forward…';
      if (phase === 'reverse') return 'Reverse…';
      if (phase === 'qualitative') return 'Qualitative…';
      return 'In progress';
    }
    return super.formatCoverageStatus(status);
  }

  override getPointPhaseStatus(pointId: string): PointPhaseDisplay | null {
    const ap = this.analysisPointForPointId(pointId);
    if (ap && !ap.regulationPointId) {
      const reverseDone = ap.landingAiStatus === 'completed';
      return {
        phase1: { label: 'Forward', state: 'skip' },
        phase2: {
          label: 'Reverse',
          state: reverseDone ? 'ok' : ap.landingAiStatus === 'failed' ? 'fail' : 'idle',
        },
      };
    }

    if (this.isRegulPipelineRun()) {
      const forwardDone = ap?.landingAiStatus === 'completed';
      const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
      const inFlight = this.isRegulPipelineInFlight();
      let forwardState: 'ok' | 'running' | 'idle' | 'fail' | 'skip' = 'idle';
      if (forwardDone) forwardState = 'ok';
      else if (inFlight && phase === 'forward') forwardState = 'running';
      else if (ap?.landingAiStatus === 'failed') forwardState = 'fail';

      let reverseState: 'ok' | 'running' | 'idle' | 'fail' | 'skip' = 'idle';
      if (forwardDone) {
        if (inFlight && (phase === 'reverse' || phase === 'qualitative')) reverseState = 'running';
        else if (!inFlight || phase === 'done') reverseState = 'ok';
      }

      return {
        phase1: { label: 'Forward', state: forwardState },
        phase2: {
          label: phase === 'qualitative' ? 'Qualitative' : 'Reverse',
          state: reverseState,
        },
      };
    }

    if (ap?.regulationPointId && ap.googleAiStatus === 'skipped' && ap.dualVerifyStatus === 'completed') {
      const p = this.resolveSessionPoint(pointId);
      const forwardDone = ap.landingAiStatus === 'completed' || Boolean(p?.landingMessage?.trim());
      const running = p?.status === 'running' || p?.status === 'processing' || p?.status === 'queued';
      return {
        phase1: {
          label: 'Forward',
          state: forwardDone ? 'ok' : running ? 'running' : 'idle',
        },
        phase2: { label: 'Reverse', state: 'skip' },
      };
    }

    const base = super.getPointPhaseStatus(pointId);
    if (!base) {
      const ap = this.analysisPointForPointId(pointId);
      if (ap && analysisPointCoverageStatus(ap, this.ndRunStatus) === 'not-run') {
        return {
          phase1: { label: 'Forward', state: 'idle' },
          phase2: { label: 'Reverse', state: 'idle' },
        };
      }
      return null;
    }
    return {
      phase1: { ...base.phase1, label: 'Forward' },
      phase2: { ...base.phase2, label: 'Reverse' },
    };
  }

  override get analysingListTotal(): number {
    if (this.ndRunPointsList.length) return this.ndRegulatoryPointsInScope().length;
    return super.analysingListTotal;
  }

  get regulRerunReverseLabel(): string {
    const n = this.phase2RetryCount;
    return n > 0 ? `Rerun reverse (${n})` : 'Rerun reverse (0)';
  }

  get canRerunForwardOnly(): boolean {
    if (!this.isRegulPipelineRun() || !this.hasResumableRun || !this.ndRunId) return false;
    const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
    const reverseTotal = this.ndRegulReverseSectionTotal;
    const reverseDone = this.ndRegulReverseSectionCompleted;
    const reverseComplete = reverseTotal > 0 && reverseDone >= reverseTotal;
    return reverseComplete || phase === 'done' || this.regulReverseIntRows.length > 0;
  }

  rerunForwardOnlyWithConfirm(): void {
    this.requestNdRunConfirm(
      'Rerun forward only (keep reverse)',
      'Type start to rerun forward judgments for all regulatory clauses. Reverse mappings and INT rows stay in the database.',
      () => this.rerunForwardOnly(),
    );
  }

  protected async rerunForwardOnly(): Promise<void> {
    if (!this.ndRunId) return;
    this.retryingPointId = '__batch__';
    this.analysisState = 'running';
    const res = await this.ndApi.rerunForwardOnly(this.ndRunId);
    this.retryingPointId = null;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not start forward-only rerun', 'error');
      return;
    }
    this.toast.show('Forward-only rerun started (reverse preserved)', 'success', 2200);
    this.pollNdRun(this.ndRunId);
  }

  override analysingDisplayId(pointId: string): string {
    const cached = this.runPointMetaByGovKey.get(pointId);
    if (cached?.clause) return cached.clause;
    for (const meta of this.runPointMetaByAnalysisId.values()) {
      if (meta.govKey === pointId || meta.analysisPointId === pointId) {
        return meta.clause;
      }
    }
    const ap = this.analysisPointForPointId(pointId);
    if (ap) {
      const meta = this.metaForAnalysisPoint(ap);
      if (meta.clause) return meta.clause;
    }
    const fromSuper = super.analysingDisplayId(pointId);
    return fromSuper && !isUuidLike(fromSuper) ? fromSuper : '';
  }

  protected override sessionLookupKeysForPointId(pointId: string): string[] {
    const keys = new Set(super.sessionLookupKeysForPointId(pointId));
    const meta = this.runPointMetaByGovKey.get(pointId);
    if (meta) {
      keys.add(meta.analysisPointId);
      keys.add(meta.govKey);
      if (meta.clause) keys.add(meta.clause);
    }
    for (const m of this.runPointMetaByAnalysisId.values()) {
      if (m.govKey === pointId || m.analysisPointId === pointId || m.clause === pointId) {
        keys.add(m.analysisPointId);
        keys.add(m.govKey);
        if (m.clause) keys.add(m.clause);
      }
    }
    return [...keys];
  }

  private govCatalog(): SourcedGovPoint[] {
    const seen = new Set<string>();
    const out: SourcedGovPoint[] = [];
    for (const p of [...this.govPoints, ...this.rawGovPoints] as SourcedGovPoint[]) {
      const key = `${p.point_id}|${p.regulationPointId ?? ''}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(p);
    }
    return out;
  }

  private metaForAnalysisPoint(p: AnalysisPoint): RunPointDisplayMeta {
    const cached = this.runPointMetaByAnalysisId.get(p.id);
    if (cached) return cached;
    const meta = resolveRunPointDisplayMeta(p, this.govCatalog());
    this.runPointMetaByAnalysisId.set(p.id, meta);
    this.runPointMetaByGovKey.set(meta.govKey, meta);
    if (meta.clause) this.runPointMetaByGovKey.set(meta.clause, meta);
    return meta;
  }

  private reconcileRunWithGovCatalog(): void {
    this.runPointMetaByAnalysisId.clear();
    this.runPointMetaByGovKey.clear();
    if (!this.ndRunPointsList.length) return;

    const govKeys = new Set<string>();
    for (const p of this.ndRunPointsList) {
      if (!this.isRegulatoryAnalysisPoint(p)) continue;
      const meta = this.metaForAnalysisPoint(p);
      this.snapshotByPointId.set(p.id, meta.snapshot);
      govKeys.add(meta.govKey);

      this.ndRunPointsByNumber.set(p.id, p);
      this.ndRunPointsByNumber.set(meta.govKey, p);
      if (meta.clause) this.ndRunPointsByNumber.set(meta.clause, p);
      if (p.regulationPointId) this.ndRunPointsByNumber.set(p.regulationPointId, p);

      const mapped =
        this.sessionPointResults.get(p.id) ??
        this.sessionPointResults.get(p.regulationPointId ?? '') ??
        this.sessionPointResults.get(meta.govKey);
      if (mapped) {
        this.sessionPointResults.set(meta.govKey, mapped);
        if (meta.clause) this.sessionPointResults.set(meta.clause, mapped);
        const status =
          this.sessionPointStatus.get(p.id) ??
          this.sessionPointStatus.get(p.regulationPointId ?? '');
        if (status) {
          this.sessionPointStatus.set(meta.govKey, status);
          if (meta.clause) this.sessionPointStatus.set(meta.clause, status);
        }
      }
    }

    if (govKeys.size) {
      this.sessionSelectedPointIds = new Set(govKeys);
      if (this.ndRunStatus !== 'draft' || this.selected.size === 0) {
        this.selected = new Set(govKeys);
      }
    }
  }

  openPointDetailModal(pointId: string, event: Event): void {
    event.stopPropagation();
    this.pointDetailModalId = pointId;
  }

  closePointDetailModal(): void {
    this.pointDetailModalId = null;
  }

  pointDetailModalSnapshot(): PointSnapshot | null {
    if (!this.pointDetailModalId) return null;
    return this.pointSnapshotForPointId(this.pointDetailModalId);
  }

  pointDetailModalGov(): GovPoint | undefined {
    if (!this.pointDetailModalId) return undefined;
    return this.govPoints.find((g) => g.point_id === this.pointDetailModalId);
  }

  protected override pointSnapshotForPointId(pointId: string): PointSnapshot | null {
    const meta = this.runPointMetaByGovKey.get(pointId);
    if (meta?.snapshot) return meta.snapshot;

    const point = this.analysisPointForPointId(pointId);
    if (!point) return null;
    const cached = this.snapshotByPointId.get(point.id);
    if (cached) return cached;
    const raw = parsePointSnapshot(point.pointSnapshot);
    const gov = this.govPointForAnalysis(pointId);
    const snap = hydratePointSnapshotFromGov(
      raw,
      gov,
      point.regulationPointId || pointId,
    );
    this.snapshotByPointId.set(point.id, snap);
    return snap;
  }

  private govPointForAnalysis(pointId: string): SourcedGovPoint | undefined {
    const all = [
      ...(this.govPoints as SourcedGovPoint[]),
      ...(this.rawGovPoints as SourcedGovPoint[]),
    ];

    const direct = all.find((g) => g.point_id === pointId);
    if (direct) return direct;

    const byDisplay = all.find((g) => resolveGovPointDisplayNumber(g) === pointId);
    if (byDisplay) return byDisplay;

    const saved = this.ndRunPointsByNumber.get(pointId);
    const snap = saved ? parsePointSnapshot(saved.pointSnapshot) : null;
    const clause =
      (snap?.pointNumber && !this.looksLikeUuid(snap.pointNumber)
        ? snap.pointNumber.trim()
        : '') || resolveSnapshotDisplayNumber(snap ?? {}, saved?.regulationPointId ?? pointId);

    if (clause) {
      const byClause = all.find((g) => resolveGovPointDisplayNumber(g) === clause);
      if (byClause) return byClause;
    }

    const snapText = (snap?.pointContent ?? '').trim();
    if (snapText.length >= 20) {
      const needle = snapText.slice(0, 48);
      const byText = all.find((g) => (g.text ?? '').trim().includes(needle));
      if (byText) return byText;
    }

    if (this.looksLikeUuid(pointId) || saved?.regulationPointId) {
      const regId = saved?.regulationPointId ?? pointId;
      const matches = all.filter((g) => g.regulationPointId === regId);
      if (matches.length === 1) return matches[0];
      if (clause) {
        const narrowed = matches.find((g) => resolveGovPointDisplayNumber(g) === clause);
        if (narrowed) return narrowed;
      }
      if (snapText) {
        const narrowed = matches.find((g) =>
          (g.text ?? '').trim().includes(snapText.slice(0, 40)),
        );
        if (narrowed) return narrowed;
      }
    }

    return all.find(
      (g) => g.regulationPointId === pointId || g.pointNumber === pointId,
    );
  }

  gapCountForPointId(pointId: string): number {
    const point = this.analysisPointForPointId(pointId);
    if (!point) return 0;
    if (this.isRegulPipelineInFlight() && point.regulationPointId) return 0;
    const attachments = this.attachmentsForPoint(pointId).length;
    let count = countDisplayGapsForAnalysisPoint(point, attachments);
    if (count > 0) return count;
    // Live poll can leave a stale pending row in ndRunPointsByNumber while session
    // already has Landing text — rebuild from session so the gap badge survives.
    const sev = this.getPointGapSeverity(pointId);
    if (!sev || sev === 'compliant') return 0;
    if (resolveAnalysisPointSeverity(point)) return 0;
    const rebuilt = this.buildSessionAnalysisPoint(pointId);
    if (!rebuilt) return 0;
    this.sessionPointCache.set(pointId, rebuilt);
    return countDisplayGapsForAnalysisPoint(rebuilt, attachments);
  }

  gapCountForGapItem(item: GapItemData): number {
    const fromPoint = this.gapCountForPointId(this.gapItemPointId(item));
    if (fromPoint > 0) return fromPoint;
    return item.gapCount ?? 0;
  }

  override getPointGapSeverity(pointId: string): GapSeverity | null {
    for (const key of this.sessionLookupKeysForPointId(pointId)) {
      const saved = this.ndRunPointsByNumber.get(key);
      if (!saved) continue;
      const sev = resolveAnalysisPointSeverity(saved);
      if (sev) return sev;
    }
    return super.getPointGapSeverity(pointId);
  }

  get sortedAnalysingRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
    displayId: string;
    isInt?: boolean;
  }> {
    const filtered = this.analysingListRows.filter((row) => {
      if (this.analysingStatusFilter === 'all') return true;
      if (this.analysingStatusFilter === 'running') {
        return row.status === 'running' || row.status === 'processing';
      }
      return row.status === this.analysingStatusFilter;
    });
    return sortByPointKey(
      filtered,
      this.analysingPointSort,
      this.analysingPointSortDir,
      (row) => row.displayId || row.pointId,
      (row) => this.getPointGapSeverity(row.pointId) ?? '',
    ).filter((row, index, arr) => arr.findIndex((r) => r.pointId === row.pointId) === index);
  }

  /** Reverse INT rows for the analysing list (below regulatory clauses). */
  get analysingListIntRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
    displayId: string;
    isInt: boolean;
  }> {
    return this.ndIntReversePoints().map((p) => {
      const meta = this.metaForAnalysisPoint(p);
      const status =
        analysisPointCoverageStatus(p, this.ndRunStatus, this.regulCoverageContext());
      return {
        pointId: p.id,
        title: meta.title,
        status,
        selected: false,
        displayId: meta.clause || meta.govKey,
        isInt: true,
      };
    });
  }

  get sortedAnalysingIntRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
    displayId: string;
    isInt: boolean;
  }> {
    const filtered = this.analysingListIntRows.filter((row) => {
      if (this.analysingStatusFilter === 'all') return true;
      if (this.analysingStatusFilter === 'running') {
        return row.status === 'running' || row.status === 'processing';
      }
      return row.status === this.analysingStatusFilter;
    });
    return sortByPointKey(
      filtered,
      this.analysingPointSort,
      this.analysingPointSortDir,
      (row) => row.displayId || row.pointId,
      (row) => this.getPointGapSeverity(row.pointId) ?? '',
    ).filter((row, index, arr) => arr.findIndex((r) => r.pointId === row.pointId) === index);
  }

  get exportableRegGapPointCount(): number {
    if (!this.ndRunId || !this.ndRunDetailPoints.length) return 0;
    return buildGapAnalysisExportRows(this.collectRegulatoryDonePointsForExport()).length;
  }

  get exportableAllGapPointCount(): number {
    if (!this.ndRunId || !this.ndRunDetailPoints.length) return 0;
    return buildGapAnalysisExportRows(this.collectAllDonePointsForExport()).length;
  }

  async exportRegGapAnalysisExcel(): Promise<void> {
    await this.exportGapAnalysisExcelForPoints(
      this.collectRegulatoryDonePointsForExport(),
      'regulatory',
    );
  }

  async exportAllGapAnalysisExcel(): Promise<void> {
    await this.exportGapAnalysisExcelForPoints(
      this.collectAllDonePointsForExport(),
      'all',
    );
  }

  private async exportGapAnalysisExcelForPoints(
    points: AnalysisPoint[],
    scope: 'regulatory' | 'all',
  ): Promise<void> {
    if (this.exportingGapReport || !this.ndRunId) return;
    const rows = buildGapAnalysisExportRows(points);
    if (!rows.length) {
      this.toast.show('No completed points with AI results to export yet', 'info');
      return;
    }
    this.exportingGapReport = true;
    this.cdr.markForCheck();
    try {
      await exportRegulGapAnalysisExcelFromPoints(points);
      const label = scope === 'all' ? 'regulatory + INT clauses' : 'regulatory clauses';
      this.toast.show(`Exported ${rows.length} ${label} to Excel`, 'success');
    } catch {
      this.toast.show('Export failed — try again', 'error');
    } finally {
      this.exportingGapReport = false;
      this.cdr.markForCheck();
    }
  }

  setAnalysingStatusFilter(filter: 'all' | 'running' | 'queued' | 'failed' | 'completed'): void {
    this.analysingStatusFilter = filter;
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

  regDocIdForPointId(pointId: string): string | null {
    const snap = this.pointSnapshotForPointId(pointId);
    return snap?.regulationDocumentId ?? this.regulationPdfDocId;
  }

  get selectedRegulationDocId(): string | null {
    if (!this.selectedDetailPointId) return this.regulationPdfDocId;
    return this.regDocIdForPointId(this.selectedDetailPointId);
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

  pointComplianceSeverity(point: AnalysisPoint): GapSeverity | null {
    return resolveAnalysisPointSeverity(point);
  }

  get regulQualitativeOverallRating(): string {
    const result = this.regulQualitativeAssessment?.result;
    if (!result) return '';
    return String(result.overallRating ?? result.overall_rating ?? '').trim();
  }

  get regulQualitativeDimensions(): Array<{
    dimension: string;
    rating: string;
    commentary: string;
    examples?: string[];
  }> {
    const dims = this.regulQualitativeAssessment?.result?.dimensions;
    return Array.isArray(dims) ? dims : [];
  }

  get regulQualitativeStrengths(): string[] {
    const s = this.regulQualitativeAssessment?.result?.strengths;
    return Array.isArray(s) ? s : [];
  }

  get regulQualitativeRecommendations(): string[] {
    const result = this.regulQualitativeAssessment?.result;
    if (!result) return [];
    const recs = result.improvementRecommendations ?? result.improvement_recommendations;
    return Array.isArray(recs) ? recs : [];
  }

  get showRegulQualitativeCard(): boolean {
    return this.regulQualitativeAssessment?.status === 'completed' && this.regulQualitativeOverallRating.length > 0;
  }

  override get inlineGapSummary(): { compliant: number; partialCompliant: number; nonCompliant: number } {
    if (this.ndRunPointsList.length) {
      if (this.isRegulPipelineInFlight()) {
        return { compliant: 0, partialCompliant: 0, nonCompliant: 0 };
      }
      return ndComplianceSummaryFromPoints(this.ndRegulatoryPointsInScope());
    }
    return super.inlineGapSummary;
  }

  async loadNdRunPoints(runId: string): Promise<void> {
    const res = await this.ndApi.getResults(runId);
    if (!res.success || !res.data) return;
    const data = res.data as {
      run: {
        status: string;
        regulClausesConfirmedAt?: string | null;
        workflowEngine?: string;
        regulPipelinePhase?: string | null;
      };
      points: AnalysisPoint[];
      pointAttachments?: PointGapAttachment[];
      actionItemReviews?: ActionItemReviewEntry[];
      regulQualitativeAssessment?: {
        status: string;
        result?: Record<string, unknown>;
        errorMessage?: string;
      } | null;
    };
    this.ndRunStatus = data.run.status;
    if (data.run.workflowEngine) this.ndWorkflowEngine = data.run.workflowEngine;
    if (data.run.regulPipelinePhase != null) {
      this.ndRegulPipelinePhase = data.run.regulPipelinePhase;
    }
    this.regulClausesConfirmed = Boolean(data.run.regulClausesConfirmedAt);
    this.regulQualitativeAssessment = data.regulQualitativeAssessment ?? null;
    this.ndRunPointsList = data.points ?? [];
    this.ndRunDetailPoints = this.ndRunPointsList;
    this.ndPointAttachments = data.pointAttachments ?? [];
    this.ndActionItemReviews = data.actionItemReviews ?? [];
    this.indexNdRunPoints(data.points ?? []);
    this.reconcileRunWithGovCatalog();
    this.fixSelectedDetailPointAfterReconcile();
    this.attachmentsByPointId.clear();
    this.reviewsByPointId.clear();

    for (const attachment of this.ndPointAttachments) {
      const list = this.attachmentsByPointId.get(attachment.analysisPointId);
      if (list) list.push(attachment);
      else this.attachmentsByPointId.set(attachment.analysisPointId, [attachment]);
    }

    for (const p of data.points) {
      if (!p.id) continue;
      this.reviewsByPointId.set(p.id, reviewsForPoint(this.ndActionItemReviews, p.id));
    }
    this.syncInlineGapSeveritiesFromNdRun();
    this.hydrateNdSessionFromPoints(data.points ?? [], {
      status: data.run.status,
      processedPointsCount: (data.run as { processedPointsCount?: number }).processedPointsCount,
      totalPointsCount: this.ndRegulatoryPointsInScope().length || data.points?.length,
    });
    this.syncRegulProgressSteps();
    if (
      this.ndRunStatus === 'draft' &&
      !this.regulClausesConfirmed &&
      this.ndRunPointsList.length > 0
    ) {
      this.showRegulClauseReview = true;
      this.buildRegulClauseRows();
    }
    this.cdr.markForCheck();
  }

  private buildRegulClauseRows(): void {
    this.regulClauseRows = sortByPointRef(
      this.ndRunPointsInScope().map((p) => {
        const snap = parsePointSnapshot(p.pointSnapshot);
        return {
          pointId: p.id,
          pointNumber: snap.pointNumber ?? '',
          pointTitle: snap.pointTitle ?? '',
          pointContent: snap.pointContent ?? '',
        };
      }),
      (row) => row.pointNumber || row.pointId,
    );
  }

  async confirmRegulClauses(): Promise<void> {
    if (!this.ndRunId || this.regulClauseConfirmLoading) return;
    this.regulClauseConfirmLoading = true;
    try {
      const clauses = this.regulClauseRows.map((row) => ({
        analysisPointId: row.pointId,
        pointNumber: row.pointNumber,
        pointTitle: row.pointTitle,
        pointContent: row.pointContent,
      }));
      const res = await this.ndApi.confirmRegulClauses(this.ndRunId, clauses);
      if (!res.success) {
        this.toast.show(res.message ?? 'Could not confirm clauses', 'error', 5000);
        return;
      }
      this.regulClausesConfirmed = true;
      this.showRegulClauseReview = false;
      await this.loadNdRunPoints(this.ndRunId);
      const selectedIds = this.ndRunPointsInScope().map((p) => this.metaForAnalysisPoint(p).govKey);
      if (selectedIds.length) {
        const forwardOnly = this.pendingNdRunForwardOnly;
        this.pendingNdRunForwardOnly = false;
        if (forwardOnly) {
          this.toast.show('Clauses confirmed — starting forward-only analysis…', 'success', 4000);
          await this.launchNdAnalysisRunForwardOnly(this.ndRunId, selectedIds);
        } else {
          this.toast.show('Clauses confirmed — starting forward/reverse analysis…', 'success', 4000);
          await this.launchNdAnalysisRun(this.ndRunId, selectedIds);
        }
        this.scrollToWorkspace();
      } else {
        this.toast.show('Clauses confirmed — select points and Run analysis', 'success', 4000);
      }
    } finally {
      this.regulClauseConfirmLoading = false;
      this.cdr.markForCheck();
    }
  }

  protected override collectDoneAnalysisPointsForExport(): AnalysisPoint[] {
    return this.collectRegulatoryDonePointsForExport();
  }

  protected collectRegulatoryDonePointsForExport(): AnalysisPoint[] {
    const done = super.collectDoneAnalysisPointsForExport();
    return done.filter((p) => this.isRegulatoryAnalysisPoint(p));
  }

  protected collectAllDonePointsForExport(): AnalysisPoint[] {
    const reg = this.collectRegulatoryDonePointsForExport();
    const seen = new Set(reg.map((p) => p.id));
    const intDone = this.ndIntReversePoints().filter((p) => {
      if (seen.has(p.id)) return false;
      if (p.landingAiStatus !== 'completed') return false;
      return Boolean(p.landingAiResult?.trim());
    });
    const combined = [...reg, ...intDone];
    combined.sort((a, b) => {
      const sa = parsePointSnapshot(a.pointSnapshot).pointNumber || a.id;
      const sb = parsePointSnapshot(b.pointSnapshot).pointNumber || b.id;
      return sa.localeCompare(sb, undefined, { numeric: true });
    });
    return combined;
  }

  override exportDoneGapAnalysisPdf(): void {
    if (this.exportingGapReport || !this.ndRunId) return;
    const points = this.collectDoneAnalysisPointsForExport();
    const rows = buildGapAnalysisExportRows(points);
    if (!rows.length) {
      this.toast.show('No completed points with AI results to export yet', 'info');
      return;
    }
    this.exportingGapReport = true;
    this.cdr.markForCheck();
    try {
      exportGapAnalysisPdfFromPoints(points, {
        runName: 'Gap Analysis Report',
        subtitle: `Regul workflow V3 · ${rows.length} point(s)`,
      });
    } finally {
      this.exportingGapReport = false;
      this.cdr.markForCheck();
    }
  }

  /** Index analysis points under § number, regulation UUID, and analysis-point id. */
  private indexNdRunPoints(points: AnalysisPoint[]): void {
    this.ndRunPointsByNumber.clear();
    this.sessionPointCache.clear();
    this.snapshotByPointId.clear();

    for (const p of points) {
      const raw = parsePointSnapshot(p.pointSnapshot);
      const gov = this.govPointForAnalysis(
        p.regulationPointId ?? raw.regulationPointId ?? p.id ?? '',
      );
      const snap = hydratePointSnapshotFromGov(
        raw,
        gov,
        p.regulationPointId ?? raw.regulationPointId,
      );
      const keys = new Set<string>();
      const displayNum = resolveSnapshotDisplayNumber(snap, p.regulationPointId);
      if (displayNum) keys.add(displayNum);
      if (snap.pointNumber?.trim() && !this.looksLikeUuid(snap.pointNumber)) {
        keys.add(snap.pointNumber.trim());
      }
      if (snap.regulationPointId?.trim()) keys.add(snap.regulationPointId.trim());
      if (snap.pageReference?.trim()) keys.add(snap.pageReference.trim());
      if (p.regulationPointId?.trim()) keys.add(p.regulationPointId.trim());
      if (p.id) keys.add(p.id);
      for (const key of keys) this.ndRunPointsByNumber.set(key, p);
      if (p.id) this.snapshotByPointId.set(p.id, snap);

      // Also index under current gov list ids (library remaps point_id).
      for (const g of this.govPoints) {
        const gNum = resolveGovPointDisplayNumber(g as SourcedGovPoint);
        const sourced = g as SourcedGovPoint;
        const matches =
          (displayNum && gNum === displayNum) ||
          (g.point_id === p.id) ||
          (p.regulationPointId &&
            (g.point_id === p.regulationPointId || sourced.regulationPointId === p.regulationPointId));
        if (matches) this.ndRunPointsByNumber.set(g.point_id, p);
      }
    }

    if (this.govCatalog().length) {
      this.reconcileRunWithGovCatalog();
    }
  }

  private fixSelectedDetailPointAfterReconcile(): void {
    const current = this.selectedDetailPointId;
    if (!current) return;

    const direct = this.runPointMetaByGovKey.get(current);
    if (direct?.clause) {
      this.selectedDetailPointId = direct.clause;
      return;
    }

    for (const p of this.ndRunPointsList) {
      if (p.id === current || p.regulationPointId === current) {
        const meta = this.metaForAnalysisPoint(p);
        if (meta.clause) {
          this.selectedDetailPointId = meta.clause;
          return;
        }
      }
    }

    const ap = this.analysisPointForPointId(current);
    if (ap) {
      const meta = this.metaForAnalysisPoint(ap);
      if (meta.clause) {
        this.selectedDetailPointId = meta.clause;
      }
    }
  }

  protected override onNdRunAttached(runId: string, points: AnalysisPoint[]): void {
    if (points.length) {
      this.ndRunPointsList = points;
      this.indexNdRunPoints(points);
      this.reconcileRunWithGovCatalog();
      this.fixSelectedDetailPointAfterReconcile();
    }
    void this.loadNdRunPoints(runId);
  }

  protected override getPollMergeBasePoints(): AnalysisPoint[] {
    return this.ndRunDetailPoints.length ? this.ndRunDetailPoints : this.ndRunPointsList;
  }

  /** Keep gap/severity badges in sync while /status poll merges live Landing results. */
  protected override onNdRunPointsLiveUpdate(points: AnalysisPoint[]): void {
    if (points.length) {
      this.ndRunPointsList = this.mergeNdRunPoints(this.ndRunPointsList, points);
      this.ndRunDetailPoints = this.ndRunPointsList;
      this.indexNdRunPoints(this.ndRunPointsList);
      this.reconcileRunWithGovCatalog();
      this.fixSelectedDetailPointAfterReconcile();
    }
    this.sessionPointCache.clear();
    if (this.isRegulPipelineRun()) {
      this.syncRegulProgressSteps();
    }
    this.cdr.markForCheck();
  }

  private syncInlineGapSeveritiesFromNdRun(): void {
    if (!this.inlineGapItems.length) return;
    for (const item of this.inlineGapItems) {
      const pointId = this.gapItemPointId(item);
      const saved = this.ndRunPointsByNumber.get(pointId) ?? this.analysisPointForPointId(pointId);
      if (!saved) continue;
      const attachmentCount = this.attachmentsByPointId.get(saved.id)?.length ?? 0;
      const severity = resolveAnalysisPointSeverity(saved);
      if (severity) item.severity = severity;
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
      if (!severity || severity === 'compliant') continue;
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
    const res = await this.ndApi.rerunPoint(runId, ndPoint.id, opts);
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
    const res = await this.ndApi.rerunPoint(runId, ndPoint.id, opts);
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
    const session = this.resolveSessionPoint(pointId);
    if (!session) return null;
    const landing = session.landingMessage ?? '';
    const llm = session.llmMessage ?? '';
    // Still show Result for completed/failed points even if AI text was not hydrated yet.
    if (!landing && !llm && !session.errorMessage && session.status === 'not-run') return null;
    const gov = this.govPointForAnalysis(pointId);
    const primary = (llm || landing).trim();
    const block = primary ? parseReferenceComplianceBlock(primary) : null;
    const cap =
      block?.correctiveAction?.trim() ||
      this.inlineGapItems.find((i) => this.gapItemPointId(i) === pointId)?.gaps?.trim() ||
      '';
    const fromBlock = normalizeStatusToSeverity(block?.status ?? '');
    const fromSession = this.sessionPointSeverityHint(pointId);
    const sev = fromBlock ?? fromSession;
    const scoredStatus =
      sev ??
      (session.status === 'failed' ? null : landing || llm ? 'partial_compliant' : null);
    return {
      id: session.id || pointId,
      regulationPointId: session.pointId !== pointId ? session.pointId : pointId,
      pointSnapshot: JSON.stringify({
        pointNumber:
          resolveGovPointDisplayNumber(gov as SourcedGovPoint) ||
          resolveSnapshotDisplayNumber(
            {
              pointNumber: (gov as SourcedGovPoint)?.pointNumber,
              pointTitle: gov?.title ?? session.pointTitle ?? undefined,
              pointContent: gov?.text ?? undefined,
              pageReference: gov?.section ?? undefined,
              regulationPointId: session.pointId || pointId,
            },
            session.pointId || pointId,
          ) ||
          null,
        pointTitle: gov?.title ?? session.pointTitle ?? undefined,
        pointContent: gov?.text ?? undefined,
        pageReference: gov?.section ?? undefined,
        regulationDocumentId: this.regulationPdfDocId,
        regulationPointId: session.pointId || pointId,
      }),
      landingAiStatus: scoredStatus ?? (session.status === 'failed' ? 'failed' : 'pending'),
      landingAiResult: landing ? JSON.stringify({ message: landing }) : null,
      googleAiStatus: llm ? scoredStatus ?? 'pending' : 'pending',
      googleAiResult: llm ? JSON.stringify({ message: llm }) : null,
      dualVerifyStatus: session.agreementJson ? 'passed' : 'skipped',
      finalStatus: sev,
      finalActionPlan: cap && cap !== 'N/A' && cap !== '—' ? cap : null,
      originalAiActionPlan: cap && cap !== 'N/A' && cap !== '—' ? cap : null,
      landingAiError: session.errorMessage ?? null,
    };
  }

  /** Severity from session report path without touching ndRunPointsByNumber (avoids recursion). */
  private sessionPointSeverityHint(pointId: string): GapSeverity | null {
    return super.getPointGapSeverity(pointId);
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
      for (const lib of this.libraryDisplayTree) {
        this.libraryComparableCounts.set(lib.libraryId, lib.analyseCount);
      }
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

      this.selected = new Set(prepared.unique.map((p) => p.point_id));

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

      if (this.ndRunPointsList.length) {
        this.indexNdRunPoints(this.ndRunPointsList);
        this.fixSelectedDetailPointAfterReconcile();
      }
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

    if (detail.points?.length) {
      this.ndRunPointsList = detail.points;
    }

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

    this.reconcileRunWithGovCatalog();
    this.fixSelectedDetailPointAfterReconcile();
  }

  get showDualVerifyFailedBanner(): boolean {
    return false;
  }

  override get phase2RetryCount(): number {
    return 0;
  }

  override rerunPointPhase2(pointId: string): void {
    this.toast.show('Regul reverse mapping runs on internal sections, not per clause', 'info', 4000);
  }

  protected override async retryAllNdPhase2(): Promise<void> {
    if (!this.ndRunId) return;
    this.retryingPointId = '__batch__';
    this.analysisState = 'running';
    const res = await this.ndApi.rerunAllFailedDualVerify(this.ndRunId);
    this.retryingPointId = null;
    if (!res.success) {
      this.toast.show(res.message ?? 'Could not start reverse pass rerun', 'error');
      return;
    }
    this.toast.show('Reverse pass rerun started', 'success', 2200);
    this.pollNdRun(this.ndRunId);
  }

  rerunAllDualVerifyFailed(): void {
    void this.retryAllNdPhase2();
  }

  /** ND shell: type this word to confirm AI analysis (uses credits). */
  readonly ndRunConfirmPhrase = 'start';
  showNdRunConfirm = false;
  ndRunConfirmInput = '';
  ndRunConfirmTitle = 'Start analysis';
  ndRunConfirmHint = 'This run uses the Regul.ai workflow pipeline.';
  private pendingNdRunAction: (() => void | Promise<void>) | null = null;
  /** After clause confirm, start forward-only instead of full pipeline. */
  private pendingNdRunForwardOnly = false;

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
      this.pendingNdRunForwardOnly = false;
      this.requestNdRunConfirm(
        'Start Regul workflow analysis',
        this.regulRunConfirmHint(),
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

  runForwardOnlyAndScroll(): void {
    if (!this.isNdShell) return;
    this.pendingNdRunForwardOnly = true;
    this.requestNdRunConfirm(
      'Start forward-only analysis',
      'Runs regulatory clause judgment only — skips reverse coverage (internal sections) and qualitative review.',
      () => this.runNdShellForwardOnlyAnalysis().then(() => this.scrollToWorkspace()),
    );
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
      if (this.ndRunId && this.ndRunStatus === 'draft' && !this.regulClausesConfirmed) {
        return 'Confirm regulatory clauses in the review panel before running analysis.';
      }
      return null;
    }
    return super.runBlockedReason;
  }

  /** ND shell: Regul workflow V3 — forward/reverse via NdRegulAnalysisProcessor (admin LLM). */
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

    if (this.ndRunId && this.ndRunStatus === 'draft') {
      const synced = await this.ensureDraftRunMatchesSelection(selectedIds);
      if (!synced) return;
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
          await this.loadNdRunPoints(this.ndRunId);
          if (!this.regulClausesConfirmed) {
            this.showRegulClauseReview = true;
            this.buildRegulClauseRows();
            this.toast.show('Review and confirm clauses, then Run again', 'info', 5000);
            return;
          }
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
    await this.router.navigate([this.regulAnalysisRoute], {
      queryParams: { run: runId },
      replaceUrl: true,
    });
    await this.loadNdRunPoints(runId);
    this.showRegulClauseReview = true;
    this.buildRegulClauseRows();
    this.toast.show('Review clauses and confirm before running analysis', 'info', 5000);
  }

  /** ND shell: forward judgment only — no reverse or qualitative phases. */
  private async runNdShellForwardOnlyAnalysis(): Promise<void> {
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

    if (this.ndRunId && this.ndRunStatus === 'draft') {
      const synced = await this.ensureDraftRunMatchesSelection(selectedIds);
      if (!synced) return;
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
          await this.loadNdRunPoints(this.ndRunId);
          if (!this.regulClausesConfirmed) {
            this.pendingNdRunForwardOnly = true;
            this.showRegulClauseReview = true;
            this.buildRegulClauseRows();
            this.toast.show('Review and confirm clauses, then Run forward only again', 'info', 5000);
            return;
          }
          await this.launchNdAnalysisRunForwardOnly(this.ndRunId, selectedIds);
          return;
        }
      }
    }

    this.pendingNdRunForwardOnly = true;
    const createRes = await this.ndApi.createAnalysisRun(this.buildNdCreateRunPayload(selectedIds));
    if (!createRes.success || !createRes.data?.id) {
      this.pendingNdRunForwardOnly = false;
      this.error = createRes.message ?? 'Could not create analysis run';
      this.toast.show(this.error, 'error', 5000);
      return;
    }

    const runId = createRes.data.id;
    this.ndRunId = runId;
    await this.router.navigate([this.regulAnalysisRoute], {
      queryParams: { run: runId },
      replaceUrl: true,
    });
    await this.loadNdRunPoints(runId);
    this.showRegulClauseReview = true;
    this.buildRegulClauseRows();
    this.toast.show('Review clauses and confirm before running forward-only analysis', 'info', 5000);
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
      workflowEngine: this.regulWorkflowEngineId,
      enableQualitative: this.enableQualitativeAssessment,
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
      if (!p) return { pointNumber: this.displayLabelForPoint(id) || null };
      const pointNumber =
        resolveGovPointDisplayNumber(p) ||
        (p.pointNumber && !this.looksLikeUuid(p.pointNumber) && !isJunkExtractPointId(p.pointNumber)
          ? p.pointNumber
          : resolveSnapshotDisplayNumber(
              {
                pointNumber: p.pointNumber,
                pointTitle: p.title ?? undefined,
                pointContent: p.text,
                pageReference: p.section ?? undefined,
                regulationPointId: p.regulationPointId ?? id,
              },
              p.regulationPointId ?? id,
            )) ||
        null;
      const gov = this.govPointForAnalysis(id);
      const content = (gov?.text?.trim() || p.text?.trim() || '').length >= (p.text?.trim().length ?? 0)
        ? gov?.text?.trim() || p.text
        : p.text;
      return {
        pointNumber,
        pointId: p.regulationPointId ?? id,
        pointTitle: p.title ?? gov?.title ?? undefined,
        pointContent: content,
        pageReference: p.section ?? undefined,
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
          this.toast.show(`Uploaded ${file.name} — run parse from Documents or at analysis time`, 'success', 3500);
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

  rerunAllStuckOrQueuedWithConfirm(): void {
    if (!this.isNdShell) {
      this.rerunAllStuckOrQueued();
      return;
    }
    this.requestNdRunConfirm(
      'Rerun forward (selected clauses)',
      'Type start to rerun forward analysis for queued or unfinished points.',
      () => this.rerunAllStuckOrQueued(),
    );
  }

  rerunRegulReversePassWithConfirm(): void {
    this.requestNdRunConfirm(
      'Rerun reverse pass',
      'Type start to re-map internal manual sections against selected regulatory clauses.',
      () => this.retryAllNdPhase2(),
    );
  }

  override rerunPointAll(pointId: string): void {
    if (this.isNdShell) {
      this.requestNdRunConfirm(
        'Rerun this point',
        'Type start to rerun forward + reverse for this point.',
        () => super.rerunPointAll(pointId),
      );
      return;
    }
    super.rerunPointAll(pointId);
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

function normalizeStatusToSeverity(raw: string): ComplianceSeverity | null {
  const s = raw.replace(/\*+/g, '').replace(/\s+/g, ' ').trim().toLowerCase();
  if (!s) return null;
  if (/\bnon[- ]?compliant\b/.test(s) || (/\bnon\b/.test(s) && /compliant/.test(s))) {
    return 'non_compliant';
  }
  if (/\bpartial\b/.test(s)) return 'partial_compliant';
  if (/\bcompliant\b/.test(s)) return 'compliant';
  return null;
}
