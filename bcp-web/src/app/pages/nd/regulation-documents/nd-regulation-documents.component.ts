import { Component, HostListener, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import {
  dedupeRegulationDocuments,
  sortRegulationDocuments,
} from '../../../../lib/regulation-catalog-utils';
import { formatDate } from '../../../../lib/nd/utils';
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
import { NdShellFocusService } from '../../../services/nd/nd-shell-focus.service';
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

@Component({
  selector: 'app-nd-regulation-documents',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdRegulationPointsPanelComponent, NdManualRegulationPointsPanelComponent],
  templateUrl: './nd-regulation-documents.component.html',
  styleUrls: ['./nd-regulation-documents.component.scss', '../nd-shared.scss'],
})
export class NdRegulationDocumentsComponent implements OnInit, OnDestroy {
  private static readonly PANEL_SPLIT_KEY = 'nd-reg-panel-split-left';

  private readonly api = inject(NdApiService);
  private readonly shellFocus = inject(NdShellFocusService);
  private readonly route = inject(ActivatedRoute);
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
  hidingId: string | null = null;
  showDeleted = false;
  savingDeptId: string | null = null;
  error = '';
  message = '';

  selectedDoc: RegulationDocument | null = null;
  selectedPoints: RegulationPoint[] = [];
  pointsSource = '';
  pointsLoading = false;
  showPointsPanel = false;
  /** Left (table) share when points panel is open — kept small by default. */
  leftPanelPct = 28;
  highlightPointNumber = '';
  globalPointSearch = '';
  pointSearchLoading = false;
  pointSearchResults: RegulationPointSearchGroup[] = [];
  pointSearchTotal = 0;
  pointSearchError = '';
  private pointSearchTimer: ReturnType<typeof setTimeout> | null = null;
  private extractPollTimer: ReturnType<typeof setInterval> | null = null;
  private readonly pollingExtractIds = new Set<string>();

  async ngOnInit(): Promise<void> {
    this.restorePanelSplit();
    await this.auth.refreshProfile();
    await this.loadDepartments();
    this.route.queryParamMap.subscribe((params) => {
      const wasDeleted = this.showDeleted;
      this.showDeleted = params.get('deleted') === '1';
      if (wasDeleted && !this.showDeleted) this.closePointsPanel();
      void this.loadDocs();
    });
  }

  get panelGridColumns(): string | null {
    if (!this.showPointsPanel) return null;
    const left = this.leftPanelPct;
    const right = 100 - left;
    return `minmax(10rem, ${left}%) 10px minmax(16rem, ${right}%)`;
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
      { 'setup-split': { min: 18, max: 55 } },
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
      if (Number.isFinite(pct) && pct >= 18 && pct <= 55) {
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

  private ensureExtractPolling(): void {
    if (this.extractPollTimer || !this.pollingExtractIds.size) return;
    this.extractPollTimer = setInterval(() => void this.pollExtractingDocs(), 2500);
  }

  private trackExtractingDoc(docId: string): void {
    this.pollingExtractIds.add(docId);
    this.extractingId = docId;
    this.ensureExtractPolling();
  }

  private async pollExtractingDocs(): Promise<void> {
    if (!this.pollingExtractIds.size) {
      this.stopExtractPolling();
      return;
    }
    for (const id of [...this.pollingExtractIds]) {
      const res = await this.api.getRegulationDocument(id);
      if (!res.success || !res.data) continue;
      const doc = res.data as RegulationDocument;
      const idx = this.docs.findIndex((d) => d.id === id);
      if (idx >= 0) {
        this.docs[idx] = { ...this.docs[idx], ...doc };
        if (this.selectedDoc?.id === id) this.selectedDoc = this.docs[idx];
      }
      const st = (doc.extractionStatus ?? '').toLowerCase();
      if (st === 'extracted' || st === 'completed' || st === 'failed' || (doc.pointCount ?? 0) > 0) {
        this.pollingExtractIds.delete(id);
        if (this.extractingId === id) this.extractingId = null;
        if (st === 'failed') this.error = `Extraction failed for "${doc.name}"`;
        else if (this.selectedDoc?.id === id) await this.loadPointsForDoc(id);
      }
    }
    if (!this.pollingExtractIds.size) {
      this.stopExtractPolling();
      await this.loadDocs(true);
    }
  }

  private syncExtractPollingFromDocs(): void {
    for (const doc of this.docs) {
      if ((doc.extractionStatus ?? '').toLowerCase() === 'processing') {
        this.pollingExtractIds.add(doc.id);
      }
    }
    if (this.pollingExtractIds.size) this.ensureExtractPolling();
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

  async loadDepartments(): Promise<void> {
    const res = await this.api.getDepartments();
    if (res.success && res.data) this.departments = res.data as Department[];
  }

  async loadDocs(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    const res = await this.api.getRegulationDocuments({
      departmentId: this.deptFilter || undefined,
      status: this.statusFilter || undefined,
      hiddenOnly: this.showDeleted,
    });
    if (res.success && res.data) {
      const all = res.data as RegulationDocument[];
      this.docs = sortRegulationDocuments(dedupeRegulationDocuments(all));
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

  async handleUpload(): Promise<void> {
    if (!this.file) return;
    this.uploading = true;
    this.error = '';
    const res = await this.api.uploadRegulationDocument(this.file, this.uploadDept || undefined);
    if (res.success) {
      const data = res.data as { id?: string; extractionStatus?: string; pointCount?: number };
      this.message = 'Document uploaded';
      this.file = null;
      await this.loadDocs(true);
      if (data?.id && (data.extractionStatus === 'processing' || data.extractionStatus === 'pending')) {
        if (data.extractionStatus === 'processing') {
          this.trackExtractingDoc(data.id);
          this.message = 'Document uploaded — extraction in progress…';
        } else {
          this.message = 'Document uploaded — click Run extraction to extract regulation points.';
        }
      } else if (data?.id) {
        this.message = `Document uploaded — ${data.pointCount ?? 0} points extracted`;
      }
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

  async handleExtract(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.trackExtractingDoc(doc.id);
    this.error = '';
    this.message = '';
    const res = await this.api.extractRegulationDocument(doc.id);
    if (res.success) {
      const data = res.data as { pointCount?: number; extractionStatus?: string };
      if ((data?.extractionStatus ?? '').toLowerCase() === 'processing') {
        this.message = `Extracting "${doc.name}"…`;
        return;
      }
      this.extractingId = null;
      this.pollingExtractIds.delete(doc.id);
      this.message = `Extraction complete — ${data?.pointCount ?? 0} points`;
      const idx = this.docs.findIndex((d) => d.id === doc.id);
      if (idx >= 0) {
        this.docs[idx] = {
          ...this.docs[idx],
          pointCount: data?.pointCount ?? this.docs[idx].pointCount,
          extractionStatus: data?.extractionStatus === 'completed' ? 'extracted' : (data?.extractionStatus ?? 'extracted'),
        };
      }
      if (this.selectedDoc?.id === doc.id || this.showPointsPanel) {
        await this.loadPointsForDoc(doc.id);
        this.selectedDoc = this.docs.find((d) => d.id === doc.id) ?? this.selectedDoc;
      }
      await this.loadDocs(true);
    } else {
      this.error = res.message ?? 'Extraction failed';
    }
    this.extractingId = null;
    this.pollingExtractIds.delete(doc.id);
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
    const res = await this.api.getDocumentPoints(docId);
    if (res.success && res.data) {
      this.selectedPoints = res.data as RegulationPoint[];
      this.pointsSource = (res as { source?: string }).source ?? '';
      const idx = this.docs.findIndex((d) => d.id === docId);
      if (idx >= 0) {
        this.docs[idx] = { ...this.docs[idx], pointCount: this.selectedPoints.length };
        if (this.selectedDoc?.id === docId) {
          this.selectedDoc = this.docs[idx];
        }
      }
    } else {
      this.selectedPoints = [];
      this.pointsSource = '';
    }
    this.pointsLoading = false;
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
      await this.loadDocs(true);
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
    const res = await this.api.getRegulationDocumentFileUrl(docId);
    if (res.success && res.data?.url) {
      const pdfPage = page != null && page > 0 ? page : null;
      const url = pdfPage ? `${res.data.url}#page=${pdfPage}` : res.data.url;
      window.open(url, '_blank', 'noopener');
      return;
    }
    this.error = res.message ?? 'Could not open regulation PDF';
  }

  docPointMeta(doc: RegulationDocument): string {
    return `${doc.pointCount ?? 0} pts`;
  }

  formatDate = formatDate;

  extractionClass(status: string): string {
    if (status === 'extracted' || status === 'manual' || status === 'completed') return 'completed';
    if (status === 'processing') return 'running';
    if (status === 'failed') return 'failed';
    return 'pending';
  }

  extractionLabel(status: string): string {
    if (status === 'manual') return 'Manual';
    if (status === 'extracted' || status === 'completed') return 'Extracted';
    if (status === 'processing') return 'Extracting…';
    if (status === 'failed') return 'Failed';
    if (status === 'pending') return 'Pending';
    return status;
  }

  isExtractingDoc(doc: RegulationDocument): boolean {
    return this.extractingId === doc.id || this.pollingExtractIds.has(doc.id);
  }

  isSelected(doc: RegulationDocument): boolean {
    return this.showPointsPanel && this.selectedDoc?.id === doc.id;
  }

  isManualDoc(doc: RegulationDocument): boolean {
    return doc.isManual === true || doc.source === 'manual';
  }
}
