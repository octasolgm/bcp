import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../components/nd/nd-point-sort-controls.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdGapAnalysisComponent } from '../nd/gap-analysis/nd-gap-analysis.component';
import { REGUL_PIPELINE_FULL, REGUL_PIPELINE_HYBRID_V5 } from '../../../lib/nd/regul-fields';
import type { DocAnalysisReadyState } from '../../../lib/nd/doc-analysis-ready';
import type { GovPoint, StoredDocumentDto } from '../../services/api.service';
import type { NdGovPoint } from '../../../lib/regulation-catalog-utils';
import { AnalyseRegulComponent } from '../analyse-regul/analyse-regul.component';
import { AnalyseBase } from '../shared/analyse-base';
import type { AnalysisPoint } from '../../../lib/nd/types';
import { NdPipelinePanelService } from '../../services/nd/nd-pipeline-panel.service';
import type { NdLocalExtractionSection } from '../../services/nd/nd-api.service';

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
    InProgressNavButtonComponent,
    NdGapPointDetailComponent,
    NdPointSortControlsComponent,
    NdPointNumberTreeComponent,
    NdStatusBadgeComponent,
    NdGapAnalysisComponent,
  ],
  templateUrl: '../analyse-regul/analyse-regul.component.html',
  styleUrl: '../analyse-regul/analyse-regul.component.scss',
})
export class AnalyseRegulFullV2Component extends AnalyseRegulComponent {
  override readonly versionLabel = '';
  override readonly versionPath: string = '/analyse-regul-full-v2';

  protected override readonly regulWorkflowEngineId: string = REGUL_PIPELINE_HYBRID_V5;
  protected override readonly regulAnalysisRoute: string = '/nd/analyse-regul-full-v2';
  protected override showReversePipelineUi = false;

  private readonly pipelinePanel = inject(NdPipelinePanelService);
  private pipelineDocsTimer: ReturnType<typeof setTimeout> | null = null;
  private pipelineDocsDestroyed = false;
  /** True once a poll tick has found at least one doc AND finished a readiness fetch for it —
   * the slow-poll interval only kicks in after this, so no fixed delay is ever "too short" for
   * how long the doc lists actually took to load. */
  private hasSyncedPipelineDocsOnce = false;

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

  override ngOnInit(): void {
    this.pipelinePanel.activate();
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
    this.pipelinePanel.deactivate();
    super.ngOnDestroy();
  }

  /** Recursive self-pacing poll: fires every ~200ms until the doc lists have actually populated
   * and a readiness fetch has completed for them at least once, then drops to a slow ~4s
   * steady-state cadence. Recursing off the end of each cycle (rather than a fixed setInterval)
   * means the fast phase naturally stretches to cover however long the doc lists really take to
   * load, instead of assuming a fixed number of milliseconds will be enough. */
  private async pollPipelineDocs(): Promise<void> {
    if (this.pipelineDocsDestroyed) return;
    await this.syncPipelineDocs();
    if (this.pipelineDocsDestroyed) return;
    const delayMs = this.hasSyncedPipelineDocsOnce ? 4000 : 200;
    this.pipelineDocsTimer = setTimeout(() => void this.pollPipelineDocs(), delayMs);
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
    this.pipelinePanel.setDocs(docs);

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
    this.pipelinePanel.setPhase(this.ndRegulPipelinePhase);
    this.pipelinePanel.setRetrievalPreview(preview);
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
    await super.loadNdRunPoints(runId);
    this.validateRunEngine();
    await this.loadPipelineRetrievalPreviewAndFullPoints(runId);
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
}
