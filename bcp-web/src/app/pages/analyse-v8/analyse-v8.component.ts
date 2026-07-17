import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRouteSnapshot, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { DualVerifyResultCardComponent } from '../../components/dual-verify-result-card/dual-verify-result-card.component';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { AnalyseBase } from '../shared/analyse-base';
import { startPanelResize, type PanelResizeKind } from '../shared/panel-resize';
import type { GovPoint } from '../../services/api.service';
import type { LibrarySummary, RegulationDocument, ActionPlanHistoryEntry, AnalysisPoint } from '../../../lib/nd/types';
import { parseReferenceComplianceBlock } from '../../../lib/ai-lab/parse-reference-response';
import type { GapSeverity } from '../../services/reguliq-store';
import { parsePointSnapshot } from '../../../lib/nd/utils';
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
import { NdResultsComponent } from '../nd/results/nd-results.component';

type PointsSource = 'regulation' | 'library';

type ApiLibraryPoint = {
  regulationPointId: string;
  regulationDocumentId: string;
  displayOrder: number;
  pointSnapshot?: string | Record<string, unknown>;
};

@Component({
  selector: 'app-analyse-v8',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, InProgressNavButtonComponent, DualVerifyResultCardComponent, NdGapPointDetailComponent, NdPointNumberTreeComponent, NdStatusBadgeComponent, NdResultsComponent],
  templateUrl: './analyse-v8.component.html',
  styleUrl: './analyse-v8.component.scss',
})
export class AnalyseV8Component extends AnalyseBase implements OnInit, OnDestroy {
  readonly versionLabel = 'V8 — Points on Top';
  readonly versionPath = '/analyse-v8';

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
    return this.buildSessionAnalysisPoint(pointId);
  }

  pointSnapshotForPointId(pointId: string) {
    const point = this.analysisPointForPointId(pointId);
    return point ? parsePointSnapshot(point.pointSnapshot) : null;
  }

  complianceLabelForPointId(pointId: string): string {
    const status = this.analysisPointForPointId(pointId)?.finalStatus;
    if (!status) return '';
    return this.gapComplianceLabel(status as GapSeverity);
  }

  get selectedPointSnapshot() {
    const point = this.selectedDetailAnalysisPoint;
    return point ? parsePointSnapshot(point.pointSnapshot) : null;
  }

  get canEditResultCap(): boolean {
    if (!this.activeNdRunId) return false;
    const role = this.ndAuth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return !['submitted_for_review', 'checker_approved', 'reviewer_approved'].includes(this.ndRunStatus);
  }

  selectedPointComplianceLabel(): string {
    const status = this.selectedDetailAnalysisPoint?.finalStatus;
    if (!status) return '';
    return this.gapComplianceLabel(status as GapSeverity);
  }

  async loadNdRunPoints(runId: string): Promise<void> {
    const res = await this.ndApi.getResults(runId);
    if (!res.success || !res.data) return;
    const data = res.data as { run: { status: string }; points: AnalysisPoint[] };
    this.ndRunStatus = data.run.status;
    this.ndRunPointsByNumber.clear();
    for (const p of data.points) {
      const snap = parsePointSnapshot(p.pointSnapshot);
      const num = snap.pointNumber?.trim();
      if (num) this.ndRunPointsByNumber.set(num, p);
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
    if (!this.activeNdRunId) return false;
    const role = this.ndAuth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    const status = this.ndRunWorkflowStatus.toLowerCase();
    if (['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(status)) {
      return true;
    }
    return status === '' && this.analysisState === 'complete';
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

  runAnalysisAndScroll(): void {
    if (this.runBlockedReason) {
      this.runAnalysis();
      return;
    }
    this.runAnalysis();
    this.scrollToWorkspace();
  }

  runDemoAnalysisAndScroll(): void {
    this.runDemoAnalysis();
    this.scrollToWorkspace();
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
