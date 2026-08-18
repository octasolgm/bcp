import { Component, HostListener, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import {
  dedupeRegulationDocuments,
  findRegulationDocumentIndex,
  isRegulationExtractSuccess,
  isRegulationExtractTerminal,
  prepareRegulationPointsResponse,
  regulationDocLookupIds,
  sortRegulationDocuments,
} from '../../../../lib/regulation-catalog-utils';
import { formatDate, formatTableDate } from '../../../../lib/nd/utils';
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
import type { Department, RegulationDocument, RegulationPoint } from '../../../../lib/nd/types';
import { NdRegulationPointsPanelComponent } from './nd-regulation-points-panel.component';
import { NdManualRegulationPointsPanelComponent } from './nd-manual-regulation-points-panel.component';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import { NdShellFocusService } from '../../../services/nd/nd-shell-focus.service';
import { NdWorkspaceNavService } from '../../../services/nd/nd-workspace-nav.service';
import { ToastService } from '../../../services/toast.service';
import { startPanelResize } from '../../shared/panel-resize';
import { formatPointPageRef, resolveRegulationPdfPage } from '../../../../lib/nd/regulation-pdf-page';

export type RegulationPointSearchHit = {
  id: string;
  pointNumber: string;
  pointTitle?: string | null;
  snippet?: string;
  pageReference?: string | null;
  pdfPage?: number | null;
  storedDocumentId?: string | null;
};

export type RegulationPointSearchGroup = {
  documentId: string;
  documentName: string;
  departmentName?: string | null;
  isManual?: boolean;
  storedDocumentId?: string | null;
  points: RegulationPointSearchHit[];
};

/** How long a just-uploaded row survives list refreshes that don't return it yet. */
const RecentUploadKeepMs = 90_000;

@Component({
  selector: 'app-nd-regulation-documents',
  standalone: true,
  imports: [CommonModule, FormsModule, NdRegulationPointsPanelComponent, NdManualRegulationPointsPanelComponent, NdPageAlertComponent],
  templateUrl: './nd-regulation-documents.component.html',
  styleUrls: ['./nd-regulation-documents.component.scss', '../nd-shared.scss'],
})
export class NdRegulationDocumentsComponent implements OnInit, OnDestroy {
  private static readonly PANEL_SPLIT_KEY = 'nd-reg-panel-split-left';

  private readonly api = inject(NdApiService);
  private readonly shellFocus = inject(NdShellFocusService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);
  readonly auth = inject(NdAuthService);
  readonly formatPointPageRef = formatPointPageRef;

  docs: RegulationDocument[] = [];
  departments: Department[] = [];
  deptFilter = '';
  statusFilter = '';
  searchQuery = '';
  sortColumn: 'name' | 'department' | 'points' | 'created' | 'status' = 'created';
  sortDir: SortDir = 'desc';
  uploadDept = '';
  file: File | null = null;
  loading = true;
  uploading = false;
  extractingId: string | null = null;
  parsingId: string | null = null;
  refreshingPagesId: string | null = null;
  repairingPointsId: string | null = null;
  exportingPointsId: string | null = null;
  hidingId: string | null = null;
  showDeleted = false;
  savingDeptId: string | null = null;
  error = '';
  message = '';
  /** Document id → upload timestamp, so fresh rows survive an eventually-consistent list. */
  private readonly recentUploads = new Map<string, number>();

  selectedDoc: RegulationDocument | null = null;
  selectedPoints: RegulationPoint[] = [];
  pointsSource = '';
  pointsLoading = false;
  showPointsPanel = false;
  /** Left (table) share when points panel is open — kept small by default. */
  leftPanelPct = 20;
  highlightPointNumber = '';
  globalPointSearch = '';
  pointSearchLoading = false;
  pointSearchResults: RegulationPointSearchGroup[] = [];
  pointSearchTotal = 0;
  pointSearchError = '';
  private pointSearchTimer: ReturnType<typeof setTimeout> | null = null;
  private extractPollTimer: ReturnType<typeof setInterval> | null = null;
  private readonly pollingExtractIds = new Set<string>();
  private readonly pollingParseIds = new Set<string>();
  private readonly completionNotified = new Set<string>();
  private readonly pollStuckSince = new Map<string, { key: string; since: number }>();
  private pollInFlight = false;
  private departmentsLoaded = false;

  async ngOnInit(): Promise<void> {
    this.restorePanelSplit();
    void this.auth.refreshProfile();
    if (this.route.snapshot.queryParamMap.get('deleted') === '1') {
      await this.router.navigate(['/nd/regulation-documents/deleted'], { replaceUrl: true });
      return;
    }
    this.syncDeletedFromRoute();
    this.route.data.subscribe(() => this.syncDeletedFromRoute());
  }

  private syncDeletedFromRoute(): void {
    const wasDeleted = this.showDeleted;
    this.showDeleted = !!this.route.snapshot.data['deletedOnly'];
    if (wasDeleted && !this.showDeleted) this.closePointsPanel();
    void this.refreshCatalog();
  }

  private async refreshCatalog(): Promise<void> {
    const tasks: Promise<void>[] = [this.loadDocs()];
    if (!this.departmentsLoaded) {
      tasks.push(
        this.loadDepartments().then(() => {
          this.departmentsLoaded = true;
        }),
      );
    }
    await Promise.all(tasks);
  }

  get panelGridColumns(): string | null {
    if (!this.showPointsPanel) return null;
    const left = this.leftPanelPct;
    const right = 100 - left;
    return `minmax(0, ${left}%) 10px minmax(0, ${right}%)`;
  }

  startPointsPanelResize(event: MouseEvent): void {
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
        NdRegulationDocumentsComponent.PANEL_SPLIT_KEY,
        String(this.leftPanelPct),
      );
    };
    window.addEventListener('mouseup', onUp);
  }

  private restorePanelSplit(): void {
    try {
      const saved = localStorage.getItem(NdRegulationDocumentsComponent.PANEL_SPLIT_KEY);
      if (!saved) return;
      const pct = Number.parseFloat(saved);
      if (Number.isFinite(pct) && pct >= 14 && pct <= 40) {
        this.leftPanelPct = pct;
      }
    } catch {
      /* ignore storage errors */
    }
  }

  ngOnDestroy(): void {
    this.stopExtractPolling();
    this.shellFocus.setRegulationPointsPanelOpen(false);
    if (this.pointSearchTimer) clearTimeout(this.pointSearchTimer);
  }

  private stopExtractPolling(): void {
    if (this.extractPollTimer) {
      clearInterval(this.extractPollTimer);
      this.extractPollTimer = null;
    }
  }

  private get extractPollMs(): number {
    return this.auth.isDemoViewer() ? 1200 : 2500;
  }

  private ensureExtractPolling(): void {
    if (this.extractPollTimer || (!this.pollingExtractIds.size && !this.pollingParseIds.size)) return;
    this.extractPollTimer = setInterval(() => void this.pollProcessingDocs(), this.extractPollMs);
  }

  private trackExtractingDoc(docId: string): void {
    this.pollingParseIds.delete(docId);
    this.pollingExtractIds.add(docId);
    this.extractingId = docId;
    this.ensureExtractPolling();
    void this.pollProcessingDocs();
  }

  private clearExtractPolling(...ids: string[]): void {
    for (const id of ids) {
      if (!id) continue;
      this.pollingExtractIds.delete(id);
      if (this.extractingId === id) this.extractingId = null;
    }
  }

  private trackParsingDoc(docId: string): void {
    this.pollingExtractIds.delete(docId);
    this.pollingParseIds.add(docId);
    this.pollStuckSince.delete(docId);
    this.parsingId = docId;
    this.ensureExtractPolling();
    void this.pollProcessingDocs();
  }

  private async pollProcessingDocs(): Promise<void> {
    if (this.pollInFlight) return;
    const activeIds = new Set([...this.pollingExtractIds, ...this.pollingParseIds]);
    if (!activeIds.size) {
      this.stopExtractPolling();
      return;
    }
    this.pollInFlight = true;
    try {
      for (const id of activeIds) {
        // Skip if another handler already finished this doc while we were awaiting.
        const isParsePoll = this.pollingParseIds.has(id);
        const isExtractPoll = this.pollingExtractIds.has(id);
        if (!isParsePoll && !isExtractPoll) continue;

        const res = await this.api.getRegulationDocument(id);
        if (!this.pollingParseIds.has(id) && !this.pollingExtractIds.has(id)) continue;
        if (!res.success || !res.data) continue;
        const doc = res.data as RegulationDocument;
        const idx = findRegulationDocumentIndex(this.docs, id);
        if (idx >= 0) {
          let merged = { ...this.docs[idx], ...doc };
          const prevPts = this.docs[idx].pointCount ?? 0;
          const nextPts = doc.pointCount ?? 0;
          if (nextPts <= 0 && prevPts > 0) {
            merged.pointCount = prevPts;
          } else if (
            prevPts > 0 &&
            nextPts > prevPts &&
            !this.pollingExtractIds.has(id)
          ) {
            // Status poll must not inflate canonical list counts from a stale single-doc fetch.
            merged.pointCount = prevPts;
          }
          merged = this.preserveInFlightExtractionState(id, this.docs[idx], merged, isParsePoll);
          this.docs[idx] = merged;
          if (this.selectedDoc && findRegulationDocumentIndex([this.selectedDoc], id) >= 0) {
            this.selectedDoc = this.docs[idx];
          }
        }
        const st = (doc.extractionStatus ?? '').toLowerCase();
        if (st === 'processing') {
          if (isParsePoll) {
            this.message = doc.extractionProgressLabel?.trim() || 'Parsing document…';
            const progressKey = `${doc.extractionProgressLabel ?? ''}|${doc.extractionProgressPct ?? ''}`;
            const stuck = this.pollStuckSince.get(id);
            if (!stuck || stuck.key !== progressKey) {
              this.pollStuckSince.set(id, { key: progressKey, since: Date.now() });
            } else if (Date.now() - stuck.since > 60_000) {
              this.pollingParseIds.delete(id);
              this.pollStuckSince.delete(id);
              if (this.parsingId === id) this.parsingId = null;
              const stallMsg = `Parse stalled for "${doc.name}". Click Parse to try again.`;
              this.error = stallMsg;
              this.toast.show(stallMsg, 'error', 6000);
              continue;
            }
          } else {
            this.message = doc.extractionProgressLabel?.trim() || `Extracting "${doc.name}"…`;
          }
        }

        if (isParsePoll && st === 'parsed') {
          this.pollingParseIds.delete(id);
          this.pollStuckSince.delete(id);
          if (this.parsingId === id) this.parsingId = null;
          const notifyKey = `parse:${id}`;
          if (!this.completionNotified.has(notifyKey)) {
            this.completionNotified.add(notifyKey);
            this.message = `Parse complete — "${doc.name}"`;
            this.toast.show(this.message, 'success', 4000);
          }
          this.patchRegulationDoc(doc, {
            extractionStatus: 'parsed',
            extractionProgressLabel: null,
            extractionProgressPct: null,
          });
          continue;
        }

        if (isParsePoll && (st === 'failed' || st === 'paused')) {
          this.pollingParseIds.delete(id);
          this.pollStuckSince.delete(id);
          if (this.parsingId === id) this.parsingId = null;
          if (st === 'failed') this.error = `Parse failed for "${doc.name}"`;
          continue;
        }

        if (!isParsePoll && isRegulationExtractTerminal(st)) {
          const row = idx >= 0 ? this.docs[idx] : doc;
          const pts = Math.max(row.pointCount ?? 0, doc.pointCount ?? 0);
          // Don't treat "extracted with 0 pts" as terminal while extract POST may still be writing.
          if ((st === 'extracted' || st === 'completed') && pts <= 0) {
            continue;
          }
          this.clearExtractPolling(id, row.id, row.storedDocumentId ?? '');
          const notifyKey = `extract:${row.id}`;
          if (!this.completionNotified.has(notifyKey)) {
            this.completionNotified.add(notifyKey);
            if (st === 'failed') {
              const detail = doc.extractionProgressLabel?.trim();
              this.error = detail
                ? `Extraction failed for "${row.name}": ${detail}`
                : `Extraction failed for "${row.name}"`;
            } else if (st === 'paused') {
              this.message = doc.extractionProgressLabel ?? `Extraction paused for "${row.name}"`;
            } else if (isRegulationExtractSuccess(st, pts)) {
              this.patchRegulationDoc(row, {
                pointCount: pts,
                extractionStatus: 'extracted',
                extractionProgressLabel: null,
                extractionProgressPct: null,
              });
              this.message = `Extraction complete — ${pts} points`;
              this.toast.show(this.message, 'success', 4000);
              if (this.selectedDoc && findRegulationDocumentIndex([this.selectedDoc], row.id) >= 0) {
                await this.loadPointsForDoc(row.id);
              }
            }
          }
        } else if (!isParsePoll && st === 'parsed' && this.parsingId === id) {
          this.parsingId = null;
        }
      }
      if (!this.pollingExtractIds.size && !this.pollingParseIds.size) {
        this.stopExtractPolling();
        await this.loadDocs(true);
      }
    } finally {
      this.pollInFlight = false;
    }
  }

  private syncExtractPollingFromDocs(): void {
    for (const doc of this.docs) {
      const st = (doc.extractionStatus ?? '').toLowerCase();
      // Never re-open polling for docs that already have extracted points.
      if (st !== 'processing' || this.hasExtractedPoints(doc)) continue;
      const lookupIds = regulationDocLookupIds(doc);
      if (
        lookupIds.some(
          (id) =>
            this.completionNotified.has(`extract:${id}`)
            || this.completionNotified.has(`parse:${id}`),
        )
      ) {
        continue;
      }
      const label = (doc.extractionProgressLabel ?? '').toLowerCase();
      // Demo parse steps say "Building markdown" etc. — keep those on the parse poll track.
      const looksLikeParse =
        label.includes('parsing')
        || label.includes('markdown')
        || label.includes('reading document')
        || label.includes('parse');
      if (looksLikeParse || this.pollingParseIds.has(doc.id)) {
        this.pollingExtractIds.delete(doc.id);
        this.pollingParseIds.add(doc.id);
      } else {
        this.pollingExtractIds.add(doc.id);
      }
    }
    if (this.pollingExtractIds.size || this.pollingParseIds.size) this.ensureExtractPolling();
  }

  get canUpload(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get canExtract(): boolean {
    return this.canUpload;
  }

  get canViewDeleted(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  showRowParseButton(doc: RegulationDocument): boolean {
    return this.canExtract && !this.isManualDoc(doc);
  }

  showRowExtractButton(doc: RegulationDocument): boolean {
    return this.canExtract && !this.isManualDoc(doc);
  }

  /**
   * View is always visible so the workflow reads Upload → Parse → Extract → View.
   * It is disabled with an explanatory reason until points exist.
   */
  showRowViewButton(): boolean {
    return true;
  }

  /** Null when the action is allowed, otherwise the reason shown in the tooltip. */
  viewDisabledReason(doc: RegulationDocument): string | null {
    if (this.isManualDoc(doc)) return null;
    if (this.hasExtractedPoints(doc)) return null;
    if (this.isExtractingDoc(doc)) return 'Extraction in progress — points will appear when it finishes.';
    if (this.isParsingDoc(doc)) return 'Parsing in progress. Extract points before viewing.';
    if (this.isParsedDoc(doc) || this.isPausedDoc(doc)) return 'Extract points first to view them.';
    return 'Parse the document first, then extract points to view them.';
  }

  extractDisabledReason(doc: RegulationDocument): string | null {
    if (this.isManualDoc(doc)) return 'Manual documents do not use extraction.';
    if (this.isExtractingDoc(doc)) return 'Extraction already running.';
    if (this.isParsingDoc(doc)) return 'Wait for parsing to finish.';
    if (this.canShowExtract(doc)) return null;
    return 'Parse the document first.';
  }

  parseDisabledReason(doc: RegulationDocument): string | null {
    if (this.isManualDoc(doc)) return 'Manual documents do not use parsing.';
    if (this.parsingId === doc.id || this.isParsingDoc(doc)) return 'Parsing already running.';
    if (this.isExtractingDoc(doc)) return 'Wait for extraction to finish before re-parsing.';
    return null;
  }

  parseButtonTitle(doc: RegulationDocument): string {
    return (
      this.parseDisabledReason(doc)
      ?? (this.isParsedDoc(doc) || this.hasExtractedPoints(doc)
        ? 'Re-parse PDF to markdown'
        : 'Parse PDF to markdown (required before extraction)')
    );
  }

  extractButtonTitle(doc: RegulationDocument): string {
    return (
      this.extractDisabledReason(doc)
      ?? (this.hasExtractedPoints(doc) ? 'Re-extract regulation points' : 'Extract regulation points')
    );
  }

  viewButtonTitle(doc: RegulationDocument): string {
    return (
      this.viewDisabledReason(doc)
      ?? (this.isManualDoc(doc) ? 'Manage points' : 'View extracted points')
    );
  }

  showRowJsonButton(doc: RegulationDocument): boolean {
    if (this.auth.isDemoViewer()) return false;
    return this.hasExtractedPoints(doc) || this.isManualDoc(doc);
  }

  /** Production makers/admins, or Demo Admin only — not regular demo makers. */
  showRowRepairButton(doc: RegulationDocument): boolean {
    if (!this.canExtract || this.isManualDoc(doc)) return false;
    if (!this.hasExtractedPoints(doc)) return false;
    if (this.auth.isDemoViewer() && !this.auth.isDemoAdmin()) return false;
    return true;
  }

  /**
   * Demo runs extraction from the document row only — the points panel stays a read-only
   * viewer so the demo walkthrough is Upload → Parse → Extract → View.
   */
  showPanelExtractButton(doc: RegulationDocument): boolean {
    if (this.auth.isDemoViewer()) return false;
    return this.canShowExtract(doc);
  }

  canClickRowExtract(doc: RegulationDocument): boolean {
    return this.canShowExtract(doc) && !this.isExtractingDoc(doc);
  }

  parseButtonLabel(doc: RegulationDocument): string {
    if (this.parsingId === doc.id || this.isParsingDoc(doc)) return 'Parsing…';
    if (this.isParsedDoc(doc) || this.hasExtractedPoints(doc)) return 'Re-parse';
    const st = (doc.extractionStatus ?? '').toLowerCase();
    if (st === 'failed') return 'Retry parse';
    return 'Parse';
  }

  extractButtonLabel(doc: RegulationDocument): string {
    if (this.extractingId === doc.id || this.isExtractingDoc(doc)) return 'Extracting…';
    if (this.isPausedDoc(doc)) return 'Resume';
    if (this.hasExtractedPoints(doc)) return 'Re-extract';
    if ((doc.extractionStatus ?? '').toLowerCase() === 'failed') return 'Retry extract';
    return 'Extract';
  }

  async loadDepartments(): Promise<void> {
    const res = await this.api.getDepartments();
    if (res.success && res.data) this.departments = res.data as Department[];
  }

  async loadDocs(silent = false, replace = false): Promise<void> {
    if (!silent) this.loading = true;
    const res = await this.api.getRegulationDocuments({
      departmentId: this.deptFilter || undefined,
      status: this.statusFilter || undefined,
      hiddenOnly: this.showDeleted,
    });
    if (res.success && res.data) {
      const incoming = res.data as RegulationDocument[];
      const merged = replace
        ? incoming
        : this.mergeRegulationList(this.docs, incoming);
      this.docs = sortRegulationDocuments(dedupeRegulationDocuments(merged));
      this.syncExtractPollingFromDocs();
    } else if (!silent || this.docs.length === 0) {
      this.error = res.message ?? 'Failed to load regulation documents';
    }
    this.loading = false;
  }

  onFiltersChange(): void {
    void this.loadDocs();
  }

  get visibleDocs(): RegulationDocument[] {
    let list = this.docs.filter((doc) => {
      if (!matchesSearch(this.searchQuery, [doc.name, doc.departmentName])) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'name':
          return compareText(a.name, b.name, this.sortDir);
        case 'department':
          return compareText(a.departmentName ?? '', b.departmentName ?? '', this.sortDir);
        case 'points':
          return compareNumber(a.pointCount ?? 0, b.pointCount ?? 0, this.sortDir);
        case 'status':
          return compareText(a.extractionStatus, b.extractionStatus, this.sortDir);
        case 'created':
        default:
          return compareDateIso(a.createdAt, b.createdAt, this.sortDir);
      }
    }).sort((a, b) => {
      const am = this.isManualDoc(a) ? 0 : 1;
      const bm = this.isManualDoc(b) ? 0 : 1;
      return am - bm;
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, '', this.deptFilter, this.statusFilter);
  }

  toggleSort(column: typeof this.sortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'created');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: typeof this.sortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.deptFilter = '';
    this.statusFilter = '';
    void this.loadDocs();
  }

  onFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.file = input.files?.[0] ?? null;
  }

  private upsertUploadedRegulationDoc(
    data: {
      id?: string;
      name?: string;
      departmentId?: string | null;
      extractionStatus?: string;
      pointCount?: number;
    },
    file: File,
  ): void {
    if (!data?.id) return;
    this.recentUploads.set(data.id, Date.now());
    const deptId = data.departmentId ?? (this.uploadDept || null);
    const deptName = deptId ? (this.departments.find((d) => d.id === deptId)?.name ?? null) : null;
    const now = new Date().toISOString();
    const optimistic: RegulationDocument = {
      id: data.id,
      source: 'nd',
      name: data.name ?? file.name,
      departmentId: deptId,
      departmentName: deptName,
      extractionStatus: data.extractionStatus ?? 'pending',
      pointCount: data.pointCount ?? 0,
      createdAt: now,
      updatedAt: now,
      originalFileName: file.name,
      isHidden: false,
    };
    this.docs = sortRegulationDocuments(
      dedupeRegulationDocuments([optimistic, ...this.docs.filter((d) => d.id !== optimistic.id)]),
    );
  }

  async handleUpload(): Promise<void> {
    if (!this.file) return;
    this.uploading = true;
    this.error = '';
    const file = this.file;
    const res = await this.api.uploadRegulationDocument(file, this.uploadDept || undefined);
    if (res.success) {
      const data = res.data as {
        id?: string;
        name?: string;
        departmentId?: string | null;
        extractionStatus?: string;
        pointCount?: number;
      };
      this.statusFilter = '';
      this.upsertUploadedRegulationDoc(data, file);
      this.file = null;
      const uploadStatus = (data.extractionStatus ?? 'pending').toLowerCase();
      if (data?.id && uploadStatus === 'pending') {
        this.message = 'Document uploaded — click Parse to start.';
      } else if (data?.id && uploadStatus === 'processing') {
        const label = (data as { extractionProgressLabel?: string }).extractionProgressLabel ?? '';
        if (label.toLowerCase().includes('parsing')) {
          this.trackParsingDoc(data.id);
        } else {
          this.trackExtractingDoc(data.id);
        }
        this.patchRegulationDoc(
          { id: data.id, name: data.name ?? file.name } as RegulationDocument,
          {
            extractionStatus: 'processing',
            extractionProgressLabel: (data as { extractionProgressLabel?: string }).extractionProgressLabel,
            extractionProgressPct: (data as { extractionProgressPct?: number }).extractionProgressPct,
          },
        );
        this.message = label.trim() || 'Processing document…';
      } else if (data?.id && (uploadStatus === 'extracted' || uploadStatus === 'completed')) {
        this.message = `Document uploaded — ${data.pointCount ?? 0} points extracted`;
      } else {
        this.message = 'Document uploaded';
      }
      this.toast.show(this.message, 'success', 4000);
      if (!this.showDeleted) {
        this.workspaceNav.bumpNavBadges({ regulationDocuments: 1 });
      }
      void this.loadDocs(true);
    } else {
      this.error = res.message ?? 'Upload failed';
    }
    this.uploading = false;
  }

  async handleDeptChange(doc: RegulationDocument, departmentId: string): Promise<void> {
    const idx = this.docs.findIndex((d) => d.id === doc.id);
    if (idx < 0) return;

    const prevDeptId = this.docs[idx].departmentId ?? '';
    const prevDeptName = this.docs[idx].departmentName ?? null;
    if (prevDeptId === departmentId) return;

    const deptName =
      departmentId ? (this.departments.find((d) => d.id === departmentId)?.name ?? null) : null;

    this.savingDeptId = doc.id;
    this.error = '';
    this.docs[idx] = {
      ...this.docs[idx],
      departmentId: departmentId || null,
      departmentName: deptName,
    };

    const res = await this.api.updateRegulationDocument(doc.id, {
      departmentId: departmentId || null,
    });
    if (!res.success) {
      this.docs[idx] = {
        ...this.docs[idx],
        departmentId: prevDeptId || null,
        departmentName: prevDeptName,
      };
      this.error = res.message ?? 'Failed to update department';
    } else if (this.selectedDoc?.id === doc.id) {
      this.selectedDoc = this.docs[idx];
    }
    this.savingDeptId = null;
  }

  hasExtractedPoints(doc: RegulationDocument): boolean {
    if (this.isManualDoc(doc)) return (doc.pointCount ?? 0) > 0;
    if ((doc.pointCount ?? 0) > 0) return true;
    // Side panel may have loaded points before list pointCount caught up.
    if (
      this.selectedDoc
      && findRegulationDocumentIndex([this.selectedDoc], doc.id) >= 0
      && this.selectedPoints.length > 0
    ) {
      return true;
    }
    return false;
  }

  async handleRepairPoints(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.repairingPointsId = doc.id;
    this.error = '';
    this.toast.show('Repairing regulation points… this may take a minute.', 'info', 5000);
    try {
      const res = await this.api.repairRegulationPoints(doc.id);
      if (res.success && res.data) {
        const r = res.data.repair;
        this.message =
          `Repaired points: ${r.beforeCount} → ${r.afterCount} active ` +
          `(${r.softDeleted} soft-deleted, ${r.pagesRefreshed} pages refreshed).`;
        if (this.selectedDoc?.id === doc.id || this.showPointsPanel) {
          await this.loadPointsForDoc(doc.id);
          await this.loadDocs(true);
        }
      } else {
        this.error = res.message ?? 'Could not repair points';
      }
    } finally {
      this.repairingPointsId = null;
    }
  }

  async handleRefreshPageReferences(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.refreshingPagesId = doc.id;
    this.error = '';
    this.toast.show('Refreshing PDF page numbers…', 'info', 5000);
    try {
      const res = await this.api.refreshRegulationPageReferences(doc.id);
      if (res.success) {
        const n = res.data?.pointsUpdated ?? 0;
        this.message = `Updated PDF page numbers for ${n} points (no Landing AI credits used).`;
        if (this.selectedDoc?.id === doc.id || this.showPointsPanel) {
          await this.loadPointsForDoc(doc.id);
        }
      } else {
        this.error = res.message ?? 'Could not refresh page numbers';
      }
    } finally {
      this.refreshingPagesId = null;
    }
  }

  async handleExtract(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.error = '';
    this.message = '';
    this.trackExtractingDoc(doc.id);
    this.patchRegulationDoc(doc, {
      extractionStatus: 'processing',
      extractionProgressLabel: 'Extracting regulation points…',
      extractionProgressPct: 10,
    });
    const res = await this.api.extractRegulationDocument(doc.id);
    if (res.success) {
      const data = res.data as {
        id?: string;
        regulationDocumentId?: string;
        storedDocumentId?: string;
        pointCount?: number;
        extractionStatus?: string;
        extractionProgressLabel?: string | null;
        extractionProgressPct?: number | null;
      };
      const regDocId = data?.regulationDocumentId ?? data?.id ?? doc.id;
      const processing = (data?.extractionStatus ?? '').toLowerCase() === 'processing';
      if (processing) {
        const idx = findRegulationDocumentIndex(this.docs, doc.id);
        if (idx >= 0) {
          this.docs[idx] = {
            ...this.docs[idx],
            id: regDocId,
            storedDocumentId: data?.storedDocumentId ?? this.docs[idx].storedDocumentId,
            extractionStatus: data?.extractionStatus ?? this.docs[idx].extractionStatus,
            extractionProgressLabel: data?.extractionProgressLabel ?? this.docs[idx].extractionProgressLabel,
            extractionProgressPct: data?.extractionProgressPct ?? this.docs[idx].extractionProgressPct,
          };
          if (this.selectedDoc && findRegulationDocumentIndex([this.selectedDoc], doc.id) >= 0) {
            this.selectedDoc = this.docs[idx];
          }
        }
        this.message =
          data?.extractionProgressLabel?.trim() || `Extracting "${doc.name}"…`;
        if (regDocId !== doc.id) {
          this.pollingExtractIds.delete(doc.id);
          this.pollingExtractIds.add(regDocId);
          if (this.extractingId === doc.id) this.extractingId = regDocId;
        }
        return;
      }
      this.clearExtractPolling(doc.id, regDocId, data?.storedDocumentId ?? '');
      this.stopExtractPolling();
      const responsePts = data?.pointCount ?? 0;
      if (responsePts > 0) {
        const notifyIds = regulationDocLookupIds({
          ...doc,
          id: regDocId,
          storedDocumentId: data?.storedDocumentId ?? doc.storedDocumentId,
        });
        const alreadyNotified = notifyIds.some((id) => this.completionNotified.has(`extract:${id}`));
        for (const id of notifyIds) this.completionNotified.add(`extract:${id}`);
        this.patchRegulationDoc(doc, {
          id: regDocId,
          storedDocumentId: data?.storedDocumentId ?? doc.storedDocumentId,
          pointCount: responsePts,
          extractionStatus: 'extracted',
          extractionProgressLabel: null,
          extractionProgressPct: null,
        });
        this.message = `Extraction complete — ${responsePts} points`;
        if (!alreadyNotified) this.toast.show(this.message, 'success', 4000);
        if (this.selectedDoc && findRegulationDocumentIndex([this.selectedDoc], doc.id) >= 0) {
          await this.loadPointsForDoc(regDocId);
        }
        void this.loadDocs(true);
        return;
      }
      await this.loadDocs(true);
      const refreshed =
        this.docs.find((d) => d.id === regDocId)
        ?? this.docs.find((d) => findRegulationDocumentIndex([d], doc.id) >= 0);
      const pts = refreshed?.pointCount ?? 0;
      if (pts <= 0) {
        this.error = `Extraction finished but no points were saved for "${doc.name}". Click Extract to retry.`;
        this.toast.show(this.error, 'error', 6000);
      } else {
        this.message = `Extraction complete — ${pts} points`;
        this.toast.show(this.message, 'success', 4000);
      }
      if (this.selectedDoc?.id === doc.id || this.showPointsPanel) {
        await this.loadPointsForDoc(doc.id);
        this.selectedDoc = this.docs.find((d) => d.id === doc.id) ?? this.selectedDoc;
      }
    } else {
      this.message = '';
      this.error = res.message ?? 'Extraction failed';
      this.toast.show(this.error, 'error', 6000);
      this.extractingId = null;
      this.pollingExtractIds.delete(doc.id);
      this.pollingParseIds.delete(doc.id);
      this.stopExtractPolling();
      this.patchRegulationDoc(doc, {
        extractionStatus: 'failed',
        extractionProgressLabel: res.message ?? 'Extraction failed',
        extractionProgressPct: null,
        pointCount: 0,
      });
      await this.loadDocs(true);
    }
  }

  viewExtractionProgress(doc: RegulationDocument, event?: Event): void {
    event?.stopPropagation();
    this.trackExtractingDoc(doc.id);
    void this.viewPoints(doc, event);
    this.message = doc.extractionProgressLabel?.trim() || `Extracting "${doc.name}"…`;
  }

  async handleStopExtract(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.error = '';
    const res = await this.api.stopRegulationExtract(doc.id);
    if (!res.success) {
      this.error = res.message ?? 'Could not stop extraction';
      return;
    }
    const data = res.data as RegulationDocument | undefined;
    const idx = this.docs.findIndex((d) => d.id === doc.id);
    if (idx >= 0 && data) {
      this.docs[idx] = { ...this.docs[idx], ...data, extractionStatus: 'paused' };
      if (this.selectedDoc?.id === doc.id) this.selectedDoc = this.docs[idx];
    }
    this.pollingExtractIds.delete(doc.id);
    this.extractingId = null;
    this.message =
      (data as RegulationDocument | undefined)?.extractionProgressLabel ??
      'Extraction stopped — click Extract to resume from saved progress.';
    await this.loadDocs(true);
  }

  async viewPoints(
    doc: RegulationDocument,
    event?: Event,
    highlightPoint?: string,
    options?: { keepPointSearch?: boolean },
  ): Promise<void> {
    event?.stopPropagation();
    if (!options?.keepPointSearch) this.clearGlobalPointSearch();
    this.selectedDoc = doc;
    this.showPointsPanel = true;
    this.highlightPointNumber = highlightPoint?.trim() ?? '';
    this.shellFocus.setRegulationPointsPanelOpen(true);
    await this.loadPointsForDoc(doc.id);
  }

  async onManualPointsChanged(): Promise<void> {
    if (!this.selectedDoc) return;
    await this.loadPointsForDoc(this.selectedDoc.id);
    await this.loadDocs(true);
    this.selectedDoc = this.docs.find((d) => d.id === this.selectedDoc?.id) ?? this.selectedDoc;
  }

  closePointsPanel(): void {
    this.showPointsPanel = false;
    this.highlightPointNumber = '';
    this.shellFocus.setRegulationPointsPanelOpen(false);
  }

  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    if (this.showPointsPanel) this.closePointsPanel();
  }

  onGlobalPointSearch(value: string): void {
    this.globalPointSearch = value;
    if (this.pointSearchTimer) clearTimeout(this.pointSearchTimer);
    const q = value.trim();
    if (q.length < 2) {
      this.pointSearchResults = [];
      this.pointSearchTotal = 0;
      this.pointSearchLoading = false;
      this.pointSearchError = '';
      return;
    }
    this.pointSearchError = '';
    this.pointSearchLoading = true;
    this.pointSearchTimer = setTimeout(() => void this.runGlobalPointSearch(q), 320);
  }

  private async runGlobalPointSearch(q: string): Promise<void> {
    const res = await this.api.searchRegulationPoints(q);
    if (this.globalPointSearch.trim() !== q) return;
    this.pointSearchLoading = false;
    if (res.success && res.data) {
      this.pointSearchResults = res.data as RegulationPointSearchGroup[];
      this.pointSearchTotal = res.totalMatches ?? this.countSearchMatches(this.pointSearchResults);
      this.pointSearchError = '';
    } else {
      this.pointSearchResults = [];
      this.pointSearchTotal = 0;
      this.pointSearchError = res.message ?? 'Point search failed';
    }
  }

  private countSearchMatches(groups: RegulationPointSearchGroup[]): number {
    return groups.reduce((sum, g) => sum + (g.points?.length ?? 0), 0);
  }

  async openPointFromSearch(group: RegulationPointSearchGroup, hit: RegulationPointSearchHit): Promise<void> {
    const doc = this.docFromSearchGroup(group);
    await this.viewPoints(doc, undefined, hit.pointNumber, { keepPointSearch: true });
  }

  async openSourceFromSearch(
    group: RegulationPointSearchGroup,
    hit: RegulationPointSearchHit,
    event?: Event,
  ): Promise<void> {
    event?.stopPropagation();
    const doc = this.docFromSearchGroup(group);
    if (this.isManualDoc(doc)) return;
    const page = resolveRegulationPdfPage(hit.pageReference, hit.pdfPage);
    const fileDocId = hit.storedDocumentId ?? group.storedDocumentId ?? doc.id;
    await this.openDocumentById(fileDocId, event, page);
  }

  canOpenSourceFromSearch(group: RegulationPointSearchGroup): boolean {
    return !group.isManual;
  }

  private docFromSearchGroup(group: RegulationPointSearchGroup): RegulationDocument {
    const existing = this.docs.find((d) => d.id === group.documentId);
    return (
      existing ??
      ({
        id: group.documentId,
        name: group.documentName,
        departmentName: group.departmentName ?? undefined,
        extractionStatus: group.isManual ? 'manual' : 'extracted',
        pointCount: group.points.length,
        createdAt: new Date().toISOString(),
        isManual: group.isManual,
        source: group.isManual ? 'manual' : 'nd',
      } as RegulationDocument)
    );
  }

  openSourceTooltip(group: RegulationPointSearchGroup, hit: RegulationPointSearchHit): string {
    const page = formatPointPageRef(hit.pageReference, hit.pdfPage);
    return [
      group.documentName,
      hit.pointNumber,
      hit.pointTitle,
      page ?? hit.pageReference,
    ]
      .filter((p) => p?.trim())
      .join(' · ');
  }

  searchHitTooltip(hit: RegulationPointSearchHit): string {
    return [hit.pointNumber, hit.pointTitle, hit.snippet].filter((p) => p?.trim()).join('\n');
  }

  clearGlobalPointSearch(): void {
    this.globalPointSearch = '';
    this.pointSearchResults = [];
    this.pointSearchTotal = 0;
    this.pointSearchLoading = false;
    this.pointSearchError = '';
  }

  private async loadPointsForDoc(docId: string): Promise<void> {
    this.pointsLoading = true;
    // Lite truncates long clause text — enough for the library panel and much faster to render.
    const res = await this.api.getDocumentPoints(docId, { lite: true });
    if (res.success && res.data) {
      const docName = this.selectedDoc?.name ?? this.docs.find((d) => d.id === docId)?.name;
      const prepared = prepareRegulationPointsResponse(res.data as unknown[], {
        docName,
        apiPointCount: res.pointCount,
      });
      this.selectedPoints = prepared.points;
      this.pointsSource = res.source ?? '';
      // Always use API canonical pointCount so list header matches the points endpoint.
      const stored = res.pointCount != null && res.pointCount > 0
        ? res.pointCount
        : prepared.storedCount;
      if (stored > 0) {
        this.patchRegulationDoc(
          this.selectedDoc ?? ({ id: docId } as RegulationDocument),
          {
            pointCount: stored,
            extractionStatus: 'extracted',
            extractionProgressLabel: null,
            extractionProgressPct: null,
          },
        );
      }
    } else {
      this.selectedPoints = [];
      this.pointsSource = '';
    }
    this.pointsLoading = false;
  }

  /** List-row canonical count — never inflated by viewing points in the side panel. */
  docListPointCount(doc: RegulationDocument | null | undefined): number {
    if (!doc) return 0;
    const idx = findRegulationDocumentIndex(this.docs, doc.id);
    if (idx >= 0) return this.docs[idx].pointCount ?? 0;
    return doc.pointCount ?? 0;
  }

  async handleHide(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (this.isManualDoc(doc)) return;
    const confirmed = confirm(
      `Delete "${doc.name}" from the library?\n\nNothing is deleted from the database — extraction credits and points are kept. A super admin can restore it from the Deleted tab.`,
    );
    if (!confirmed) return;

    this.hidingId = doc.id;
    this.error = '';
    this.message = '';
    const res = await this.api.hideRegulationDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Regulation removed from library';
      if (this.selectedDoc?.id === doc.id) this.closePointsPanel();
      this.removeRegulationDocFromList(doc);
      if (!this.showDeleted) {
        this.workspaceNav.bumpNavBadges({ regulationDocuments: -1 });
      }
      await this.loadDocs(true, true);
    } else {
      this.error = res.message ?? 'Failed to delete regulation';
    }
    this.hidingId = null;
  }

  async handleRestore(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.hidingId = doc.id;
    this.error = '';
    this.message = '';
    const res = await this.api.restoreRegulationDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Regulation restored';
      if (this.selectedDoc?.id === doc.id) this.closePointsPanel();
      if (this.showDeleted) {
        this.workspaceNav.bumpNavBadges({
          regulationDocuments: 1,
          regulationDocumentsDeleted: -1,
        });
      }
      await this.loadDocs(true);
    } else {
      this.error = res.message ?? 'Failed to restore regulation';
    }
    this.hidingId = null;
  }

  actorLabel(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    return trimmed || '—';
  }

  async openDocument(doc: RegulationDocument, event?: Event, page?: number | null): Promise<void> {
    event?.stopPropagation();
    if (this.isManualDoc(doc)) return;
    const fileDocId = doc.storedDocumentId ?? doc.id;
    await this.openDocumentById(fileDocId, event, page);
  }

  async openDocumentById(docId: string, event?: Event, page?: number | null): Promise<void> {
    event?.stopPropagation();
    this.error = '';
    const ok = await this.api.openRegulationDocumentPdf(docId, page ?? null);
    if (!ok) this.error = 'Could not open document PDF';
  }

  async openRegulationSourcePage(doc: RegulationDocument, page: number): Promise<void> {
    const fileDocId = doc.storedDocumentId ?? doc.id;
    await this.openDocumentById(fileDocId, undefined, page);
  }

  async exportPointsJson(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (this.exportingPointsId) return;
    this.exportingPointsId = doc.id;
    this.error = '';
    this.message = '';
    this.toast.show('Preparing JSON download…', 'info', 4000);
    await Promise.resolve();
    const res = await this.api.downloadRegulationPointsExport(doc.id);
    if (!res.success) {
      this.error = res.message ?? 'Failed to download points';
      this.toast.show(res.message ?? 'Download failed', 'error', 5000);
    } else {
      this.message = `Downloaded points JSON for "${doc.name}"`;
    }
    this.exportingPointsId = null;
  }

  docPageMeta(doc: RegulationDocument): string {
    if (!this.shouldShowDocPages(doc)) return '—';
    const pages = doc.pageCount ?? 0;
    return pages > 0 ? `${pages}` : '—';
  }

  /** PDF page total — only after parse completes (not during upload/parsing). */
  private shouldShowDocPages(doc: RegulationDocument): boolean {
    if (this.isManualDoc(doc)) return true;
    if (this.isParsingDoc(doc)) return false;
    const st = (doc.extractionStatus ?? '').toLowerCase();
    if (st === 'pending' || st === 'processing') return false;
    return (
      st === 'parsed' ||
      st === 'extracted' ||
      st === 'paused' ||
      st === 'completed' ||
      this.isParsedDoc(doc) ||
      this.hasExtractedPoints(doc)
    );
  }

  docPointMeta(doc: RegulationDocument): string {
    if (this.isManualDoc(doc)) {
      return `${this.docListPointCount(doc)} pts`;
    }
    if (!this.hasExtractedPoints(doc)) {
      return '—';
    }
    const n = this.docListPointCount(doc);
    const part = this.extractionPartLabel(doc);
    if (this.isExtractingDoc(doc)) {
      const saving = this.extractionSavingPointsLabel(doc);
      if (saving) return `${n} pts · ${saving}`;
      if (part) return `${n} pts · ${part}`;
      const pct = this.extractionProgressPct(doc);
      if (pct != null) return `${n} pts · ${pct}%`;
    }
    if ((doc.extractionStatus ?? '').toLowerCase() === 'paused' && part) {
      return `${n} pts · saved ${part}`;
    }
    return `${n} pts`;
  }

  private extractionPartLabel(doc: RegulationDocument): string | null {
    const label = doc.extractionProgressLabel ?? '';
    const m = /part\s+(\d+)\s*\/\s*(\d+)/i.exec(label);
    if (m) return `part ${m[1]}/${m[2]}`;
    if (doc.extractionParseChunkCompleted != null && doc.extractionParseChunkCompleted >= 0) {
      return `part ${doc.extractionParseChunkCompleted + 1} saved`;
    }
    return null;
  }

  private extractionSavingPointsLabel(doc: RegulationDocument): string | null {
    const label = doc.extractionProgressLabel ?? '';
    const m = /Saving\s+(\d+)\s+regulation points/i.exec(label);
    return m ? `${m[1]} pts found` : null;
  }

  /** Top banner while extract is running — demo skips stale "Parsing…" labels from older API responses. */
  private extractProgressMessage(doc: RegulationDocument): string {
    const label = (doc.extractionProgressLabel ?? '').trim();
    if (!this.auth.isDemoViewer()) return label || `Extracting "${doc.name}"…`;
    if (label && !label.toLowerCase().includes('parsing')) return label;
    return `Extracting "${doc.name}"…`;
  }

  formatDate = formatDate;
  formatTableDate = formatTableDate;

  extractionClass(status: string): string {
    if (status === 'extracted' || status === 'manual' || status === 'completed') return 'completed';
    if (status === 'parsed') return 'pending';
    if (status === 'processing') return 'running';
    if (status === 'paused') return 'pending';
    if (status === 'failed') return 'failed';
    return 'pending';
  }

  extractionClassForDoc(doc: RegulationDocument): string {
    if (this.isManualDoc(doc)) return 'completed';
    if (this.hasExtractedPoints(doc)) return 'completed';
    if (this.isParsingDoc(doc) || this.isExtractingDoc(doc)) return 'running';
    if (this.isPausedDoc(doc)) return 'pending';
    const st = (doc.extractionStatus ?? '').toLowerCase();
    if (st === 'failed') return 'failed';
    return 'pending';
  }

  extractionLabel(status: string): string {
    if (status === 'manual') return 'Manual';
    if (status === 'parsed') return 'Pending extract';
    if (status === 'extracted' || status === 'completed') return 'Extracted';
    if (status === 'processing') return 'Extracting…';
    if (status === 'paused') return 'Paused';
    if (status === 'failed') return 'Failed';
    if (status === 'pending') return 'Pending parse';
    return status;
  }

  /** Status column label — Pending parse · Pending extract · Extracted (or active Parsing/Extracting). */
  workflowStatusLabel(doc: RegulationDocument): string {
    if (this.isManualDoc(doc)) return this.extractionLabel(doc.extractionStatus);
    if (this.isParsingDoc(doc)) return 'Parsing…';
    if (this.isExtractingDoc(doc)) {
      const label = (doc.extractionProgressLabel ?? '').toLowerCase();
      if (label.includes('parsing')) return 'Parsing…';
      return 'Extracting…';
    }
    if (this.isPausedDoc(doc)) return 'Paused';
    if (this.hasExtractedPoints(doc)) return 'Extracted';
    const st = (doc.extractionStatus ?? '').toLowerCase();
    if (st === 'parsed' || st === 'completed') return 'Pending extract';
    if (st === 'failed') return 'Failed';
    return 'Pending parse';
  }

  /**
   * A just-uploaded row is not returned by the list API right away, and it has no points
   * and no extraction running — without this it would be merged away and only reappear on
   * a later refresh, which reads as the upload silently failing.
   */
  private isRecentUpload(docId: string): boolean {
    const at = this.recentUploads.get(docId);
    if (at == null) return false;
    if (Date.now() - at <= RecentUploadKeepMs) return true;
    this.recentUploads.delete(docId);
    return false;
  }

  private mergeRegulationList(
    prev: RegulationDocument[],
    incoming: RegulationDocument[],
  ): RegulationDocument[] {
    const prevByKey = new Map<string, RegulationDocument>();
    for (const d of prev) {
      prevByKey.set(d.id, d);
      if (d.storedDocumentId) prevByKey.set(d.storedDocumentId, d);
    }

    const usedPrevIds = new Set<string>();
    const merged = incoming.map((row) => {
      const old =
        prevByKey.get(row.id)
        ?? (row.storedDocumentId ? prevByKey.get(row.storedDocumentId) : undefined);
      if (!old) return row;
      usedPrevIds.add(old.id);
      const combined = this.mergeRegulationRow(old, row);
      // List may return legacy id (stored doc) while extract used regulation doc id — keep nd id.
      const preferOldId =
        old.source === 'nd'
        || ((old.pointCount ?? 0) > (row.pointCount ?? 0) && (old.pointCount ?? 0) > 0);
      return preferOldId
        ? { ...combined, id: old.id, source: old.source ?? combined.source }
        : combined;
    });

    for (const d of prev) {
      if (usedPrevIds.has(d.id)) continue;
      const shadowed = merged.some(
        (m) => m.id === d.id || (!!d.storedDocumentId && m.storedDocumentId === d.storedDocumentId),
      );
      if (
        !shadowed
        && (this.isDocExtractionInFlight(d.id) || this.isRecentUpload(d.id))
      ) {
        merged.push(d);
      }
    }

    return merged;
  }

  private removeRegulationDocFromList(doc: RegulationDocument): void {
    const ids = new Set(
      [doc.id, doc.storedDocumentId].filter(Boolean) as string[],
    );
    this.docs = this.docs.filter(
      (row) => !ids.has(row.id) && (!row.storedDocumentId || !ids.has(row.storedDocumentId)),
    );
    this.pollingExtractIds.delete(doc.id);
    this.pollingParseIds.delete(doc.id);
    this.pollStuckSince.delete(doc.id);
    if (this.parsingId === doc.id) this.parsingId = null;
    if (this.extractingId === doc.id) this.extractingId = null;
  }

  private mergeRegulationRow(
    old: RegulationDocument,
    incoming: RegulationDocument,
  ): RegulationDocument {
    const oldPts = old.pointCount ?? 0;
    const incomingPts = incoming.pointCount;
    const pointCount =
      incomingPts == null
        ? oldPts
        : incomingPts > 0
          ? incomingPts
          : oldPts > 0
            ? oldPts
            : incomingPts;
    if (this.isDocExtractionInFlight(old.id)) {
      const oldSt = (old.extractionStatus ?? '').toLowerCase();
      if (oldSt === 'processing') {
        return {
          ...old,
          ...incoming,
          pointCount,
          extractionStatus: old.extractionStatus,
          extractionProgressLabel: old.extractionProgressLabel ?? incoming.extractionProgressLabel,
          extractionProgressPct: old.extractionProgressPct ?? incoming.extractionProgressPct,
        };
      }
    }

    const oldSt = (old.extractionStatus ?? '').toLowerCase();
    const newSt = (incoming.extractionStatus ?? '').toLowerCase();
    // Never let a stale list refresh turn a successful extract into "Pending extract".
    if (
      oldPts > 0
      && (oldSt === 'extracted' || oldSt === 'completed')
      && (incomingPts == null || incomingPts <= 0)
      && (newSt === 'parsed' || newSt === 'pending' || newSt === 'completed' || newSt === '')
    ) {
      return {
        ...old,
        ...incoming,
        pointCount: oldPts,
        extractionStatus: 'extracted',
        extractionProgressLabel: null,
        extractionProgressPct: null,
      };
    }

    // Never demote Extracted → Extracting from a stale poll/list refresh.
    if (
      oldPts > 0
      && (oldSt === 'extracted' || oldSt === 'completed')
      && newSt === 'processing'
      && !this.isDocExtractionInFlight(old.id)
      && !this.isDocExtractionInFlight(incoming.id)
    ) {
      return {
        ...old,
        ...incoming,
        pointCount: Math.max(oldPts, incomingPts ?? 0),
        extractionStatus: 'extracted',
        extractionProgressLabel: null,
        extractionProgressPct: null,
      };
    }

    const oldRank = this.regulationStatusRank({ ...old, pointCount: old.pointCount ?? 0 });
    const newRank = this.regulationStatusRank({ ...incoming, pointCount });
    // pointCount: 0 is a real value — do not treat it as "prefer incoming status".
    // Extracted+points must beat processing so list refresh cannot re-open the spinner.
    const preferIncoming =
      (incomingPts != null && incomingPts > 0 && incomingPts >= oldPts && newSt !== 'processing')
      || (newRank > oldRank && !(oldPts > 0 && (oldSt === 'extracted' || oldSt === 'completed') && newSt === 'processing'));
    const extractionStatus = preferIncoming ? incoming.extractionStatus : old.extractionStatus;
    const extractionProgressLabel = preferIncoming
      ? incoming.extractionProgressLabel
      : old.extractionProgressLabel;
    const extractionProgressPct = preferIncoming
      ? incoming.extractionProgressPct
      : old.extractionProgressPct;
    return {
      ...old,
      ...incoming,
      pointCount,
      extractionStatus,
      extractionProgressLabel,
      extractionProgressPct,
    };
  }

  private isDocExtractionInFlight(docId: string): boolean {
    return (
      this.pollingExtractIds.has(docId)
      || this.pollingParseIds.has(docId)
      || this.extractingId === docId
      || this.parsingId === docId
    );
  }

  /** Keep local processing UI when status polls race ahead of the extract POST. */
  private preserveInFlightExtractionState(
    docId: string,
    prev: RegulationDocument,
    merged: RegulationDocument,
    isParsePoll: boolean,
  ): RegulationDocument {
    if (!this.isDocExtractionInFlight(docId)) return merged;
    const prevSt = (prev.extractionStatus ?? '').toLowerCase();
    const mergedSt = (merged.extractionStatus ?? '').toLowerCase();
    if (prevSt !== 'processing') return merged;
    if (isParsePoll) {
      if (mergedSt === 'parsed' || mergedSt === 'processing') return merged;
      return {
        ...merged,
        extractionStatus: prev.extractionStatus,
        extractionProgressLabel: prev.extractionProgressLabel ?? merged.extractionProgressLabel,
        extractionProgressPct: prev.extractionProgressPct ?? merged.extractionProgressPct,
      };
    }
    if (mergedSt === 'processing') return merged;
    if (mergedSt === 'extracted' || mergedSt === 'completed' || mergedSt === 'failed' || mergedSt === 'paused') {
      return merged;
    }
    return {
      ...merged,
      extractionStatus: prev.extractionStatus,
      extractionProgressLabel: prev.extractionProgressLabel ?? merged.extractionProgressLabel,
      extractionProgressPct: prev.extractionProgressPct ?? merged.extractionProgressPct,
    };
  }

  private regulationStatusRank(doc: RegulationDocument): number {
    const st = (doc.extractionStatus ?? '').toLowerCase();
    if (st === 'processing') return 6;
    if ((doc.pointCount ?? 0) > 0) return 5;
    if (st === 'extracted' || st === 'completed') return 4;
    if (st === 'parsed') return 3;
    if (st === 'paused') return 2;
    if (st === 'failed') return 1;
    return 0;
  }

  private patchRegulationDoc(
    doc: RegulationDocument,
    patch: Partial<RegulationDocument>,
  ): void {
    const ids = new Set(
      [...regulationDocLookupIds(doc), patch.id, patch.storedDocumentId].filter(Boolean) as string[],
    );
    this.docs = this.docs.map((row) =>
      ids.has(row.id) || (row.storedDocumentId && ids.has(row.storedDocumentId))
        ? { ...row, ...patch }
        : row,
    );
    if (this.selectedDoc && (ids.has(this.selectedDoc.id) || (this.selectedDoc.storedDocumentId && ids.has(this.selectedDoc.storedDocumentId)))) {
      const updated = this.docs.find(
        (d) => d.id === this.selectedDoc!.id || d.storedDocumentId === this.selectedDoc!.storedDocumentId,
      );
      if (updated) this.selectedDoc = updated;
    }
  }

  isPausedDoc(doc: RegulationDocument): boolean {
    return (doc.extractionStatus ?? '').toLowerCase() === 'paused';
  }

  isParsedDoc(doc: RegulationDocument): boolean {
    return (doc.extractionStatus ?? '').toLowerCase() === 'parsed';
  }

  needsParse(doc: RegulationDocument): boolean {
    if (this.isManualDoc(doc)) return false;
    const st = (doc.extractionStatus ?? '').toLowerCase();
    return (doc.pointCount ?? 0) === 0 && !this.isParsedDoc(doc) && (st === 'pending' || st === 'failed');
  }

  canShowExtract(doc: RegulationDocument): boolean {
    if (this.isManualDoc(doc)) return false;
    const st = (doc.extractionStatus ?? '').toLowerCase();
    // Demo extract auto-parses when needed; show Extract from pending/failed too.
    if (this.auth.isDemoViewer()) {
      return (
        this.isParsedDoc(doc)
        || this.isPausedDoc(doc)
        || this.hasExtractedPoints(doc)
        || st === 'failed'
        || st === 'pending'
      );
    }
    return (
      this.isParsedDoc(doc)
      || this.isPausedDoc(doc)
      || this.hasExtractedPoints(doc)
      || st === 'failed'
    );
  }

  isParsingDoc(doc: RegulationDocument): boolean {
    return (
      this.parsingId === doc.id ||
      this.pollingParseIds.has(doc.id) ||
      (doc.extractionStatus === 'processing' &&
        (doc.extractionProgressLabel ?? '').toLowerCase().includes('parsing') &&
        !this.pollingExtractIds.has(doc.id))
    );
  }

  isExtractingDoc(doc: RegulationDocument): boolean {
    if (this.isParsingDoc(doc)) return false;
    const st = (doc.extractionStatus ?? '').toLowerCase();
    return (
      this.extractingId === doc.id ||
      this.pollingExtractIds.has(doc.id) ||
      (st === 'processing' && !this.pollingParseIds.has(doc.id))
    );
  }

  async handleParse(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.parsingId = doc.id;
    this.error = '';
    this.message = '';
    this.trackParsingDoc(doc.id);
    this.patchRegulationDoc(doc, {
      extractionStatus: 'processing',
      extractionProgressLabel: 'Parsing document…',
      extractionProgressPct: 8,
    });
    const res = await this.api.parseRegulationDocument(doc.id);
    if (res.success) {
      const data = res.data as {
        id?: string;
        regulationDocumentId?: string;
        storedDocumentId?: string;
        extractionStatus?: string;
        pointCount?: number;
        extractionProgressLabel?: string | null;
        extractionProgressPct?: number | null;
      };
      if ((data?.extractionStatus ?? '').toLowerCase() === 'processing') {
        this.patchRegulationDoc(doc, {
          extractionStatus: 'processing',
          extractionProgressLabel: data.extractionProgressLabel,
          extractionProgressPct: data.extractionProgressPct,
        });
        this.message = data.extractionProgressLabel?.trim() || `Parsing "${doc.name}"…`;
        return;
      }
      this.parsingId = null;
      this.pollingParseIds.delete(doc.id);
      const parseId = data.regulationDocumentId ?? data.id ?? doc.id;
      const notifyKey = `parse:${parseId}`;
      const notifyKeyAlt = `parse:${doc.id}`;
      const alreadyNotified =
        this.completionNotified.has(notifyKey) || this.completionNotified.has(notifyKeyAlt);
      this.completionNotified.add(notifyKey);
      this.completionNotified.add(notifyKeyAlt);
      this.message = `Parse complete — "${doc.name}"`;
      this.patchRegulationDoc(doc, {
        id: parseId,
        storedDocumentId: data.storedDocumentId ?? doc.storedDocumentId,
        extractionStatus: data.extractionStatus ?? 'parsed',
        pointCount: data.pointCount ?? doc.pointCount,
        pageCount: (data as { pageCount?: number }).pageCount ?? doc.pageCount,
        extractionProgressLabel: null,
        extractionProgressPct: null,
      });
      if (!alreadyNotified) this.toast.show(this.message, 'success', 4000);
      await this.loadDocs(true);
    } else {
      this.parsingId = null;
      this.pollingParseIds.delete(doc.id);
      this.error = res.message ?? 'Parse failed';
      this.toast.show(this.error, 'error', 6000);
      this.patchRegulationDoc(doc, {
        extractionStatus: 'failed',
        extractionProgressLabel: this.error,
        extractionProgressPct: null,
      });
      await this.loadDocs(true);
    }
  }

  canResumeExtract(doc: RegulationDocument): boolean {
    return this.isPausedDoc(doc);
  }

  showExtractProgressActions(doc: RegulationDocument): boolean {
    return this.isExtractingDoc(doc) || this.isPausedDoc(doc);
  }

  extractionStatusText(doc: RegulationDocument): string {
    if (this.isExtractingDoc(doc) || this.isPausedDoc(doc)) {
      return doc.extractionProgressLabel?.trim() || this.extractionLabel(doc.extractionStatus);
    }
    return this.extractionLabel(doc.extractionStatus);
  }

  extractionProgressPct(doc: RegulationDocument): number | null {
    if (!this.isExtractingDoc(doc) && !this.isParsingDoc(doc)) return null;
    const pct = doc.extractionProgressPct;
    if (pct == null || Number.isNaN(pct)) return null;
    return Math.max(0, Math.min(100, pct));
  }

  isSelected(doc: RegulationDocument): boolean {
    return this.showPointsPanel && this.selectedDoc?.id === doc.id;
  }

  isManualDoc(doc: RegulationDocument): boolean {
    return doc.isManual === true || doc.source === 'manual';
  }
}
