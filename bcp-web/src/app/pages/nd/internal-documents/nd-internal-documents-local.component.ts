import { Component, HostListener, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { NdApiService, NdLocalExtractionResult, NdOcrEngine } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdShellFocusService } from '../../../services/nd/nd-shell-focus.service';
import { NdWorkspaceNavService } from '../../../services/nd/nd-workspace-nav.service';
import { isActiveDocumentRun } from '../../../services/active-analysis-sessions.service';
import { ToastService } from '../../../services/toast.service';
import { startPanelResize } from '../../shared/panel-resize';
import { formatBytes, formatDate, formatTableDate } from '../../../../lib/nd/utils';
import { catalogPdfPageLabel } from '../../../../lib/nd/doc-page-count';
import {
  docAnalysisReadyClass,
  docAnalysisReadyLabel,
  internalAnalysisReadyState,
  usedInAnalysesLabel,
  type DocAnalysisReadyState,
} from '../../../../lib/nd/doc-analysis-ready';
import {
  compareDateIso,
  compareNumber,
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';
import { ndAnalysisRunTarget } from '../../../../lib/nd/run-links';
import type { AnalysisRunSummary, InternalDocument, InternalDocumentSection } from '../../../../lib/nd/types';
import { NdInternalDocumentSectionsPanelComponent } from './nd-internal-document-sections-panel.component';

type DocSortColumn = 'title' | 'uploaded' | 'size' | 'source' | 'pages' | 'analyses';

type InternalDocAnalysisRun = {
  id: string;
  source: 'nd_analysis' | 'legacy_analysis' | string;
  name: string;
  regulationFileName?: string | null;
  internalFileName?: string | null;
  status: string;
  pointCount: number;
  completedPoints?: number;
  failedPoints?: number;
  runningPoints?: number;
  isActive?: boolean;
  sessionAvailable?: boolean;
  dualVerifySessionId?: string | null;
  complianceSessionId?: string | null;
  createdAt: string;
  updatedAt?: string;
};

/** How long a just-uploaded row survives list refreshes that don't return it yet. */
const RecentUploadKeepMs = 90_000;

/**
 * Same document library UI as nd-internal-documents.component (columns, filters, upload, sections
 * panel, analysis history, delete/restore) but "Parse" runs the local pipeline (PdfPig + Tesseract,
 * no Landing AI, no per-page cost, nothing leaves this server) instead of calling Landing AI. Local
 * extraction is a single synchronous call that parses AND extracts sections together, so there is
 * one combined action instead of separate Parse / Extract steps, and there is no page-repair or
 * markdown export (the local pipeline doesn't produce either). Everything else — upload, delete,
 * download source, analysis history, filters/sort, the sections panel — is unchanged from the
 * original page. See docs/discussion/REGUL-PIPELINE-BUILD-PLAN.md.
 */
@Component({
  selector: 'app-nd-internal-documents-local',
  standalone: true,
  imports: [CommonModule, FormsModule, NdInternalDocumentSectionsPanelComponent, NdPageAlertComponent],
  templateUrl: './nd-internal-documents-local.component.html',
  styleUrls: ['./nd-internal-documents.component.scss', '../nd-shared.scss'],
})
export class NdInternalDocumentsLocalComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);
  readonly auth = inject(NdAuthService);
  private readonly shellFocus = inject(NdShellFocusService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);

  /** Which local OCR engine this route uses — set via route data ({engine: 'tesseract' | 'rapidocr'}),
   * so the exact same page/component serves both /internal-documents-new and
   * /internal-documents-rapidocr, just pointed at a different backend engine. */
  readonly engine: NdOcrEngine = (this.route.snapshot.data['engine'] as NdOcrEngine) ?? 'tesseract';

  get engineLabel(): string {
    switch (this.engine) {
      case 'rapidocr':
        return 'RapidOCR';
      case 'docling-light':
        return 'Docling (Light)';
      case 'docling-glm':
        return 'Docling (GLM-OCR)';
      default:
        return 'Tesseract';
    }
  }

  private static readonly PANEL_SPLIT_KEY = 'nd-internal-docs-local-sections-panel-split';

  docs: InternalDocument[] = [];
  file: File | null = null;
  loading = true;
  uploading = false;
  parsingId: string | null = null;
  extractingId: string | null = null;
  deletingId: string | null = null;
  exportingFileId: string | null = null;
  error = '';
  message = '';
  /** Document id → upload timestamp, so fresh rows survive an eventually-consistent list. */
  private readonly recentUploads = new Map<string, number>();
  /** Full local extraction result per document (sections come from here, not from Landing AI). */
  private readonly localResults = new Map<string, NdLocalExtractionResult>();
  sectionsFor: InternalDocument | null = null;
  showParsedText = false;
  sectionRows: InternalDocumentSection[] = [];
  analysisFor: InternalDocument | null = null;
  analysisRuns: InternalDocAnalysisRun[] = [];
  loadingAnalysisRuns = false;
  analysisLoadError: string | null = null;
  selectedDocId: string | null = null;
  searchQuery = '';
  parseFilter = '';
  sourceFilter = '';
  sortColumn: DocSortColumn = 'uploaded';
  sortDir: SortDir = 'desc';
  /** Left (table) share when sections panel is open. */
  leftPanelPct = 20;

  async ngOnInit(): Promise<void> {
    this.restorePanelSplit();
    await this.auth.refreshProfile();
    await this.load();
  }

  get showSectionsPanel(): boolean {
    return !!this.sectionsFor;
  }

  get panelGridColumns(): string | null {
    if (!this.showSectionsPanel) return null;
    const left = this.leftPanelPct;
    const right = 100 - left;
    return `minmax(0, ${left}%) 10px minmax(0, ${right}%)`;
  }

  startSectionsPanelResize(event: MouseEvent): void {
    const layout = (event.target as HTMLElement).closest('.library-layout');
    const containerWidth = layout?.clientWidth ?? 1200;
    startPanelResize(
      {
        kind: 'setup-split',
        startX: event.clientX,
        startY: event.clientY,
        startVal: this.leftPanelPct,
        containerWidth,
      },
      event,
      (_kind, value) => {
        this.leftPanelPct = value;
      },
      { 'setup-split': { min: 14, max: 40 } },
    );
    const onUp = () => {
      window.removeEventListener('mouseup', onUp);
      localStorage.setItem(
        NdInternalDocumentsLocalComponent.PANEL_SPLIT_KEY,
        String(this.leftPanelPct),
      );
    };
    window.addEventListener('mouseup', onUp);
  }

  private restorePanelSplit(): void {
    try {
      const saved = localStorage.getItem(NdInternalDocumentsLocalComponent.PANEL_SPLIT_KEY);
      if (!saved) return;
      const pct = Number.parseFloat(saved);
      if (Number.isFinite(pct) && pct >= 14 && pct <= 40) {
        this.leftPanelPct = pct;
      }
    } catch {
      /* ignore storage errors */
    }
  }

  get canUpload(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get canParse(): boolean {
    return this.canUpload;
  }

  get canDelete(): boolean {
    return this.canUpload;
  }

  showRowSectionsButton(_doc: InternalDocument): boolean {
    return true;
  }

  isDocParsed(doc: InternalDocument): boolean {
    return (doc.parseStatus ?? '').toLowerCase() === 'parsed';
  }

  hasExtractedSections(doc: InternalDocument): boolean {
    return (
      (doc.sectionExtractStatus ?? '').toLowerCase() === 'extracted' ||
      (doc.sectionCount ?? 0) > 0
    );
  }

  analysisReadyState(doc: InternalDocument): DocAnalysisReadyState {
    return internalAnalysisReadyState(doc);
  }

  analysisReadyLabel(doc: InternalDocument): string {
    if (doc.generatedByAnalysis) return 'Generated by analysis';
    return docAnalysisReadyLabel(this.analysisReadyState(doc));
  }

  analysisReadyClass(doc: InternalDocument): string {
    return docAnalysisReadyClass(this.analysisReadyState(doc));
  }

  /** True once parsed but sections somehow didn't land — local pipeline always does both together. */
  showsParsedPendingExtractChips(doc: InternalDocument): boolean {
    return (
      !doc.generatedByAnalysis &&
      this.isDocParsed(doc) &&
      !this.hasExtractedSections(doc) &&
      !this.isParsingDoc(doc)
    );
  }

  usedInAnalysesLabel = usedInAnalysesLabel;

  sectionExtractClass(status?: string): string {
    if (status === 'extracted') return 'completed';
    if (status === 'processing') return 'running';
    if (status === 'failed') return 'failed';
    return 'pending';
  }

  sectionExtractLabel(status?: string): string {
    if (status === 'extracted') return 'Extracted';
    if (status === 'processing') return 'Extracting…';
    if (status === 'failed') return 'Failed';
    return 'Pending extract';
  }

  isParsingDoc(doc: InternalDocument): boolean {
    return this.parsingId === doc.id;
  }

  parseStatusText(_doc: InternalDocument): string {
    return 'Parsing locally…';
  }

  parseProgressPct(_doc: InternalDocument): number | null {
    return null;
  }

  parseButtonLabel(doc: InternalDocument): string {
    if (this.isParsingDoc(doc)) return 'Parsing…';
    if (doc.parseStatus === 'failed') return 'Retry parse (Local)';
    if (this.isDocParsed(doc)) return 'Re-parse (Local)';
    return 'Parse (Local)';
  }

  /** Step 1 only — parse to text with page references. Does not touch section/point extraction. */
  async handleParse(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (!this.canParse || this.parsingId) return;
    this.parsingId = doc.id;
    this.error = '';
    this.message = '';
    const idx = this.docs.findIndex((d) => d.id === doc.id);
    if (idx >= 0) {
      this.docs[idx] = { ...this.docs[idx], parseStatus: 'processing', parseError: null };
    }
    try {
      const res = await this.api.localParseById(doc.id, this.engine);
      if (!res.success || !res.data) {
        this.error = res.message || `Local parse failed for "${doc.title}".`;
        if (idx >= 0) this.docs[idx] = { ...this.docs[idx], parseStatus: 'failed' };
        return;
      }
      const data = res.data;
      this.localResults.set(doc.id, data);
      // eslint-disable-next-line no-console
      console.log(`[local-parse] ${doc.title}`, data);
      const failed = (data.status ?? 'parsed').toLowerCase() === 'failed';
      if (idx >= 0) {
        // Re-parsing invalidates any previous extract — the backend resets it too, mirror that here.
        this.docs[idx] = {
          ...this.docs[idx],
          parseStatus: failed ? 'failed' : 'parsed',
          parseError: data.error ?? null,
          parsedAt: data.parsedAt ?? new Date().toISOString(),
          parsedByName: this.auth.profile()?.fullName ?? this.docs[idx].parsedByName,
          pageCount: data.totalPages ?? this.docs[idx].pageCount,
          sectionExtractStatus: 'pending',
          sectionCount: 0,
        };
      }
      this.message = failed
        ? `Local parse failed for "${doc.title}".`
        : `Parsed "${doc.title}" locally — ${data.totalPages ?? 0} page(s), ${data.ocrPageCount ?? 0} via OCR. Click Extract to find sections.`;
      if (!failed) this.toast.show(this.message, 'success', 4000);
    } catch (err) {
      this.error = err instanceof Error ? err.message : `Local parse failed for "${doc.title}".`;
      if (idx >= 0) this.docs[idx] = { ...this.docs[idx], parseStatus: 'failed' };
    } finally {
      this.parsingId = null;
    }
  }

  /** Step 2 — split the already-parsed text into sections. Requires handleParse to have run first. */
  async handleExtractSections(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (doc.parseStatus !== 'parsed') {
      this.toast.show('Parse the document first', 'warning', 4000);
      return;
    }
    this.extractingId = doc.id;
    this.error = '';
    const idx = this.docs.findIndex((d) => d.id === doc.id);
    if (idx >= 0) this.docs[idx] = { ...this.docs[idx], sectionExtractStatus: 'processing' };
    try {
      const res = await this.api.localExtractById(doc.id, this.engine);
      if (!res.success || !res.data) {
        this.error = res.message || `Local extract failed for "${doc.title}".`;
        if (idx >= 0) this.docs[idx] = { ...this.docs[idx], sectionExtractStatus: 'failed' };
        return;
      }
      const data = res.data;
      this.localResults.set(doc.id, data);
      console.log(`[local-extract] ${doc.title}`, data);
      const failed = (data.extractStatus ?? 'extracted').toLowerCase() === 'failed';
      if (idx >= 0) {
        this.docs[idx] = {
          ...this.docs[idx],
          sectionExtractStatus: failed ? 'failed' : 'extracted',
          sectionCount: data.sectionCount ?? 0,
          sectionExtractedAt: data.extractedAt ?? new Date().toISOString(),
        };
        if (this.sectionsFor?.id === doc.id) {
          this.sectionsFor = this.docs[idx];
          this.sectionRows = this.mapLocalSections(data);
        }
      }
      this.message = failed
        ? `Local extract failed for "${doc.title}".`
        : `Extracted ${data.sectionCount ?? 0} section(s) from "${doc.title}" (local).`;
      if (!failed) this.toast.show(this.message, 'success', 4000);
    } catch (err) {
      this.error = err instanceof Error ? err.message : `Local extract failed for "${doc.title}".`;
      if (idx >= 0) this.docs[idx] = { ...this.docs[idx], sectionExtractStatus: 'failed' };
    } finally {
      this.extractingId = null;
    }
  }

  private mapLocalSections(result: NdLocalExtractionResult): InternalDocumentSection[] {
    return (result.sections ?? []).map((s, i) => ({
      id: `${s.clauseNo}-${i}`,
      sectionRef: s.clauseNo,
      sectionText: s.clauseText,
      sourcePage: s.sourcePage,
      displayOrder: i,
    }));
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const res = await this.api.getInternalDocuments();
    if (res.success && res.data) {
      this.docs = this.keepRecentUploads(res.data as InternalDocument[]);
      await this.mergeLocalStatuses();
    } else if (!silent || this.docs.length === 0) {
      this.error = res.message ?? 'Failed to load documents';
    }
    this.loading = false;
  }

  private async mergeLocalStatuses(): Promise<void> {
    const ids = this.docs.map((d) => d.id).filter(Boolean);
    if (!ids.length) return;
    try {
      const res = await this.api.localExtractStatusBatch(ids, this.engine);
      if (!res.success || !res.data) return;
      for (const doc of this.docs) {
        const local = res.data[doc.id];
        if (!local) continue;
        this.localResults.set(doc.id, local);
        doc.parseStatus = (local.status ?? 'pending').toLowerCase();
        doc.parseError = local.error ?? null;
        doc.sectionExtractStatus = (local.extractStatus ?? 'pending').toLowerCase();
        doc.sectionCount = local.sectionCount ?? 0;
        doc.parsedAt = local.parsedAt ?? doc.parsedAt;
        doc.sectionExtractedAt = local.extractedAt ?? doc.sectionExtractedAt;
        doc.pageCount = local.totalPages ?? doc.pageCount;
      }
    } catch {
      // Status is a nice-to-have on load — a failure here shouldn't block the document list.
    }
  }

  async openDocument(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.error = '';
    const res = await this.api.getInternalDocumentFileUrl(doc.id);
    if (res.success && res.data?.url) {
      window.open(res.data.url, '_blank', 'noopener');
      return;
    }
    this.error = res.message ?? 'Could not open document';
  }

  async downloadInternalFile(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (this.exportingFileId) return;
    this.exportingFileId = doc.id;
    this.error = '';
    this.toast.show('Preparing PDF download…', 'info', 4000);
    const res = await this.api.downloadInternalFileExport(doc.id);
    if (!res.success) {
      this.error = res.message ?? 'Failed to download file';
      this.toast.show(res.message ?? 'Download failed', 'error', 5000);
    }
    this.exportingFileId = null;
  }

  async handleDelete(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (
      !confirm(
        `Remove "${doc.title}" from the library?\n\nNothing is deleted from the database — file data is kept.`,
      )
    ) {
      return;
    }
    this.deletingId = doc.id;
    this.error = '';
    const res = await this.api.hideInternalDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Document removed from library';
      this.removeInternalDocFromList(doc);
      this.workspaceNav.bumpNavBadges({ internalDocuments: -1 });
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to delete document';
    }
    this.deletingId = null;
  }

  actorLabel(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    return trimmed || '—';
  }

  get visibleDocs(): InternalDocument[] {
    let list = this.docs.filter((doc) => {
      if (!matchesSearch(this.searchQuery, [doc.title, doc.originalFileName, doc.department])) {
        return false;
      }
      if (this.sourceFilter && (doc.source ?? 'nd') !== this.sourceFilter) return false;
      if (this.parseFilter && (doc.parseStatus ?? 'pending') !== this.parseFilter) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'title':
          return compareText(a.title, b.title, this.sortDir);
        case 'size':
          return compareNumber(a.sizeBytes ?? 0, b.sizeBytes ?? 0, this.sortDir);
        case 'pages':
          return compareNumber(a.pageCount ?? 0, b.pageCount ?? 0, this.sortDir);
        case 'analyses':
          return compareNumber(a.analysisRunCount ?? 0, b.analysisRunCount ?? 0, this.sortDir);
        case 'source':
          return compareText(a.source ?? 'nd', b.source ?? 'nd', this.sortDir);
        case 'uploaded':
        default:
          return compareDateIso(a.uploaded, b.uploaded, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.sourceFilter) || !!this.parseFilter;
  }

  toggleSort(column: DocSortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'uploaded');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: DocSortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.sourceFilter = '';
    this.parseFilter = '';
  }

  onFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.file = input.files?.[0] ?? null;
  }

  async handleUpload(): Promise<void> {
    if (!this.file) return;
    this.uploading = true;
    this.error = '';
    const file = this.file;
    const res = await this.api.uploadInternalDocument(file);
    if (res.success) {
      const data = res.data as {
        id?: string;
        title?: string;
        originalFileName?: string;
        parseStatus?: string;
      };
      this.addOptimisticUpload(data, file);
      this.message = 'Uploaded — status is Not parsed. Click Parse &amp; Extract (Local) when ready.'.replace(
        '&amp;',
        '&',
      );
      this.error = '';
      this.toast.show(this.message, 'success', 4000);
      this.workspaceNav.bumpNavBadges({ internalDocuments: 1 });
      void this.load(true);
    } else {
      this.error = res.message ?? 'Upload failed';
    }
    this.uploading = false;
  }

  /** Show the new row straight away instead of waiting for the next list refresh. */
  private addOptimisticUpload(
    data: { id?: string; title?: string; originalFileName?: string; parseStatus?: string },
    file: File,
  ): void {
    if (!data?.id) return;
    this.recentUploads.set(data.id, Date.now());
    const now = new Date().toISOString();
    const optimistic: InternalDocument = {
      id: data.id,
      source: 'nd',
      title: data.title ?? file.name,
      originalFileName: data.originalFileName ?? file.name,
      uploaded: now,
      uploadedAt: now,
      sizeBytes: file.size,
      parseStatus: 'pending',
      isHidden: false,
    };
    this.docs = [optimistic, ...this.docs.filter((d) => d.id !== optimistic.id)];
  }

  private keepRecentUploads(incoming: InternalDocument[]): InternalDocument[] {
    if (!this.recentUploads.size) return incoming;
    const cutoff = Date.now() - RecentUploadKeepMs;
    const known = new Set(incoming.map((d) => d.id));
    const pending: InternalDocument[] = [];
    for (const [id, at] of [...this.recentUploads]) {
      if (at < cutoff) {
        this.recentUploads.delete(id);
        continue;
      }
      if (known.has(id)) continue;
      const local = this.docs.find((d) => d.id === id);
      if (local) pending.push(local);
    }
    return pending.length ? [...pending, ...incoming] : incoming;
  }

  private removeInternalDocFromList(doc: InternalDocument): void {
    this.docs = this.docs.filter((row) => row.id !== doc.id);
    this.recentUploads.delete(doc.id);
    this.localResults.delete(doc.id);
    if (this.parsingId === doc.id) this.parsingId = null;
    if (this.sectionsFor?.id === doc.id) this.closeSections();
  }

  openSections(doc: InternalDocument, event?: Event): void {
    event?.stopPropagation();
    this.selectedDocId = doc.id;
    this.sectionsFor = doc;
    const local = this.localResults.get(doc.id);
    this.sectionRows = local ? this.mapLocalSections(local) : [];
    // Nothing extracted yet but the doc has been parsed — show the parsed text right away instead
    // of an empty sections list the user has to click past.
    this.showParsedText = this.sectionRows.length === 0 && !!local?.markdownText;
    this.shellFocus.setRegulationPointsPanelOpen(true);
  }

  closeSections(): void {
    this.sectionsFor = null;
    this.showParsedText = false;
    this.sectionRows = [];
    this.shellFocus.setRegulationPointsPanelOpen(false);
  }

  /** Full parsed markdown (with page refs) for the given doc, once Parse has run — independent of Extract. */
  parsedTextFor(doc: InternalDocument | null): string | null {
    if (!doc) return null;
    return this.localResults.get(doc.id)?.markdownText ?? null;
  }

  toggleParsedText(): void {
    this.showParsedText = !this.showParsedText;
  }

  /** Escape closes the topmost open panel — analysis picker overlays the sections panel. */
  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    if (this.analysisFor) this.closeAnalysisPicker();
    else if (this.sectionsFor) this.closeSections();
  }

  async openInternalSourcePage(docId: string, page: number): Promise<void> {
    const ok = await this.api.openInternalDocumentPdf(docId, page);
    if (!ok) this.toast.show('Could not open document PDF', 'error');
  }

  async viewAnalysis(doc: InternalDocument): Promise<void> {
    this.loadingAnalysisRuns = true;
    this.analysisLoadError = null;
    this.selectedDocId = doc.id;
    this.analysisFor = doc;
    this.analysisRuns = [];

    const res = await this.api.getInternalDocumentAnalysisRuns(doc.id);
    this.loadingAnalysisRuns = false;

    if (!res.success || !res.data) {
      this.analysisLoadError = res.message ?? 'Could not load analysis history.';
      return;
    }

    const runs = (res.data as InternalDocAnalysisRun[]).slice().sort((a, b) => {
      const aActive = isActiveDocumentRun(a);
      const bActive = isActiveDocumentRun(b);
      if (aActive !== bActive) return aActive ? -1 : 1;
      return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
    });

    this.analysisRuns = runs;
  }

  closeAnalysisPicker(): void {
    this.analysisFor = null;
    this.analysisRuns = [];
    this.analysisLoadError = null;
    this.loadingAnalysisRuns = false;
  }

  isRunInProgress(run: InternalDocAnalysisRun): boolean {
    return isActiveDocumentRun(run);
  }

  openAnalysisRun(run: InternalDocAnalysisRun): void {
    this.closeAnalysisPicker();

    if (run.sessionAvailable === false && run.source !== 'nd_analysis') {
      this.toast.show('This analysis session is no longer available', 'warning', 5000);
      return;
    }

    const summary: AnalysisRunSummary = {
      id: run.id,
      source:
        run.source === 'nd_analysis'
          ? 'nd_analysis'
          : run.dualVerifySessionId
            ? 'legacy_dual_verify'
            : 'legacy_analysis',
      name: run.name,
      status: run.status,
      totalPointsCount: run.pointCount,
      processedPointsCount: run.completedPoints ?? 0,
      createdAt: run.createdAt,
      legacySessionId: run.dualVerifySessionId ?? undefined,
      legacyHref: run.complianceSessionId
        ? `/nd/gap-analysis?saved=compliance:${run.complianceSessionId}`
        : undefined,
    };

    const target = ndAnalysisRunTarget(summary, this.auth.getRole());
    void this.router.navigate(target.routerLink, { queryParams: target.queryParams });
  }

  runStatusLabel(run: InternalDocAnalysisRun): string {
    if (this.isRunInProgress(run)) return 'In progress';
    if (run.status === 'completed') return 'Completed';
    if (run.status === 'failed') return 'Failed';
    return run.status;
  }

  formatRunWhen(iso: string): string {
    return formatDate(iso);
  }

  isSelected(doc: InternalDocument): boolean {
    return (
      this.selectedDocId === doc.id ||
      this.sectionsFor?.id === doc.id ||
      this.analysisFor?.id === doc.id
    );
  }

  formatDate = formatDate;
  formatTableDate = formatTableDate;
  formatBytes = formatBytes;

  docPageMeta(doc: InternalDocument): string {
    return catalogPdfPageLabel(doc.pageCount, this.isParsingDoc(doc));
  }
}
