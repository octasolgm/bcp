import { Component, OnInit, inject } from '@angular/core';
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
import type { AnalysisRunSummary, InternalDocument } from '../../../../lib/nd/types';

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
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-internal-documents.component.html',
  styleUrls: ['./nd-internal-documents.component.scss', '../nd-shared.scss'],
})
export class NdInternalDocumentsComponent implements OnInit {
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
  deletingId: string | null = null;
  showDeleted = false;
  error = '';
  message = '';
  historyFor: InternalDocument | null = null;
  analysisFor: InternalDocument | null = null;
  analysisRuns: InternalDocAnalysisRun[] = [];
  loadingAnalysisRuns = false;
  searchQuery = '';
  parseFilter = '';
  sourceFilter = '';
  sortColumn: DocSortColumn = 'uploaded';
  sortDir: SortDir = 'desc';

  async ngOnInit(): Promise<void> {
    if (this.route.snapshot.queryParamMap.get('deleted') === '1') {
      await this.router.navigate(['/nd/internal-documents/deleted'], { replaceUrl: true });
      return;
    }
    this.syncDeletedFromRoute();
    this.route.data.subscribe(() => this.syncDeletedFromRoute());
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

  isParsingDoc(doc: InternalDocument): boolean {
    return this.parsingId === doc.id || doc.parseStatus === 'processing';
  }

  async handleParse(doc: InternalDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.parsingId = doc.id;
    this.error = '';
    this.message = '';
    const res = await this.api.parseInternalDocument(doc.id);
    this.parsingId = null;
    if (res.success) {
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
    }
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading = true;
    this.error = '';
    const res = await this.api.getInternalDocuments(this.showDeleted);
    if (res.success && res.data) {
      this.docs = res.data as InternalDocument[];
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
