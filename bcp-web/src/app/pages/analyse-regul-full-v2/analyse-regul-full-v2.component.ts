import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../components/nd/nd-point-sort-controls.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdGapAnalysisComponent } from '../nd/gap-analysis/nd-gap-analysis.component';
import { NdReportSummaryStackComponent } from '../../components/nd/nd-report-summary-stack.component';
import { NdClauseRailCardComponent } from '../../components/nd/nd-clause-rail-card.component';
import { REGUL_PIPELINE_FULL, REGUL_PIPELINE_HYBRID_V5 } from '../../../lib/nd/regul-fields';
import type { DocAnalysisReadyState } from '../../../lib/nd/doc-analysis-ready';
import type { GovPoint, StoredDocumentDto } from '../../services/api.service';
import type { NdGovPoint } from '../../../lib/regulation-catalog-utils';
import { AnalyseRegulComponent } from '../analyse-regul/analyse-regul.component';
import { AnalyseBase } from '../shared/analyse-base';
import type { AnalysisPoint } from '../../../lib/nd/types';
import type { GapAnalysisExcelOptions } from '../../../lib/nd/export/gap-analysis-export';
import { indexGapStates } from '../../../lib/nd/gap-state';
import type { GapEvidenceReview } from '../../../lib/nd/gap-evidence-rerun';
import { NdPipelinePanelService } from '../../services/nd/nd-pipeline-panel.service';
import { NdStepTrackerService, type NdStep } from '../../services/nd/nd-step-tracker.service';
import { NdPageHeaderActionsService } from '../../services/nd/nd-page-header-actions.service';
import type { NdLocalExtractionSection } from '../../services/nd/nd-api.service';
import { startPanelResize } from '../shared/panel-resize';
import { capGapsForAnalysisPoint } from '../../../lib/nd/cap-gap-count';
import { resolveAnalysisPointSeverity } from '../../../lib/nd/point-compliance-status';
import { buildSeededActionPlansForGap, type SeededActionPlan } from '../../../lib/nd/action-plan-seed';
import { logClauseRetrieval, logClauseTraces } from '../../../lib/nd/pipeline-console-log';

function clauseNoFromSnapshot(snapshot: string | null | undefined): string | null {
  if (!snapshot) return null;
  try {
    const parsed = JSON.parse(snapshot) as { pointNumber?: string };
    return parsed.pointNumber?.trim() || null;
  } catch {
    return null;
  }
}

/**
 * V5 — isolated clone of analyse-regul-full (V4), created specifically so the upcoming
 * query-expansion / synonym-matching work (hybrid pipeline Steps 3-4 — see
 * docs/pipeline/HYBRID-ANALYSIS-PIPELINE-PLAN.md and docs/roadmap/QUERY-EXPANSION-PLAN.md) has
 * a safe page to build against without touching V4's production behavior.
 *
 * Extends V3 (AnalyseRegulComponent) directly rather than V4 (AnalyseRegulFullComponent) — V4's
 * `versionPath`/`regulAnalysisRoute` fields were declared without an explicit `: string`
 * annotation, so TypeScript narrowed them to their literal values, which blocks a further
 * subclass from assigning a different route. Fixing that would mean editing V4's file, which is
 * explicitly out of scope (V4 must stay untouched). Extending V3 and re-declaring V4's same
 * small set of behavior overrides here achieves an identical result with zero risk to V4 — this
 * page currently behaves exactly like V4 (same engine, forward-only, full-markdown), and is
 * where the actual Step 3-4 retrieval changes land once that backend work starts.
 */
@Component({
  selector: 'app-analyse-regul-full-v2',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NdGapPointDetailComponent,
    NdPointSortControlsComponent,
    NdPointNumberTreeComponent,
    NdStatusBadgeComponent,
    NdGapAnalysisComponent,
    NdClauseRailCardComponent,
    NdReportSummaryStackComponent,
  ],
  // V2 forked its own copy of the shared V3/V4 template+styles on 2026-09-28 (28 Sep meeting UI
  // changes) specifically so this page's redesign never touches analyse-regul.component.html/.scss,
  // which V3 and V4 still use unmodified. Edit only these two files for V2-only UI work.
  templateUrl: './analyse-regul-full-v2.component.html',
  styleUrl: './analyse-regul-full-v2.component.scss',
})
export class AnalyseRegulFullV2Component extends AnalyseRegulComponent {
  override readonly versionLabel = '';
  override readonly versionPath: string = '/analyse-regul-full-v2';

  protected override readonly regulWorkflowEngineId: string = REGUL_PIPELINE_HYBRID_V5;
  protected override readonly regulAnalysisRoute: string = '/nd/analyse-regul-full-v2';
  protected override showReversePipelineUi = false;

  private readonly pipelinePanel = inject(NdPipelinePanelService);
  private readonly stepTracker = inject(NdStepTrackerService);
  private readonly pageHeaderActions = inject(NdPageHeaderActionsService);
  /** User-draggable order of the 3-column workspace (Points/Progress/Result) — each column keeps
   * its own resize handle attached (see the CSS `order` bindings in the template), so dragging a
   * column just moves it and its own handle together as a pair. Session-only — not persisted, so
   * a refresh always starts back at the default Points/Progress/Result order. */
  columnOrder: Array<'status' | 'progress' | 'result'> = ['status', 'progress', 'result'];
  /** Explicit width for the Result column — same resize model as Points/Progress (V2 only). */
  colResultWidth = 360;
  /** Collapsed workspace columns — narrow rail with title only; expand via the head control. */
  columnCollapsed: Record<'status' | 'progress' | 'result', boolean> = {
    status: false,
    progress: false,
    result: false,
  };
  private draggingColumn: 'status' | 'progress' | 'result' | null = null;

  toggleColumnCollapsed(key: 'status' | 'progress' | 'result', event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    this.columnCollapsed[key] = !this.columnCollapsed[key];
  }

  /** Pixel width for workspace columns; collapsed rails use a narrow strip. */
  columnWidthPx(key: 'status' | 'progress' | 'result'): number {
    if (this.columnCollapsed[key]) return 52;
    if (key === 'status') return this.colLeftWidth;
    if (key === 'progress') return this.colMidWidth;
    return this.colResultWidth;
  }

  private columnWidthValue(key: 'status' | 'progress' | 'result'): number {
    if (key === 'status') return this.colLeftWidth;
    if (key === 'progress') return this.colMidWidth;
    return this.colResultWidth;
  }

  private setColumnWidthValue(key: 'status' | 'progress' | 'result', px: number): void {
    if (key === 'status') this.colLeftWidth = px;
    else if (key === 'progress') this.colMidWidth = px;
    else this.colResultWidth = px;
  }

  /** CSS `order` for a column — spaced by 10 (0/10/20) so the two boundary resize handles, fixed
   * at order 5 and 15, always land visually between whichever columns occupy those two slots. */
  columnOrderValue(key: 'status' | 'progress' | 'result'): number {
    return this.columnOrder.indexOf(key) * 10;
  }

  /** Drag a vertical handle — always resizes the column on the left of the handle (all three
   * columns have pixel widths, so Result resizes correctly no matter the column order). */
  startBoundaryResize(leftKey: 'status' | 'progress' | 'result', _rightKey: 'status' | 'progress' | 'result', event: MouseEvent): void {
    event.preventDefault();
    if (this.columnCollapsed[leftKey]) return;
    const startX = event.clientX;
    const startVal = this.columnWidthValue(leftKey);
    const min = 200;
    const max = 640;
    const body = document.body;
    body.classList.add('panel-resizing');
    body.style.userSelect = 'none';
    body.style.cursor = 'col-resize';
    const onMove = (e: MouseEvent) => {
      const delta = e.clientX - startX;
      const next = Math.min(max, Math.max(min, startVal + delta));
      this.setColumnWidthValue(leftKey, next);
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      body.classList.remove('panel-resizing');
      body.style.userSelect = '';
      body.style.cursor = '';
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  onColumnDragStart(key: 'status' | 'progress' | 'result', event: DragEvent): void {
    this.draggingColumn = key;
    event.dataTransfer?.setData('text/plain', key);
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  onColumnDragOver(event: DragEvent): void {
    event.preventDefault();
  }

  onColumnDrop(targetKey: 'status' | 'progress' | 'result', event: DragEvent): void {
    event.preventDefault();
    const from = this.draggingColumn;
    this.draggingColumn = null;
    if (!from || from === targetKey) return;
    const order = [...this.columnOrder];
    const fromIdx = order.indexOf(from);
    const toIdx = order.indexOf(targetKey);
    order.splice(fromIdx, 1);
    order.splice(toIdx, 0, from);
    this.columnOrder = order;
  }

  private pipelineDocsTimer: ReturnType<typeof setTimeout> | null = null;
  private pipelineDocsDestroyed = false;
  /** Doc selection (selectedRegIds/selectedComplianceIds) is mutated directly from many call
   * sites across the shared base class, not routed through one setter — a signal-based tracker
   * refresh can't hook every mutation. Cheap enough to just recompute on a short interval instead
   * of chasing every call site. */
  private stepTrackerRefreshTimer: ReturnType<typeof setInterval> | null = null;
  /** True once a poll tick has found at least one doc AND finished a readiness fetch for it —
   * the slow-poll interval only kicks in after this, so no fixed delay is ever "too short" for
   * how long the doc lists actually took to load. */
  private hasSyncedPipelineDocsOnce = false;

  private static readonly STEP1_HEIGHT_KEY = 'v2-step1-height-px';
  /** Drag-resizable height of the Step 1 (Regulation sources) box — the handle sits between it
   * and Step 2, matching the drag-to-resize pattern already used elsewhere in ND (e.g. the
   * regulation-documents-local split panel). */
  step1HeightPx = 360;

  startStepResize(event: MouseEvent): void {
    startPanelResize(
      { kind: 'docs-height', startX: event.clientX, startY: event.clientY, startVal: this.step1HeightPx },
      event,
      (_kind, value) => {
        this.step1HeightPx = value;
      },
      { 'docs-height': { min: 220, max: 720 } },
    );
    const onUp = () => {
      window.removeEventListener('mouseup', onUp);
      try {
        localStorage.setItem(AnalyseRegulFullV2Component.STEP1_HEIGHT_KEY, String(this.step1HeightPx));
      } catch {
        /* ignore storage errors */
      }
    };
    window.addEventListener('mouseup', onUp);
  }

  private restoreStep1Height(): void {
    try {
      const saved = localStorage.getItem(AnalyseRegulFullV2Component.STEP1_HEIGHT_KEY);
      if (!saved) return;
      const px = Number.parseFloat(saved);
      if (Number.isFinite(px) && px >= 220 && px <= 720) this.step1HeightPx = px;
    } catch {
      /* ignore storage errors */
    }
  }

  /** Doc id -> whether the azure-di engine (the only engine actually used in production — see
   * /nd/regulation-documents-azure-di and /nd/internal-documents-azure-di) has it parsed+
   * extracted. V3/V4's own doc-picker readiness (complianceDocReadyState/regDocReadyState,
   * inherited below) only ever checks StoredDocument's legacy ParseStatus/SectionExtractStatus/
   * PointCount fields — LocalDocumentsController deliberately never touches those (see its own
   * doc comment: kept separate so the new pipeline can't affect the existing Landing AI-based
   * V3/V4 pages). That's correct for V3/V4, but it means a document parsed only through the new
   * pipeline shows as "not parsed" on V5's picker even when it's fully ready for Step 4
   * retrieval. Fixed here, on V5 only, checking the exact same engine + data those two reference
   * pages show, by overriding the two ready-state methods below instead of touching the shared
   * V3 file. */
  private readonly localPipelineReady = new Set<string>();

  /** Structural-chunking sections for whichever regulation docs are locally extracted, keyed by
   * stored-doc id — the raw material fetchNdRegulationPoints below turns into gov points for
   * this page. Cached from the same readiness poll that already fetches this data, so building
   * points doesn't need a second network round trip. */
  private readonly localSectionsByDoc = new Map<string, NdLocalExtractionSection[]>();

  /** Right-side hybrid engine panel — platform owner only (not client workspace admins). */
  private get showEnginePipelinePanel(): boolean {
    return this.ndAuth.canManageWorkspaces();
  }

  override ngOnInit(): void {
    this.restoreStep1Height();
    if (this.showEnginePipelinePanel) {
      this.pipelinePanel.activate();
    }
    this.stepTracker.activate();
    this.stepTracker.setSteps(this.computeTrackerSteps());
    this.stepTrackerRefreshTimer = setInterval(() => {
      this.stepTracker.setSteps(this.computeTrackerSteps());
      if (this.showEnginePipelinePanel) {
        this.pipelinePanel.setRunActive(!!this.ndRunId);
        const panelRunId = this.ndRunId ?? this.activeNdRunId ?? null;
        if (panelRunId !== this.panelRunId) {
          // A different run: drop the previous run's Steps 1-6 data and wait for this run's own phase.
          this.panelRunId = panelRunId;
          this.panelAwaitingServerPhase = !!panelRunId;
          this.pipelinePanel.clearRunData();
        }
        this.pipelinePanel.setRunId(panelRunId);
        this.pipelinePanel.setJudgmentFailures(
          this.analysingListRows.filter((r) => r.status === 'failed').length,
        );
        this.pipelinePanel.setClauseProgress(
          this.analysingListRows.length,
          this.analysingListRows.filter((r) => r.status === 'completed' || r.status === 'failed').length,
        );
        this.pipelinePanel.setPhase(this.panelPhase());
      }
      this.syncPageHeaderMarquee();
    }, 400);
    this.syncPageHeaderMarquee();
    this.pageHeaderActions.setShowInProgressNav(true);
    super.ngOnInit();
    // Doc lists themselves load asynchronously (super.ngOnInit kicks that off), and how long
    // that takes varies with network/DB latency — no fixed delay is ever safely "long enough".
    // Instead of guessing delays, poll fast and recursively re-check readiness on every tick:
    // once a tick actually sees docs AND finishes a readiness fetch for them, the picker's red
    // flash can no longer happen, and only then do we relax into the slow steady-state cadence.
    this.pipelineDocsDestroyed = false;
    void this.pollPipelineDocs();
  }

  override ngOnDestroy(): void {
    this.pipelineDocsDestroyed = true;
    if (this.pipelineDocsTimer) clearTimeout(this.pipelineDocsTimer);
    if (this.showEnginePipelinePanel) {
      this.pipelinePanel.deactivate();
    }
    this.stepTracker.deactivate();
    this.pageHeaderActions.clearMarquee();
    this.pageHeaderActions.setShowInProgressNav(false);
    if (this.stepTrackerRefreshTimer) clearInterval(this.stepTrackerRefreshTimer);
    super.ngOnDestroy();
  }

  /** Mirrors the Progress column empty/live/complete states in the blue shell header ticker. */
  private syncPageHeaderMarquee(): void {
    let text: string;
    if (this.showLiveProgressColumn) {
      const summary = this.forwardClauseProgressSummary;
      const count = this.sessionProgressLabel;
      text = `${summary} · Clauses ${count}`;
    } else if (this.showPostAnalysisWorkflow) {
      text = `Analysis complete · ${this.analysingListDone}/${this.analysingListTotal} points analysed`;
    } else if (this.isNdRunStuck) {
      text = 'Analysis did not start — use Rerun all or rerun each point below';
    } else {
      text = 'Run analysis to see live progress here';
    }
    this.pageHeaderActions.setMarquee(text);
  }

  /** Recursive self-pacing poll: fires every ~200ms until the doc lists have actually populated
   * and a readiness fetch has completed for them at least once, then drops to a steady-state
   * cadence. Recursing off the end of each cycle (rather than a fixed setInterval) means the fast
   * phase naturally stretches to cover however long the doc lists really take to load, instead of
   * assuming a fixed number of milliseconds will be enough.
   *
   * The steady-state cadence itself isn't fixed either: while every currently-listed document is
   * already fully parsed+extracted and no analysis run is in flight, nothing here can actually
   * change (a fresh upload already forces its own immediate sync — see runRegulationUploadPipeline/
   * runComplianceUploadPipeline), so polling every 4s forever was just load with no payoff. Only
   * poll that fast while something could plausibly still be in flight (a doc mid-parse/extract, or
   * a run actively processing); otherwise fall back to a slow keep-alive cadence that still catches
   * an out-of-band change (e.g. the same doc reprocessed from the dedicated Documents page in
   * another tab) without hammering the status endpoint while nothing is happening. */
  private async pollPipelineDocs(): Promise<void> {
    if (this.pipelineDocsDestroyed) return;
    await this.syncPipelineDocs();
    if (this.pipelineDocsDestroyed) return;
    const delayMs = !this.hasSyncedPipelineDocsOnce
      ? 200
      : this.allTrackedDocsReady() && !this.isRegulPipelineInFlight()
        ? 30000
        : 4000;
    this.pipelineDocsTimer = setTimeout(() => void this.pollPipelineDocs(), delayMs);
  }

  /** True once every document currently listed in either picker has come back extracted on the
   * azure-di engine — i.e. syncLocalPipelineReadiness has nothing left to wait on for them. */
  private allTrackedDocsReady(): boolean {
    const allDocs = [...this.complianceDocs, ...this.regulationDocs];
    if (allDocs.length === 0) return false;
    return allDocs.every((d) => this.localPipelineReady.has(this.storedDocId(d)));
  }

  /** Selected internal documents (pre-run selection or a resumed run's set) — pushed into the
   * shared panel service so the right-side panel can show per-document Parse/Extract/Index
   * status. */
  private async syncPipelineDocs(): Promise<void> {
    const ids =
      this.selectedComplianceIds.size > 0
        ? [...this.selectedComplianceIds]
        : this.complianceDoc?.id
          ? [this.complianceDoc.id]
          : [];
    const docs = ids.map((id) => {
      const found =
        this.complianceDocs.find((d) => d.id === id) ??
        (this.complianceDoc?.id === id ? this.complianceDoc : null);
      return { id, name: found?.originalFileName || found?.title || 'Internal document' };
    });
    if (this.showEnginePipelinePanel) {
      this.pipelinePanel.setDocs(docs);
      this.pipelinePanel.setRunActive(!!this.ndRunId);
    }
    this.stepTracker.setSteps(this.computeTrackerSteps());

    const hadDocs = this.complianceDocs.length > 0 || this.regulationDocs.length > 0;
    await this.syncLocalPipelineReadiness();
    if (hadDocs) this.hasSyncedPipelineDocsOnce = true;
  }

  /** Internal docs' own `.id` is already the StoredDocument id, but a regulation doc's `.id` is
   * its NdRegulationDocument wrapper id — the underlying StoredDocument id (what
   * NdLocalDocumentExtraction is actually keyed on) is `ndStoredDocumentId`. */
  private storedDocId(doc: StoredDocumentDto): string {
    return doc.ndStoredDocumentId ?? doc.id;
  }

  /** Checks the azure-di engine's real status for every document currently listed in either
   * picker — the same single engine /nd/regulation-documents-azure-di and
   * /nd/internal-documents-azure-di show, so V5's picker reads exactly the same numbers those
   * pages do rather than a broader "any engine" guess. Also copies the real extracted section
   * count onto each doc's pointCount in place (mutating the same objects the shared V3 template
   * already binds to) so the "N pts" label matches those reference pages too, not the legacy
   * StoredDocument.PointCount value (always 0 for docs never touched by the old pipeline). */
  private async syncLocalPipelineReadiness(): Promise<void> {
    const allDocs = [...this.complianceDocs, ...this.regulationDocs];
    const allIds = [...new Set(allDocs.map((d) => this.storedDocId(d)))];
    if (allIds.length === 0) return;

    // Light call: statuses and counts only. This runs on a poll for every listed document, and the full
    // response (parsed text + sections) took 11-19 s and 1.5 MB per call for a dozen documents — which is
    // also how long the picker sat on the red "not parsed" state. Clause text is fetched on demand below.
    const res = await this.ndApi.localExtractStatusBatch(allIds, 'azure-di', { lite: true });
    if (!res.success || !res.data) return;

    this.localPipelineReady.clear();
    for (const [id, status] of Object.entries(res.data)) {
      if (status?.extractStatus !== 'extracted') continue;
      this.localPipelineReady.add(id);
    }
    for (const doc of allDocs) {
      const id = this.storedDocId(doc);
      const status = res.data[id];
      if (status?.extractStatus === 'extracted' && (status.sectionCount ?? 0) > 0) {
        doc.pointCount = status.sectionCount;
        // A re-extract elsewhere changes the count: drop the cached clauses so the next selection re-reads them.
        const cached = this.localSectionsByDoc.get(id);
        if (cached && cached.length !== status.sectionCount) this.localSectionsByDoc.delete(id);
      }
    }
    // localPipelineReady is a plain Set mutated from inside a recursive setTimeout chain, not a
    // signal read directly in the template — confirmed live: complianceDocReadyState/
    // regDocReadyState return the correct value the instant this method resolves (verified by
    // calling them directly from the console), but the rendered row kept showing the stale legacy
    // "NOT PARSED / NOT EXTRACTED" red state indefinitely without this. Same root cause as the
    // pipeline panel's own "stuck on Pending" bug — this app doesn't automatically re-render on an
    // async callback mutating a plain property, so force one explicitly.
    this.cdr.markForCheck();
    this.cdr.detectChanges();
  }

  /** V5-only: source regulation points from the local Azure DI + structural-chunking pipeline
   * instead of the legacy Landing AI-based points endpoint. V3/V4 keep using the legacy path
   * exactly as before — this only intercepts the fetch when THIS page's instance calls it, and
   * only for a document that's actually been through the new pipeline; any other document
   * (or the legacy pages themselves) falls straight through to the unmodified base behavior.
   * A regulation doc parsed+extracted via Azure DI has real clause/section text sitting right
   * there — no reason to depend on the separate, older Landing AI gov-point extraction that
   * this document may never have been run through. */
  /**
   * Excel / PDF show what the gap analysis page shows (real accounts): each gap with its risk, Pending / Resolved
   * state and "Rerun this gap" result, and its saved actions. Demo exports stay as they are.
   */
  protected override async gapAnalysisExportOptions(): Promise<GapAnalysisExcelOptions> {
    const options = await super.gapAnalysisExportOptions();
    if (this.ndAuth.isDemoViewer() || !this.ndRunId) return { ...options, regulHybridGaps: false };
    const [gaps, results] = await Promise.all([
      this.ndApi.getRunGaps(this.ndRunId),
      this.ndApi.getResults(this.ndRunId),
    ]);
    const evidenceReviews =
      (results.success ? (results.data as { gapEvidenceReviews?: GapEvidenceReview[] } | null)?.gapEvidenceReviews : null) ?? [];
    return {
      ...options,
      regulHybridGaps: true,
      gapStates: gaps.success && gaps.data ? indexGapStates(gaps.data) : new Map(),
      evidenceReviews,
    };
  }

  protected override async fetchNdRegulationPoints(id: string): Promise<{
    success: boolean;
    points: GovPoint[];
    message?: string;
    document?: StoredDocumentDto;
  }> {
    const doc = this.regulationDocs.find((d) => d.id === id);
    if (doc && this.localPipelineReady.has(this.storedDocId(doc))) {
      const sections = await this.loadLocalSections(this.storedDocId(doc));
      const points = this.localSectionsToGovPoints(doc, sections);
      doc.pointCount = points.length;
      return { success: true, points, document: doc };
    }
    return super.fetchNdRegulationPoints(id);
  }

  /** The document's extracted clauses (clause number + text). Read once per document with the full status
   * call — the readiness poll only carries counts — and cached until the clause count changes. */
  private async loadLocalSections(storedDocId: string): Promise<NdLocalExtractionSection[]> {
    const cached = this.localSectionsByDoc.get(storedDocId);
    if (cached) return cached;
    const res = await this.ndApi.localExtractStatusBatch([storedDocId], 'azure-di');
    const sections = res.success ? (res.data?.[storedDocId]?.sections ?? []) : [];
    if (sections.length > 0) this.localSectionsByDoc.set(storedDocId, sections);
    return sections;
  }

  /** Each structural-chunking section (clause number + text, detected locally by regex over
   * numbering conventions — see LocalSectionSplitter.cs) becomes one gov point. No AI call, no
   * dependency on the legacy Landing AI extraction having ever run for this document. */
  private localSectionsToGovPoints(
    doc: StoredDocumentDto,
    sections: NdLocalExtractionSection[],
  ): NdGovPoint[] {
    const docName = doc.title || doc.originalFileName || 'Regulation document';
    // A clause number must be unique: it becomes the point id, and the picker nests duplicate numbers
    // ("3.1" twice -> "3.1.1"/"3.1.2") and then loses both. A document extracted before the table-of-
    // contents fix can still hold a contents line ("3.1 Title / 13") next to the real clause 3.1, so when
    // a number repeats keep the fullest text (the real clause), in the position it first appeared.
    const fullest = new Map<string, NdLocalExtractionSection>();
    for (const s of sections) {
      const no = (s.clauseNo || '').trim();
      const kept = fullest.get(no);
      if (!no || !kept || s.clauseText.length > kept.clauseText.length) fullest.set(no || `__${fullest.size}`, s);
    }
    const unique = [...fullest.values()];
    return unique.map((s, i) => {
      const clauseNo = (s.clauseNo || '').trim() || String(i + 1);
      const pointId = `${doc.id}:${clauseNo}`;
      return {
        point_id: pointId,
        pointNumber: clauseNo,
        text: s.clauseText,
        regulationPointId: pointId,
        regulationDocumentId: doc.id,
        docId: doc.id,
        docName,
      };
    });
  }

  /** V5 clauses are identified by clause number and never have a regulationPointId (see
   * localSectionsToGovPoints), so the base poll's "skip points without one" filter would drop every run point. */
  protected override runPointNeedsRegulationPointId(): boolean {
    return false;
  }

  protected override failedOrPendingPointKeepsRunOpen(): boolean {
    return false;
  }

  override complianceDocReadyState(doc: StoredDocumentDto): DocAnalysisReadyState {
    if (this.localPipelineReady.has(this.storedDocId(doc))) {
      return (doc.analysisRunCount ?? 0) > 0 ? 'analysed' : 'ready';
    }
    return super.complianceDocReadyState(doc);
  }

  /** The small "Parsed / Parsing… / Parse failed / Not parsed" sub-label under an internal doc's
   * name is driven by a THIRD, separate legacy status source (StoredDocument.ParseStatus via
   * ndInternalParseStatus, read by V3's complianceParseLabel — see its own doc comment) that
   * complianceDocReadyState above doesn't touch at all. A document parsed only through the new
   * Azure DI pipeline still shows this sub-label as "Parse failed" (an old Landing AI attempt's
   * leftover status) even once the pill above correctly reads "Ready to analyse" — confirmed live
   * for I M P T F S.pdf.pdf: azure-di status is parsed/extracted/indexed (25 sections), but the
   * legacy ParseStatus is still 'failed' from a prior Landing AI attempt. Fixed the same way as
   * the pill: check the real azure-di status first, on V5 only. */
  override complianceParseLabel(docId: string): string {
    if (this.localPipelineReady.has(docId)) return 'Parsed';
    return super.complianceParseLabel(docId);
  }

  override regDocReadyState(doc: StoredDocumentDto): DocAnalysisReadyState {
    if (this.localPipelineReady.has(this.storedDocId(doc))) {
      return (doc.analysisRunCount ?? 0) > 0 ? 'analysed' : 'ready';
    }
    return super.regDocReadyState(doc);
  }

  /** Pushes the live Step 1+4 retrieval preview (and current pipeline phase) from each status
   * poll into the shared panel service so the shell's right-side panel can render it. */
  protected override onNdRetrievalPreviewUpdate(
    preview: Array<{ clauseNo: string; retrieval: unknown }>,
  ): void {
    if (!this.showEnginePipelinePanel) return;
    this.pipelinePanel.setPhase(this.panelPhase());
    this.pipelinePanel.setRetrievalPreview(preview);
    this.logRetrievalToConsole(preview);
  }

  // ---- Pipeline panel phase. Launching a run sets a local "forward" before the server has reported
  // anything, which showed Steps 1-7 as done and then as processing. Until this run's server phase
  // arrives, the panel shows the run as queued.
  private panelRunId: string | null = null;
  private panelAwaitingServerPhase = false;

  protected override onNdServerPipelinePhase(_phase: string): void {
    this.panelAwaitingServerPhase = false;
  }

  private panelPhase(): string | null {
    // Only while the run is in flight: an old run whose record has no phase must not stay "queued".
    const inFlight = ['running', 'processing', 'queued', 'draft'].includes((this.ndRunWorkflowStatus || '').toLowerCase());
    return this.panelAwaitingServerPhase && inFlight ? 'queued' : this.ndRegulPipelinePhase;
  }

  // ---- DevTools console log of each clause's pipeline (Steps 1-6) and AI judgment (Steps 7-8).
  private readonly consoleLoggedRetrieval = new Map<string, string>();
  private readonly consoleLoggedTraceIds = new Set<string>();
  private readonly consolePointStatus = new Map<string, string>();

  private logRetrievalToConsole(preview: Array<{ clauseNo: string; retrieval: unknown }>): void {
    for (const p of preview) {
      const r = p.retrieval as { fusedMatches?: Array<{ sectionId?: string }> } | null;
      const signature = (r?.fusedMatches ?? []).map((m) => m.sectionId).join(',');
      if (this.consoleLoggedRetrieval.get(p.clauseNo) === signature) continue;
      this.consoleLoggedRetrieval.set(p.clauseNo, signature);
      logClauseRetrieval(p.clauseNo, p.retrieval);
    }
  }

  /** When a clause reaches a terminal status, print its AI call log (only new rows, so a rerun
   * prints just the new pass). The endpoint is platform-admin only; others simply get nothing. */
  private async logTracesForFinishedPoints(points: AnalysisPoint[]): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;
    const terminal = new Set(['completed', 'failed', 'cancelled']);
    for (const point of points) {
      if (!point.id) continue;
      const status = (point.landingAiStatus || '').toLowerCase();
      const previous = this.consolePointStatus.get(point.id);
      this.consolePointStatus.set(point.id, status);
      if (!terminal.has(status) || previous === status) continue;

      const clauseNo = clauseNoFromSnapshot(point.pointSnapshot);
      if (!clauseNo) continue;
      const res = await this.ndApi.getClauseTraces(runId, clauseNo);
      if (!res.success || !res.data) continue;
      const fresh = res.data.filter((t) => !this.consoleLoggedTraceIds.has(t.id));
      fresh.forEach((t) => this.consoleLoggedTraceIds.add(t.id));
      logClauseTraces(clauseNo, fresh);
    }
  }

  /** Point ids already checked for a gap since the run started — avoids re-checking (and
   * re-POSTing) a point on every ~3s poll tick once it's reached a terminal status. Cleared
   * implicitly by page navigation (this whole component is torn down between runs). */
  private readonly actionPlanSeededPointIds = new Set<string>();

  /** V5 only: draft a first-pass action plan for a gap the instant its clause finishes judging,
   * instead of waiting for the whole run to reach a terminal status (nd-gap-analysis's own
   * seedDefaultActionPlans gates on isAnalysisRunResultsReady(run.status), and V3/V4's embedded
   * panel only reloads once — on run completion — so on a run with a dozen clauses a gap found
   * early could sit with no action plan for minutes). Reuses the same seed-rule logic the
   * end-of-run path uses (capGapsForAnalysisPoint + buildSeededActionPlansForGap), fed by the
   * same live per-clause point updates the picker already gets from the ~3s status poll. The
   * backend /action-plans/seed endpoint dedupes by (pointId, gapIndex), so this is safe to call
   * again for a point already seeded — it's just wasted otherwise, hence actionPlanSeededPointIds. */
  private async seedActionPlansForCompletedPoints(points: AnalysisPoint[]): Promise<void> {
    const runId = this.activeNdRunId;
    if (!runId) return;

    const terminalStatuses = new Set(['completed', 'failed', 'cancelled']);
    const items: SeededActionPlan[] = [];
    for (const point of points) {
      if (!point.id || this.actionPlanSeededPointIds.has(point.id)) continue;
      if (!terminalStatuses.has((point.landingAiStatus || '').toLowerCase())) continue;
      this.actionPlanSeededPointIds.add(point.id);

      if (resolveAnalysisPointSeverity(point) === 'compliant') continue;
      for (const gap of capGapsForAnalysisPoint(point, true)) {
        items.push(
          ...buildSeededActionPlansForGap(point.id, gap, new Date(), {
            useAiAction: !this.ndAuth.isDemoViewer(),
          }),
        );
      }
    }
    if (!items.length) return;
    const res = await this.ndApi.seedActionPlans(runId, items);
    // The embedded gap-analysis panel (<app-nd-gap-analysis>) only reloads its own data when
    // gapEmbedReloadToken changes — bump it here, right after a batch of gaps actually got an
    // action plan, so the panel picks up the new rows without waiting for the whole run to
    // finish. Only on an actual seed (not every poll tick) to avoid re-fetching the panel's full
    // result set (points, sections, reviews, attachments) more often than something really changed.
    if (res.success && res.data && res.data.seeded > 0) this.gapEmbedReloadToken++;
  }

  /** Keeps the base's own live-update handling (badges, progress steps, demo preview), then
   * layers in this page's instant per-clause action-plan seeding on top. */
  protected override onNdRunPointsLiveUpdate(points: AnalysisPoint[]): void {
    super.onNdRunPointsLiveUpdate(points);
    void this.seedActionPlansForCompletedPoints(points);
    void this.logTracesForFinishedPoints(points);
  }

  /** Always runs forward-only (full markdown); reverse is not used on this page — same as V4. */
  override runAnalysisAndScroll(): void {
    this.runForwardOnlyAndScroll();
  }

  /** V3's own override of this method (which we'd otherwise inherit via `super`) additionally
   * hides the per-clause "Rerun forward" button whenever `!ap.regulationPointId` — a check meant
   * to exclude V3/V4's reverse-mapping INT rows, which genuinely have no regulation point. But
   * every V5 hybrid-engine point ALSO has no real regulationPointId (its points come from local
   * structural chunking — synthetic "{regDocId}:{clauseNo}" ids that never parse as a Guid, see
   * fetchNdRegulationPoints/localSectionsToGovPoints), so that same check was hiding the rerun
   * button for every single V5 point, all the time. V5 never uses reverse mapping/INT rows in the
   * first place, so the exclusion doesn't apply here — call the shared base's own implementation
   * directly instead of going through V3's. */
  override canShowPointRerunActions(pointId: string): boolean {
    if (this.ndAuth.isDemoViewer()) return false;
    if (this.isRegulPipelineInFlight()) return false;
    return AnalyseBase.prototype.canShowPointRerunActions.call(this, pointId);
  }

  override async loadNdRunPoints(runId: string): Promise<void> {
    // Must run BEFORE super — see ensureRunSelectedSnapshotLoaded's own comment. super.loadNdRunPoints
    // itself (synchronously, while still processing the GET /nd/results response) already calls into
    // the shared runScopeGovKeys()/ndRegulatoryPointsInScope() cycle, so the snapshot has to be in
    // place before that call, not after it returns.
    await this.ensureRunSelectedSnapshotLoaded(runId);
    await super.loadNdRunPoints(runId);
    this.validateRunEngine();
    await this.loadPipelineRetrievalPreviewAndFullPoints(runId);
  }

  /**
   * V5 only, fixes a real stack overflow reproduced live on this page (not present on V3/V4's own
   * flows): super.loadNdRunPoints() (GET /nd/results) never returns selectedPointsSnapshot, and a
   * run started directly from this page — click "Run forward only" from a fresh selection, never
   * reloaded via ?run= — never goes through attachToNdAnalysisRun either, which is the only place
   * that ever sets it. Without a snapshot, the shared runScopeGovKeys() has nothing to key off; once
   * the run leaves "draft" (which happens within the same tick the run starts) its checkbox-based
   * fallback branch is gated shut too, leaving only its "ndRegulatoryPointsInScope()" branch — which
   * calls straight back into runScopeGovKeys(), recursing on every change-detection cycle until the
   * call stack overflows. Angular's zone swallows the exception into console.error instead of
   * surfacing it, so nothing crashes visibly — the whole page just silently stops updating: Points
   * stays at 0, the progress label never advances past its first render, and the result panel never
   * shows a completed clause. Fetching the same lite run detail the cold-open path already reads
   * this same field from, purely to populate it here too, avoids all of that without changing the
   * shared method (or anything V3/V4 exercise) at all.
   */
  private async ensureRunSelectedSnapshotLoaded(runId: string): Promise<void> {
    if (this.ndRunSelectedSnapshot) return;
    const res = await this.ndApi.getAnalysisRun(runId, { lite: true });
    const snapshot = (res.data as { run?: { selectedPointsSnapshot?: string } } | undefined)?.run
      ?.selectedPointsSnapshot;
    if (snapshot) this.ndRunSelectedSnapshot = snapshot;
  }

  /** Fires once per run attach, for BOTH an actively-processing run and an already-completed
   * one — unlike pollNdRun (shared base), which only ever runs for status running/draft/
   * processing (see attachToNdAnalysisRun's activelyProcessing branch) and so never fires at all
   * for a completed run. That's exactly the case that matters here: opening an already-completed
   * V5 run via a fresh page load or a direct ?run= link goes through attachToNdAnalysisRun (never
   * through loadNdRunPoints, since analyse-regul's own ngOnInit explicitly skips calling
   * loadNdRunPoints whenever a ?run= param is present — confirmed live: this hook is the only one
   * that actually fires on that path). Without this, the pipeline panel stayed stuck on "Pending"
   * forever for a completed run, even though the real retrieval data was sitting right there on
   * the backend the whole time — and separately, the RESULT panel's "Regulatory requirement" text
   * stayed permanently truncated to the lite/list-view preview length (280 of a clause's real
   * 2100 characters, confirmed live) because mergeNdRunPoints's fix for that (see its own comment)
   * only ever runs from inside pollNdRun's tick, which — same root cause — never fires either. */
  /** Once per run and phase ("live" the first time the run is seen in flight, "done" once it has settled),
   * reload the run through the same path a cold-open uses. This page keeps its regulation list under one
   * id form while a run is live and another once the page reloads, and the poll's own rebuild of the
   * analysing list only ever produces one of them — so a run started from this page showed 0/0 and an empty
   * POINTS list until the page was reloaded (which goes through loadNdRunPoints and worked). Doing that
   * reload ourselves makes a live run look exactly like the reopened one. */
  private rehydratedRunPhase = '';

  protected override onNdRunPollMerged(data: {
    status: string;
    workflowEngine?: string;
    regulPipelinePhase?: string;
    regulReverseSectionTotal?: number | null;
    regulReverseSectionCompleted?: number | null;
    regulReverseSections?: Array<{ sectionRef: string; title: string; status: string }>;
    totalPointsCount: number;
    processedPointsCount: number;
  }): void {
    const runId = this.ndRunId;
    if (!runId) return;
    const settled = ['completed', 'failed', 'cancelled'].includes((data.status || '').toLowerCase());
    const phaseKey = `${runId}:${settled ? 'done' : 'live'}`;
    if (this.rehydratedRunPhase === phaseKey) return;
    this.rehydratedRunPhase = phaseKey;
    const status = data.status;
    void this.loadNdRunPoints(runId).then(() => {
      // A run started from this page keeps `ndRunStatus` at "draft" for as long as the page is open, and while
      // it says "draft" the run-scope helpers ignore the run's own saved snapshot (the one thing that carries
      // every form of a clause's id) and match checkboxes instead, which misses on this page's two id forms.
      // The reopened page has the real status, so mirror it — but only once the run (and so its snapshot) has
      // been loaded: with a real status and no snapshot the same helpers recurse without end.
      if (this.ndRunId === runId && status && this.ndRunSelectedSnapshot) this.ndRunStatus = status;
    });
  }

  protected override onNdRunAttached(runId: string): void {
    // A cold-open loads the run right below, so it does not need the poll-driven reload for its own phase.
    this.rehydratedRunPhase = `${runId}:live`;
    // attachToNdAnalysisRun only loads the lite list (no judgments, clause text cut to a preview),
    // so a run opened cold showed "Not started", Conf "—", "No corresponding policy extract found"
    // and clause text cut off mid-word even though the run was complete. The full results load
    // (the same one a just-launched run uses) fills all of that in, and also pulls the pipeline
    // preview via our loadNdRunPoints override.
    void this.loadNdRunPoints(runId);
  }

  private async loadPipelineRetrievalPreviewAndFullPoints(runId: string): Promise<void> {
    const statusRes = await this.ndApi.getAnalysisRunStatus(runId);
    if (statusRes.success && statusRes.data) {
      const points = (statusRes.data as { points?: unknown[] }).points;
      if (Array.isArray(points) && points.length) {
        this.ndRunDetailPoints = this.mergeNdRunPoints(
          this.ndRunDetailPoints,
          points as AnalysisPoint[],
        );
        this.cdr.markForCheck();
        this.cdr.detectChanges();
        // A finished run opened from its link is never polled, so print its AI call log here.
        void this.logTracesForFinishedPoints(points as AnalysisPoint[]);
      }
    }
    const statusData = statusRes.data as
      | { regulRetrievalPreview?: Array<{ clauseNo: string; retrieval: unknown }> | null }
      | undefined;
    if (statusRes.success && statusData?.regulRetrievalPreview) {
      this.onNdRetrievalPreviewUpdate(statusData.regulRetrievalPreview);
    }
  }

  protected override regulRunConfirmHint(): string {
    if (this.ndAuth.isDemoViewer()) {
      return 'Demo analysis replays saved CBUAE results — queued, running, and done states update quickly with no live AI.';
    }
    const model = this.regulWorkflowLlmSummary || 'admin-selected model';
    return (
      `Regul full-markdown analysis using ${model}. ` +
      'Sends complete parsed markdown for every attached internal file (any page count, multiple files supported; no section ranking). ' +
      'Forward judgment only — reverse coverage is skipped.'
    );
  }

  private validateRunEngine(): void {
    if (!this.ndRunId || !this.ndWorkflowEngine) return;
    if (this.ndWorkflowEngine === REGUL_PIPELINE_HYBRID_V5 || this.ndWorkflowEngine === REGUL_PIPELINE_FULL) return;

    const msg =
      'This run is V3 (regul_pipeline), not full-markdown. ' +
      'Click Stop, then open a fresh New analysis (without ?run=) and start a new run.';
    this.error = msg;
    this.toast.show(msg, 'error', 12000);

    if (this.ndRunStatus === 'running') {
      this.toast.show(
        'V3 runs on this page do slow section extraction before forward — Stop and create a new run.',
        'warning',
        12000,
      );
    }
  }

  override get runBlockedReason(): string | null {
    if (this.ndAuth.isDemoViewer() && this.isNdShell) {
      return super.runBlockedReason;
    }
    if (
      this.ndRunId &&
      this.ndWorkflowEngine &&
      this.ndWorkflowEngine !== REGUL_PIPELINE_HYBRID_V5 &&
      this.ndWorkflowEngine !== REGUL_PIPELINE_FULL
    ) {
      return 'Wrong workflow engine (V3). Stop this run and start fresh without ?run= in the URL.';
    }
    return super.runBlockedReason;
  }

  /** V5 only. V3/V4 share one generic "Forward judgment" label regardless of what the pipeline
   * is actually doing — this page has real phases (retrieval, then judgment) reported by the
   * backend in regulPipelinePhase, so show which one is in flight instead of the misleading
   * always-"Forward judgment" text. */
  override get forwardClauseProgressSummary(): string {
    const total = this.analysingListTotal;
    if (!total) return this.regulPipelinePhaseLabel();
    const done = this.analysingListDone;
    const counts = this.analysingStatusCounts;
    const phaseLabel = this.regulPipelinePhaseLabel();
    if (counts.running > 0 || counts.queued > 0) {
      return `${phaseLabel} (${done}/${total} done · ${counts.running} running · ${counts.queued} queued)`;
    }
    return `${phaseLabel} (${done}/${total})`;
  }

  private regulPipelinePhaseLabel(): string {
    switch ((this.ndRegulPipelinePhase || '').toLowerCase()) {
      case 'parsing':
        return 'Preparing documents';
      case 'passages':
        return 'Preparing search passages (one-time per document)';
      case 'retrieval':
        return 'Retrieving relevant policy sections';
      case 'forward':
        return 'Judging clauses against policy';
      case 'done':
        return 'Done';
      default:
        return 'Starting analysis';
    }
  }

  /** Heading shown next to the clause number in the Result panel. selectedPointSnapshot's own
   * pointTitle is often empty for points saved before the snapshot carried a title — when that
   * happens the heading used to go missing entirely, leaving the clause number as the only thing
   * in the header and the quoted regulatory text (which already opens with "<number> <title>" as
   * printed in the source document) as the only place the title showed up at all. Falls back to
   * the same title already resolved for this point's rail card. */
  get resultHeadingTitle(): string {
    const fromSnapshot = this.selectedPointSnapshot?.pointTitle?.trim();
    if (fromSnapshot) return fromSnapshot;
    const id = this.selectedDetailPointId;
    if (!id) return '';
    const row = this.analysingListRows.find((r) => r.pointId === id || r.displayId === id);
    return row?.title?.trim() || '';
  }

  /** Left-rail progress: setup (pick regulation, pick internal docs) through the same real
   * phases regulPipelinePhaseLabel reports (parsing/retrieval/forward/done), so the rail always
   * agrees with what the page itself says is happening. */
  private computeTrackerSteps(): NdStep[] {
    const defs = [
      'Regulation documents & points',
      'Internal documents',
      'Preparing documents',
      'Retrieving policy sections',
      'Judging clauses',
      'Complete',
    ];

    const hasReg = this.selectedRegIds.size > 0;
    const hasInt = this.selectedComplianceIds.size > 0;
    const started = !!this.ndRunId;
    const phase = (this.ndRegulPipelinePhase || '').toLowerCase();
    const status = (this.ndRunStatus || '').toLowerCase();
    const finished =
      phase === 'done' ||
      [
        'completed',
        'dual_verify_failed',
        'landing_ai_complete',
        'submitted_for_review',
        'pulled_back',
        'checker_approved',
        'reviewer_approved',
      ].includes(status);

    let current: number;
    if (!started) {
      current = !hasReg ? 0 : !hasInt ? 1 : 2;
    } else if (finished) {
      // One past the last index — every step (including "Complete" itself) renders as done
      // rather than leaving "Complete" stuck on its unfilled "active" ring forever.
      current = defs.length;
    } else if (phase === 'retrieval' || phase === 'passages') {
      current = 3;
    } else if (phase === 'forward') {
      current = 4;
    } else {
      current = 2; // parsing, or no phase reported yet right after the run is created
    }

    return defs.map((label, i) => ({
      label,
      state: i < current ? 'done' : i === current ? 'active' : 'pending',
    }));
  }

  // --------------------------------------------------------------------------------
  // 28 Sep meeting: uploading a regulation or internal document on this page should run the
  // same parse + extract pipeline the dedicated Documents pages run, automatically, with a
  // visible step-by-step progress state instead of leaving the file "uploaded" and unusable
  // until the user goes and parses it manually elsewhere. And it must not blank the panel while
  // it happens — refreshRegulations()/refreshComplianceDocs() already only show a full "Loading…"
  // state when the list is empty (see the V2 template), so a background refresh here just updates
  // the list in place.
  // --------------------------------------------------------------------------------

  uploadPipelineLabel(stage: 'uploading' | 'parsing' | 'extracting' | 'done' | 'failed'): string {
    switch (stage) {
      case 'uploading':
        return 'Uploading…';
      case 'parsing':
        return 'Parsing…';
      case 'extracting':
        return 'Extracting…';
      case 'done':
        return 'Parsed and extracted';
      case 'failed':
        return 'Failed';
    }
  }

  regUploadPipelineV2: { name: string; stage: 'uploading' | 'parsing' | 'extracting' | 'done' | 'failed'; message?: string } | null = null;
  complianceUploadPipelineV2: { name: string; stage: 'uploading' | 'parsing' | 'extracting' | 'done' | 'failed'; message?: string } | null = null;

  /** Overrides the base's onRegulationUpload (which only uploads, then tells a real — non-demo —
   * account to go parse/extract it manually from Regulation Docs) to also run parse + extract
   * automatically, for every account. Runs through the same azure-di local pipeline as
   * /nd/regulation-documents-azure-di (localParseById/localExtractById) rather than the legacy
   * Landing AI endpoints — this page's own readiness checks (syncLocalPipelineReadiness,
   * regDocReadyState above) only ever look at azure-di local-pipeline status, so a document parsed
   * via the old Landing AI endpoints never showed as ready here. */
  override onRegulationUpload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    void this.runRegulationUploadPipeline(file);
  }

  private async runRegulationUploadPipeline(file: File): Promise<void> {
    this.uploadingReg = true;
    this.regUploadPipelineV2 = { name: file.name, stage: 'uploading' };
    const keptRegIds = new Set(this.selectedRegIds);
    try {
      const uploaded = await this.ndApi.uploadRegulationDocument(file);
      if (!uploaded.success) {
        this.regUploadPipelineV2 = { name: file.name, stage: 'failed', message: uploaded.message ?? 'Upload failed' };
        this.toast.show(this.regUploadPipelineV2.message!, 'error', 4000);
        return;
      }
      const id = (uploaded.data as { id?: string } | undefined)?.id;
      if (!id) {
        this.regUploadPipelineV2 = null;
        this.toast.show('Document uploaded', 'success', 3000);
        this.refreshRegulations(() => this.reapplyRegSelection(keptRegIds));
        return;
      }

      this.regUploadPipelineV2 = { name: file.name, stage: 'parsing' };
      const parsed = await this.ndApi.localParseById(id, 'azure-di');
      if (!parsed.success || parsed.data?.status === 'failed') {
        this.regUploadPipelineV2 = {
          name: file.name,
          stage: 'failed',
          message: parsed.message ?? parsed.data?.error ?? 'Parse failed',
        };
        this.toast.show(this.regUploadPipelineV2.message!, 'error', 4000);
        this.refreshRegulations(() => this.reapplyRegSelection(keptRegIds));
        return;
      }

      this.regUploadPipelineV2 = { name: file.name, stage: 'extracting' };
      const extracted = await this.ndApi.localExtractById(id, 'azure-di');
      if (!extracted.success || extracted.data?.extractStatus === 'failed') {
        this.regUploadPipelineV2 = {
          name: file.name,
          stage: 'failed',
          message: extracted.message ?? extracted.data?.extractError ?? 'Extract failed',
        };
        this.toast.show(this.regUploadPipelineV2.message!, 'error', 4000);
        this.refreshRegulations(() => this.reapplyRegSelection(keptRegIds));
        return;
      }

      this.regUploadPipelineV2 = { name: file.name, stage: 'done' };
      this.toast.show(`Parsed and extracted "${file.name}"`, 'success', 3500);
      await this.syncLocalPipelineReadiness();
      this.refreshRegulations(() => {
        this.reapplyRegSelection(keptRegIds);
        const doc = this.regulationDocs.find((d) => d.id === id);
        if (doc && this.canSelectRegDoc(doc)) this.selectedRegIds.add(doc.id);
        this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
        this.loadPointsForSelectedFiles();
      });
      setTimeout(() => {
        if (this.regUploadPipelineV2?.stage === 'done') this.regUploadPipelineV2 = null;
      }, 2500);
    } catch {
      this.regUploadPipelineV2 = { name: file.name, stage: 'failed', message: 'Upload failed' };
      this.toast.show('Regulation upload failed.', 'error', 4000);
    } finally {
      this.uploadingReg = false;
    }
  }

  private reapplyRegSelection(keptRegIds: Set<string>): void {
    for (const kept of keptRegIds) this.selectedRegIds.add(kept);
    this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
  }

  /** Drives the "select all" checkbox at the top of the Regulations list — matches the same
   * checked-when-every-filtered-item-is-selected pattern used by the Reg. points / library lists. */
  get allFilteredRegsSelected(): boolean {
    const docs = this.filteredRegulationDocs;
    return docs.length > 0 && docs.every((d) => this.selectedRegIds.has(d.id));
  }

  toggleAllFilteredRegs(checked: boolean): void {
    if (checked) this.selectAllFilteredRegs();
    else this.clearRegSelection();
  }

  /** Same pattern for the Internal Documents list's "select all" checkbox. */
  get allFilteredComplianceSelected(): boolean {
    const docs = this.filteredComplianceDocs;
    return docs.length > 0 && docs.every((d) => this.selectedComplianceIds.has(d.id));
  }

  toggleAllFilteredCompliance(checked: boolean): void {
    if (checked) this.selectAllFilteredCompliance();
    else this.clearComplianceSelection();
  }

  /** Overrides the base's onComplianceSelect, which uploads internal documents through a legacy,
   * non-ND endpoint that never parses or extracts them (they never become analyzable). Routes
   * through the ND internal-documents catalog instead, then runs parse + extract through the same
   * azure-di local pipeline /nd/internal-documents-azure-di uses (localParseById/localExtractById)
   * rather than the legacy Landing AI endpoints — this page's own readiness checks
   * (syncLocalPipelineReadiness, complianceDocReadyState above) only ever look at azure-di
   * local-pipeline status, so a document parsed via the old Landing AI endpoints never showed as
   * ready here. */
  override onComplianceSelect(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    void this.runComplianceUploadPipeline(file);
  }

  private async runComplianceUploadPipeline(file: File): Promise<void> {
    this.uploadingCompliance = true;
    this.complianceUploadPipelineV2 = { name: file.name, stage: 'uploading' };
    try {
      const uploaded = await this.ndApi.uploadInternalDocument(file);
      if (!uploaded.success) {
        this.complianceUploadPipelineV2 = { name: file.name, stage: 'failed', message: uploaded.message ?? 'Upload failed' };
        this.toast.show(this.complianceUploadPipelineV2.message!, 'error', 4000);
        return;
      }
      const id = (uploaded.data as { id?: string } | undefined)?.id;
      if (!id) {
        this.complianceUploadPipelineV2 = null;
        this.toast.show('Document uploaded', 'success', 3000);
        return;
      }

      this.complianceUploadPipelineV2 = { name: file.name, stage: 'parsing' };
      const parsed = await this.ndApi.localParseById(id, 'azure-di');
      if (!parsed.success || parsed.data?.status === 'failed') {
        this.complianceUploadPipelineV2 = {
          name: file.name,
          stage: 'failed',
          message: parsed.message ?? parsed.data?.error ?? 'Parse failed',
        };
        this.toast.show(this.complianceUploadPipelineV2.message!, 'error', 4000);
        return;
      }
      if (parsed.data?.status === 'processing') {
        // Azure is taking longer than this request waited for — a rare case, not worth a full
        // polling loop here; the document is safely uploaded and can be finished from Internal
        // Documents, same as any other long-running parse there.
        this.complianceUploadPipelineV2 = null;
        this.toast.show(`"${file.name}" uploaded — still parsing, check Internal Documents shortly.`, 'success', 5000);
        return;
      }

      this.complianceUploadPipelineV2 = { name: file.name, stage: 'extracting' };
      const extracted = await this.ndApi.localExtractById(id, 'azure-di');
      if (!extracted.success || extracted.data?.extractStatus === 'failed') {
        this.complianceUploadPipelineV2 = {
          name: file.name,
          stage: 'failed',
          message: extracted.message ?? extracted.data?.extractError ?? 'Extract failed',
        };
        this.toast.show(this.complianceUploadPipelineV2.message!, 'error', 4000);
        return;
      }

      // The dedicated Internal Documents page (this doc's real source of truth from here on)
      // reads from the ND catalog; this page's own compliance list still reads from the older,
      // separate document store, so the doc this pipeline just finished would otherwise never
      // appear here to select. Inject it directly instead of switching this page's whole list
      // source (a bigger change than this fix needs).
      const sectionCount = extracted.data?.sectionCount ?? null;
      const readyDoc: StoredDocumentDto = {
        id,
        title: file.name,
        category: 'Compliance',
        pages: 0,
        uploaded: new Date().toISOString(),
        version: 'v1',
        status: 'ready',
        filter: 'aml',
        fileType: file.name.split('.').pop()?.toUpperCase() ?? '',
        docKind: 'document',
        storagePath: '',
        history: [],
        originalFileName: file.name,
        sizeBytes: file.size,
        parseStatus: 'parsed',
        sectionExtractStatus: 'extracted',
        sectionCount,
      };
      this.complianceDocs = [readyDoc, ...this.complianceDocs.filter((d) => d.id !== id)];
      this.selectedComplianceIds.add(id);
      await this.syncLocalPipelineReadiness();

      this.complianceUploadPipelineV2 = { name: file.name, stage: 'done' };
      this.toast.show(`Parsed and extracted "${file.name}"`, 'success', 3500);
      setTimeout(() => {
        if (this.complianceUploadPipelineV2?.stage === 'done') this.complianceUploadPipelineV2 = null;
      }, 2500);
    } catch {
      this.complianceUploadPipelineV2 = { name: file.name, stage: 'failed', message: 'Upload failed' };
      this.toast.show('Internal document upload failed.', 'error', 4000);
    } finally {
      this.uploadingCompliance = false;
    }
  }
}
