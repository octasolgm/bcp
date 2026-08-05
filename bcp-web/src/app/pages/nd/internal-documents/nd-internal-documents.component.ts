import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { isActiveDocumentRun } from '../../../services/active-analysis-sessions.service';
import { ToastService } from '../../../services/toast.service';
import { formatBytes, formatDate } from '../../../../lib/nd/utils';
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

type DocSortColumn = 'title' | 'uploaded' | 'size' | 'source';

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

@Component({
  selector: 'app-nd-internal-documents',
  standalone: true,
  imports: [CommonModule, FormsModule, NdInternalDocumentSectionsPanelComponent],
  templateUrl: './nd-internal-documents.component.html',
  styleUrls: ['./nd-internal-documents.component.scss', '../nd-shared.scss'],
})
export class NdInternalDocumentsComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);
  readonly auth = inject(NdAuthService);

  docs: InternalDocument[] = [];
  file: File | null = null;
  loading = true;
  uploading = false;
  parsingId: string | null = null;
  extractingSectionsId: string | null = null;
  repairingSectionPagesId: string | null = null;
  deletingId: string | null = null;
  showDeleted = false;
  error = '';
  message = '';
  historyFor: InternalDocument | null = null;
  sectionsFor: InternalDocument | null = null;
  sectionRows: InternalDocumentSection[] = [];
  loadingSections = false;
  analysisFor: InternalDocument | null = null;
  analysisRuns: InternalDocAnalysisRun[] = [];
  loadingAnalysisRuns = false;
  searchQuery = '';
  parseFilter = '';
  sourceFilter = '';
  sortColumn: DocSortColumn = 'uploaded';
  sortDir: SortDir = 'desc';

  private sectionExtractPollTimer: ReturnType<typeof setInterval> | null = null;
  private sectionPageRepairPollTimer: ReturnType<typeof setInterval> | null = null;
  private parsePollTimer: ReturnType<typeof setInterval> | null = null;
  private readonly pollingSectionExtractIds = new Set<string>();
  private readonly pollingSectionPageRepairIds = new Set<string>();
  private readonly pollingParseIds = new Set<string>();
  private readonly sectionExtractPollStartedAt = new Map<string, number>();
  private readonly sectionPageRepairPollStartedAt = new Map<string, number>();
  private readonly sectionPageRepairProgress = new Map<string, { label: string; pct: number | null }>();
  private static readonly SECTION_EXTRACT_POLL_MS = 11 * 60 * 1000;
  private static readonly SECTION_PAGE_REPAIR_POLL_MS = 20 * 60 * 1000;

  async ngOnInit(): Promise<void> {
    if (this.route.snapshot.queryParamMap.get('deleted') === '1') {
      await this.router.navigate(['/nd/internal-documents/deleted'], { replaceUrl: true });
      return;
    }
    this.syncDeletedFromRoute();
    this.route.data.subscribe(() => this.syncDeletedFromRoute());
  }

  ngOnDestroy(): void {
    this.stopSectionExtractPolling();
    this.stopSectionPageRepairPolling();
    this.stopParsePolling();
  }

  private stopParsePolling(): void {
    if (this.parsePollTimer) {
      clearInterval(this.parsePollTimer);
      this.parsePollTimer = null;
    }
  }

  private ensureParsePolling(): void {
    if (this.parsePollTimer || !this.pollingParseIds.size) return;
    this.parsePollTimer = setInterval(() => void this.pollParsingDocs(), 5000);
  }

  private syncParsePollingFromDocs(): void {
    for (const doc of this.docs) {
      if ((doc.parseStatus ?? '').toLowerCase() === 'processing') {
        this.pollingParseIds.add(doc.id);
      }
    }
    if (this.pollingParseIds.size) this.ensureParsePolling();
  }

  private async pollParsingDocs(): Promise<void> {
    if (!this.pollingParseIds.size) {
      this.stopParsePolling();
      return;
    }
    await this.load(true);
    for (const id of [...this.pollingParseIds]) {
      const doc = this.docs.find((d) => d.id === id);
      const st = (doc?.parseStatus ?? '').toLowerCase();
      if (st !== 'processing') {
        this.pollingParseIds.delete(id);
        if (this.parsingId === id) this.parsingId = null;
      }
    }
    if (!this.pollingParseIds.size) this.stopParsePolling();
  }

  private stopSectionExtractPolling(): void {
    if (this.sectionExtractPollTimer) {
      clearInterval(this.sectionExtractPollTimer);
      this.sectionExtractPollTimer = null;
    }
  }

  private ensureSectionExtractPolling(): void {
    if (this.sectionExtractPollTimer || !this.pollingSectionExtractIds.size) return;
    this.sectionExtractPollTimer = setInterval(() => void this.pollSectionExtractingDocs(), 5000);
  }

  private trackSectionExtractingDoc(docId: string): void {
    this.pollingSectionExtractIds.add(docId);
    this.sectionExtractPollStartedAt.set(docId, Date.now());
    this.extractingSectionsId = docId;
    const idx = this.docs.findIndex((d) => d.id === docId);
    if (idx >= 0) {
      this.docs[idx] = {
        ...this.docs[idx],
        sectionExtractStatus: 'processing',
        sectionExtractProgressLabel: 'Starting section extract…',
        sectionExtractProgressPct: 5,
        sectionExtractError: null,
      };
      if (this.sectionsFor?.id === docId) {
        this.sectionsFor = this.docs[idx];
      }
    }
    this.ensureSectionExtractPolling();
  }

  private syncSectionExtractPollingFromDocs(): void {
    for (const doc of this.docs) {
      if ((doc.sectionExtractStatus ?? '').toLowerCase() === 'processing') {
        this.pollingSectionExtractIds.add(doc.id);
        if (!this.sectionExtractPollStartedAt.has(doc.id)) {
          this.sectionExtractPollStartedAt.set(doc.id, Date.now());
        }
      }
    }
    if (this.pollingSectionExtractIds.size) this.ensureSectionExtractPolling();
  }

  private async pollSectionExtractingDocs(): Promise<void> {
    if (!this.pollingSectionExtractIds.size) {
      this.stopSectionExtractPolling();
      return;
    }
    for (const id of [...this.pollingSectionExtractIds]) {
      const res = await this.api.getInternalDocumentSections(id);
      if (!res.success || !res.data) continue;
      const st = (res.data.sectionExtractStatus ?? '').toLowerCase();
      const count = res.data.sectionCount ?? res.data.sections?.length ?? 0;
      const idx = this.docs.findIndex((d) => d.id === id);
      if (idx >= 0) {
        this.docs[idx] = {
          ...this.docs[idx],
          sectionExtractStatus: res.data.sectionExtractStatus ?? this.docs[idx].sectionExtractStatus,
          sectionCount: count,
          sectionExtractError: res.data.sectionExtractError ?? this.docs[idx].sectionExtractError,
          sectionExtractProgressLabel:
            res.data.sectionExtractProgressLabel ?? this.docs[idx].sectionExtractProgressLabel,
          sectionExtractProgressPct:
            res.data.sectionExtractProgressPct ?? this.docs[idx].sectionExtractProgressPct,
        };
        if (this.sectionsFor?.id === id) {
          this.sectionsFor = this.docs[idx];
        }
      }
      if (st === 'processing') {
        const started = this.sectionExtractPollStartedAt.get(id) ?? Date.now();
        if (Date.now() - started > NdInternalDocumentsComponent.SECTION_EXTRACT_POLL_MS) {
          this.pollingSectionExtractIds.delete(id);
          this.sectionExtractPollStartedAt.delete(id);
          if (this.extractingSectionsId === id) this.extractingSectionsId = null;
          this.error =
            'Section extract did not finish in time. Restart the API, refresh this page, then click Retry extract.';
          await this.load(true);
        }
        continue;
      }
      this.sectionExtractPollStartedAt.delete(id);
      if (st === 'extracted') {
        this.pollingSectionExtractIds.delete(id);
        if (this.extractingSectionsId === id) this.extractingSectionsId = null;
        const title = this.docs[idx]?.title ?? 'Document';
        this.message = `Extracted ${count} sections from "${title}"`;
        this.error = '';
        if (this.sectionsFor?.id === id) {
          this.sectionsFor = this.docs[idx] ?? this.sectionsFor;
          await this.loadSections(id);
        }
      } else if (st === 'failed') {
        this.pollingSectionExtractIds.delete(id);
        if (this.extractingSectionsId === id) this.extractingSectionsId = null;
        const detail = res.data.sectionExtractError?.trim();
        this.error = detail
          ? detail
          : `Section extract failed for "${this.docs[idx]?.title ?? 'document'}"`;
      }
    }
    if (!this.pollingSectionExtractIds.size) {
      this.stopSectionExtractPolling();
      await this.load(true);
    }
  }

  private stopSectionPageRepairPolling(): void {
    if (this.sectionPageRepairPollTimer) {
      clearInterval(this.sectionPageRepairPollTimer);
      this.sectionPageRepairPollTimer = null;
    }
  }

  private ensureSectionPageRepairPolling(): void {
    if (this.sectionPageRepairPollTimer || !this.pollingSectionPageRepairIds.size) return;
    this.sectionPageRepairPollTimer = setInterval(() => void this.pollSectionPageRepairingDocs(), 3000);
  }

  private trackSectionPageRepair(docId: string): void {
    this.pollingSectionPageRepairIds.add(docId);
    this.sectionPageRepairPollStartedAt.set(docId, Date.now());
    this.repairingSectionPagesId = docId;
    this.sectionPageRepairProgress.set(docId, {
      label: 'Starting page repair…',
      pct: 0,
    });
    this.ensureSectionPageRepairPolling();
  }

  private async pollSectionPageRepairingDocs(): Promise<void> {
    if (!this.pollingSectionPageRepairIds.size) {
      this.stopSectionPageRepairPolling();
      return;
    }

    for (const id of [...this.pollingSectionPageRepairIds]) {
      const res = await this.api.getInternalDocumentSections(id);
      if (!res.success || !res.data) continue;

      const repairStatus = (res.data.sectionPageRepairStatus ?? '').toLowerCase();
      const label = res.data.sectionPageRepairProgressLabel?.trim();
      const pct = res.data.sectionPageRepairProgressPct ?? null;
      if (label || pct != null) {
        this.sectionPageRepairProgress.set(id, {
          label: label || this.sectionPageRepairProgress.get(id)?.label || 'Repairing page references…',
          pct,
        });
      }

      if (repairStatus === 'processing') {
        const started = this.sectionPageRepairPollStartedAt.get(id) ?? Date.now();
        if (Date.now() - started > NdInternalDocumentsComponent.SECTION_PAGE_REPAIR_POLL_MS) {
          this.pollingSectionPageRepairIds.delete(id);
          this.sectionPageRepairPollStartedAt.delete(id);
          this.sectionPageRepairProgress.delete(id);
          if (this.repairingSectionPagesId === id) this.repairingSectionPagesId = null;
          this.error =
            'Page repair did not finish in time. Restart the API, refresh, then try Repair page refs again.';
        }
        continue;
      }

      this.sectionPageRepairPollStartedAt.delete(id);
      this.pollingSectionPageRepairIds.delete(id);
      this.sectionPageRepairProgress.delete(id);
      if (this.repairingSectionPagesId === id) this.repairingSectionPagesId = null;

      if (repairStatus === 'completed') {
        const refreshed = res.data.sectionPageRepairPagesRefreshed ?? 0;
        const total = res.data.sectionPageRepairSectionCount ?? res.data.sectionCount ?? 0;
        const title = this.docs.find((d) => d.id === id)?.title ?? 'Document';
        this.message =
          refreshed > 0
            ? `Updated PDF page references for ${refreshed} of ${total} sections in "${title}".`
            : `Section page references already match the PDF (${total} sections).`;
        this.error = '';
        if (this.sectionsFor?.id === id) {
          await this.loadSections(id);
        }
      } else if (repairStatus === 'failed') {
        const detail = res.data.sectionPageRepairError?.trim();
        this.error = detail ?? `Page repair failed for "${this.docs.find((d) => d.id === id)?.title ?? 'document'}".`;
        this.toast.show(this.error, 'error');
      }
    }

    if (!this.pollingSectionPageRepairIds.size) {
      this.stopSectionPageRepairPolling();
    }
  }

  sectionPageRepairStatusText(doc: InternalDocument): string {
    if (!this.isRepairingSectionPages(doc)) return '';
    return this.sectionPageRepairProgress.get(doc.id)?.label ?? 'Repairing page references…';
  }

  sectionPageRepairProgressPct(doc: InternalDocument): number | null {
    if (!this.isRepairingSectionPages(doc)) return null;
    const pct = this.sectionPageRepairProgress.get(doc.id)?.pct;
    if (pct == null || Number.isNaN(pct)) return null;
    return Math.max(0, Math.min(100, pct));
  }

  private syncDeletedFromRoute(): void {
    this.showDeleted = !!this.route.snapshot.data['deletedOnly'];
    void this.load();
  }

  get canUpload(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get canParse(): boolean {
    return this.canUpload;
  }

  get canViewDeleted(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get canDelete(): boolean {
    return this.canUpload && !this.showDeleted;
  }

  parseClass(status?: string): string {
    if (status === 'parsed') return 'completed';
    if (status === 'processing') return 'running';
    if (status === 'failed') return 'failed';
    return 'pending';
  }

  parseLabel(status?: string): string {
    if (status === 'parsed') return 'Parsed';
    if (status === 'processing') return 'Parsing…';
    if (status === 'failed') return 'Failed';
    return 'Pending parse';
  }

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

  isExtractingSections(doc: InternalDocument): boolean {
    return (
      this.extractingSectionsId === doc.id ||
      this.pollingSectionExtractIds.has(doc.id) ||
      doc.sectionExtractStatus === 'processing'
    );
  }

  sectionExtractStatusText(doc: InternalDocument): string {
    if (this.isExtractingSections(doc)) {
      return doc.sectionExtractProgressLabel?.trim() || this.sectionExtractLabel(doc.sectionExtractStatus);
    }
    return this.sectionExtractLabel(doc.sectionExtractStatus);
  }

  sectionExtractProgressPct(doc: InternalDocument): number | null {
    if (!this.isExtractingSections(doc)) return null;
    const pct = doc.sectionExtractProgressPct;
    if (pct == null || Number.isNaN(pct)) return null;
    return Math.max(0, Math.min(100, pct));
  }

  canExtractSections(doc: InternalDocument): boolean {
    return doc.parseStatus === 'parsed' && !this.isExtractingSections(doc);
  }

  sectionExtractButtonLabel(doc: InternalDocument): string {
    if (this.extractingSectionsId === doc.id) return 'Extracting…';
    if (doc.sectionExtractStatus === 'failed') return 'Retry extract';
    if (doc.sectionExtractStatus === 'extracted') return 'Re-extract';
    return 'Extract sections';
  }

  isParsingDoc(doc: InternalDocument): boolean {
    return (
      this.parsingId === doc.id ||
      this.pollingParseIds.has(doc.id) ||
      doc.parseStatus === 'processing'
    );
  }

  async handleParse(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.parsingId = doc.id;
    this.pollingParseIds.add(doc.id);
    this.ensureParsePolling();
    this.error = '';
    this.message = '';
    const res = await this.api.parseInternalDocument(doc.id);
    this.parsingId = null;
    if (res.success) {
      this.pollingParseIds.delete(doc.id);
      this.message = `Parse complete — "${doc.title}"`;
      const data = res.data as { parseStatus?: string; parsedAt?: string; parsedByName?: string };
      const idx = this.docs.findIndex((d) => d.id === doc.id);
      if (idx >= 0) {
        this.docs[idx] = {
          ...this.docs[idx],
          parseStatus: data?.parseStatus ?? 'parsed',
          parsedAt: data?.parsedAt ?? this.docs[idx].parsedAt,
          parsedByName: data?.parsedByName ?? this.docs[idx].parsedByName,
          parseError: null,
        };
      }
      await this.load(true);
    } else {
      this.error = res.message ?? 'Parse failed';
      await this.load(true);
    }
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const res = await this.api.getInternalDocuments(this.showDeleted);
    if (res.success && res.data) {
      this.docs = res.data as InternalDocument[];
      this.syncSectionExtractPollingFromDocs();
      this.syncParsePollingFromDocs();
    } else if (!silent || this.docs.length === 0) {
      this.error = res.message ?? 'Failed to load documents';
    }
    this.loading = false;
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

  async handleDelete(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (
      !confirm(
        `Remove "${doc.title}" from the library?\n\nNothing is deleted from the database — parse credits and file data are kept. A super admin can restore it from the Deleted tab.`,
      )
    ) {
      return;
    }
    this.deletingId = doc.id;
    this.error = '';
    const res = await this.api.hideInternalDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Document removed from library';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to delete document';
    }
    this.deletingId = null;
  }

  async handleRestore(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.deletingId = doc.id;
    this.error = '';
    const res = await this.api.restoreInternalDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Document restored';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Failed to restore document';
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

  parseButtonLabel(doc: InternalDocument): string {
    if (this.parsingId === doc.id) return 'Parsing…';
    if (doc.parseStatus === 'failed') return 'Retry parse';
    return 'Run parse';
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
      this.file = null;
      this.message =
        'Uploaded — status is Pending parse. Click Run parse when ready (Landing AI supports PDF and Word).';
      this.error = '';
      await this.load(true);
    } else {
      this.error = res.message ?? 'Upload failed';
    }
    this.uploading = false;
  }

  async openSections(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.sectionsFor = doc;
    this.sectionRows = [];
    await this.loadSections(doc.id);
  }

  closeSections(): void {
    this.sectionsFor = null;
    this.sectionRows = [];
    this.loadingSections = false;
  }

  private async loadSections(docId: string): Promise<void> {
    this.loadingSections = true;
    const res = await this.api.getInternalDocumentSections(docId);
    this.loadingSections = false;
    if (!res.success || !res.data) {
      this.toast.show(res.message ?? 'Could not load sections', 'error');
      return;
    }
    this.sectionRows = (res.data.sections ?? []).map((s) => ({
      id: s.id,
      sectionRef: s.sectionRef,
      sectionText: s.sectionText,
      sourcePage: s.sourcePage,
      displayOrder: s.displayOrder,
    }));
    const idx = this.docs.findIndex((d) => d.id === docId);
    if (idx >= 0 && res.data.sectionExtractStatus) {
      this.docs[idx] = {
        ...this.docs[idx],
        sectionExtractStatus: res.data.sectionExtractStatus,
        sectionCount: res.data.sectionCount ?? this.sectionRows.length,
        sectionExtractProgressLabel: res.data.sectionExtractProgressLabel,
        sectionExtractProgressPct: res.data.sectionExtractProgressPct,
      };
      if (this.sectionsFor?.id === docId) {
        this.sectionsFor = this.docs[idx];
      }
    }
  }

  async handleExtractSections(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (doc.parseStatus !== 'parsed') {
      this.toast.show('Parse the document first', 'warning', 4000);
      return;
    }
    const force = doc.sectionExtractStatus === 'extracted';
    this.trackSectionExtractingDoc(doc.id);
    this.error = '';
    void this.finishExtractSections(doc, force);
  }

  private async finishExtractSections(doc: InternalDocument, force: boolean): Promise<void> {
    const res = await this.api.extractInternalDocumentSections(doc.id, force);
    if (!res.success) {
      if ((res.message ?? '').toLowerCase().includes('timed out')) {
        this.message =
          'Section extract is still running (Landing AI can take several minutes). Watching for completion…';
        this.error = '';
        return;
      }
      this.extractingSectionsId = null;
      this.pollingSectionExtractIds.delete(doc.id);
      this.error = res.message ?? 'Section extract failed';
      return;
    }
    this.pollingSectionExtractIds.delete(doc.id);
    this.extractingSectionsId = null;
    const count = res.data?.sectionCount ?? 0;
    const reused = res.data?.reusedSaved;
    this.message = reused
      ? `Using ${count} saved sections for "${doc.title}" (no new Landing AI call)`
      : force
        ? `Re-extracted ${count} sections from "${doc.title}"`
        : `Extracted ${count} sections from "${doc.title}"`;
    const idx = this.docs.findIndex((d) => d.id === doc.id);
    if (idx >= 0) {
      this.docs[idx] = {
        ...this.docs[idx],
        sectionExtractStatus: res.data?.sectionExtractStatus ?? 'extracted',
        sectionCount: count,
        sectionExtractedAt: res.data?.sectionExtractedAt ?? this.docs[idx].sectionExtractedAt,
        sectionExtractError: null,
        sectionExtractProgressLabel: null,
        sectionExtractProgressPct: null,
      };
    }
    if (this.sectionsFor?.id === doc.id) {
      this.sectionsFor = this.docs[idx] ?? doc;
      await this.loadSections(doc.id);
    }
  }

  async handleRepairSectionPages(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (doc.parseStatus !== 'parsed') {
      this.toast.show('Parse the document first', 'warning', 4000);
      return;
    }
    if (!doc.sectionCount && doc.sectionExtractStatus !== 'extracted') {
      this.toast.show('Extract sections first', 'warning', 4000);
      return;
    }
    this.error = '';
    this.trackSectionPageRepair(doc.id);
    this.toast.show('Repairing page references… large manuals may take several minutes.', 'info', 6000);
    const res = await this.api.repairInternalDocumentSectionPages(doc.id);
    if (!res.success) {
      const msg = res.message ?? '';
      if (msg.toLowerCase().includes('already running')) {
        this.message = 'Page repair is already running — watching for completion…';
        this.error = '';
        return;
      }
      this.pollingSectionPageRepairIds.delete(doc.id);
      this.repairingSectionPagesId = null;
      this.sectionPageRepairProgress.delete(doc.id);
      this.error = msg || 'Could not start section page repair';
      this.toast.show(this.error, 'error');
      return;
    }
    if (res.data?.repairStatus === 'processing') {
      this.message = 'Page repair started — you can keep working; progress updates automatically.';
      this.error = '';
      return;
    }
    this.pollingSectionPageRepairIds.delete(doc.id);
    this.repairingSectionPagesId = null;
    this.sectionPageRepairProgress.delete(doc.id);
    const refreshed = res.data?.pagesRefreshed ?? 0;
    const total = res.data?.sectionCount ?? 0;
    this.message =
      refreshed > 0
        ? `Updated PDF page references for ${refreshed} of ${total} sections in "${doc.title}".`
        : `Section page references already match the parsed document (${total} sections).`;
    if (this.sectionsFor?.id === doc.id) {
      await this.loadSections(doc.id);
    }
  }

  isRepairingSectionPages(doc: InternalDocument): boolean {
    return this.repairingSectionPagesId === doc.id;
  }

  canRepairSectionPages(doc: InternalDocument): boolean {
    return (
      doc.parseStatus === 'parsed' &&
      (doc.sectionExtractStatus === 'extracted' || (doc.sectionCount ?? 0) > 0) &&
      !this.isExtractingSections(doc) &&
      !this.isRepairingSectionPages(doc)
    );
  }

  async openInternalSourcePage(docId: string, page: number): Promise<void> {
    const ok = await this.api.openInternalDocumentPdf(docId, page);
    if (!ok) this.toast.show('Could not open document PDF', 'error');
  }

  openHistory(doc: InternalDocument): void {
    this.historyFor = doc;
  }

  closeHistory(): void {
    this.historyFor = null;
  }

  async viewAnalysis(doc: InternalDocument): Promise<void> {
    this.loadingAnalysisRuns = true;
    this.analysisFor = doc;
    this.analysisRuns = [];

    const res = await this.api.getInternalDocumentAnalysisRuns(doc.id);
    this.loadingAnalysisRuns = false;

    if (!res.success || !res.data) {
      this.analysisFor = null;
      this.toast.show(res.message ?? 'Could not load analysis list', 'error');
      return;
    }

    const runs = (res.data as InternalDocAnalysisRun[]).slice().sort((a, b) => {
      const aActive = isActiveDocumentRun(a);
      const bActive = isActiveDocumentRun(b);
      if (aActive !== bActive) return aActive ? -1 : 1;
      return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
    });

    this.analysisRuns = runs;
    if (runs.length === 0) {
      this.analysisFor = null;
      this.toast.show('No analyses for this document yet', 'warning', 4000);
      return;
    }
    if (runs.length === 1) {
      this.openAnalysisRun(runs[0]);
      this.analysisFor = null;
    }
  }

  closeAnalysisPicker(): void {
    this.analysisFor = null;
    this.analysisRuns = [];
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

  formatDate = formatDate;
  formatBytes = formatBytes;
}
