import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import {
  ApiService,
  type DocumentAnalysisRunDto,
  type StoredDocumentDto,
} from '../../services/api.service';
import { isActiveDocumentRun } from '../../services/active-analysis-sessions.service';
import { ToastService } from '../../services/toast.service';
import { WorkspaceService } from '../../services/workspace.service';

type DocumentRow = {
  id: string;
  title: string;
  category: string;
  pages: number;
  uploaded: string;
  version: string;
  status: 'gaps' | 'reviewed' | 'compliant' | 'review-due' | string;
  gapCount?: number | null;
  filter: string;
  fileType: 'PDF' | 'DOC' | 'XLS' | string;
  history?: string[];
  storagePath?: string;
  fromStorage?: boolean;
  activeAnalysisCount?: number;
};

type PendingUpload = {
  file: File;
  title: string;
  existing: DocumentRow;
  nextVersion: string;
};

@Component({
  selector: 'app-documents',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './documents.component.html',
  styleUrl: './documents.component.scss',
})
export class DocumentsComponent implements OnInit {
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly workspace = inject(WorkspaceService);

  activeTab = 'all';
  historyFor: DocumentRow | null = null;
  analysisFor: DocumentRow | null = null;
  analysisRuns: DocumentAnalysisRunDto[] = [];
  loadingAnalysisRuns = false;
  analysisLoadError: string | null = null;
  deletingRunId: string | null = null;
  connectingOneDrive = false;
  pendingUpload: PendingUpload | null = null;
  uploading = false;
  loading = true;
  storageConfigured = false;
  storageHint = '';
  private fileInputEl: HTMLInputElement | null = null;

  readonly tabs = [
    { id: 'all', label: 'All documents' },
    { id: 'aml', label: 'AML/CFT' },
    { id: 'sanctions', label: 'Sanctions' },
    { id: 'kyc', label: 'KYC/CDD' },
  ];

  documents: DocumentRow[] = [];

  ngOnInit(): void {
    this.refresh();
  }

  get filteredDocuments(): DocumentRow[] {
    if (this.activeTab === 'all') return this.documents;
    return this.documents.filter((d) => d.filter === this.activeTab);
  }

  setTab(id: string): void {
    this.activeTab = id;
  }

  statusLabel(doc: DocumentRow): string {
    if ((doc.activeAnalysisCount ?? 0) > 0) {
      const n = doc.activeAnalysisCount ?? 0;
      return n === 1 ? 'In progress' : `${n} in progress`;
    }
    switch (doc.status) {
      case 'gaps':
        return `${doc.gapCount ?? 0} Gaps`;
      case 'reviewed':
        return 'Reviewed';
      case 'compliant':
        return 'No issues';
      case 'review-due':
        return 'Review due';
      default:
        return doc.status;
    }
  }

  statusClass(doc: DocumentRow): string {
    if ((doc.activeAnalysisCount ?? 0) > 0) return 'in-progress';
    return doc.status;
  }

  runStatusLabel(run: DocumentAnalysisRunDto): string {
    if (this.isRunInProgress(run)) return 'In progress';
    if (run.sessionAvailable === false || run.status === 'unavailable') return 'Unavailable';
    if (run.status === 'completed') return 'Completed';
    if (run.status === 'failed') return 'Failed';
    if (run.status === 'cancelled') return 'Cancelled';
    return run.status;
  }

  runProgressMeta(run: DocumentAnalysisRunDto): string {
    const done = run.completedPoints ?? 0;
    const failed = run.failedPoints ?? 0;
    const total = run.pointCount ?? 0;
    const running = run.runningPoints ?? 0;
    if (this.isRunInProgress(run)) {
      const parts = [`${done}/${total} pts`];
      if (running > 0) parts.push(`${running} running`);
      if (failed > 0) parts.push(`${failed} failed`);
      return parts.join(' · ');
    }
    if (failed > 0 && run.status === 'failed') {
      return `${failed}/${total} failed · ${done} done`;
    }
    return `${done}/${total} pts`;
  }

  runOpenLabel(run: DocumentAnalysisRunDto): string {
    if (this.isRunInProgress(run)) return 'Resume →';
    if (run.sessionAvailable === false || run.status === 'unavailable') return 'Unavailable';
    return 'Open →';
  }

  fileTypeClass(type: string): string {
    return type.toLowerCase();
  }

  formatRunWhen(iso: string): string {
    try {
      return new Date(iso).toLocaleString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return iso;
    }
  }

  refresh(): void {
    this.loading = true;
    this.api.getDocumentsStorageHealth().subscribe({
      next: (h) => {
        this.storageConfigured = h.storageConfigured;
        this.storageHint = h.hint ?? '';
      },
      error: () => {
        this.storageConfigured = false;
        this.storageHint = 'Cannot reach API documents health endpoint.';
      },
    });

    this.api.listStoredDocuments('document', this.workspace.current().id).subscribe({
      next: (res) => {
        this.documents = (res.data ?? []).map((d) => this.mapDto(d));
        this.loading = false;
      },
      error: () => {
        this.documents = [];
        this.loading = false;
        this.toast.show('Could not load documents from API', 'warning');
      },
    });
  }

  connectOneDrive(): void {
    this.connectingOneDrive = true;
    window.setTimeout(() => {
      this.connectingOneDrive = false;
      this.toast.show('OneDrive connect is simulated — uploads use Supabase Storage', 'info');
    }, 700);
  }

  triggerUpload(input: HTMLInputElement): void {
    if (!this.storageConfigured) {
      this.toast.show(
        'Supabase Storage not configured. Add Supabase:Url + ServiceRoleKey in API settings.',
        'error',
        5000,
      );
      return;
    }
    this.fileInputEl = input;
    input.click();
  }

  onUpload(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.fileInputEl = input;
    const file = input.files?.[0];
    if (!file) return;
    this.uploadFile(file, false);
  }

  confirmVersionBump(): void {
    if (!this.pendingUpload) return;
    const file = this.pendingUpload.file;
    this.pendingUpload = null;
    this.uploadFile(file, true);
  }

  cancelVersionBump(): void {
    this.toast.show('Upload cancelled', 'warning', 2000);
    this.clearPending();
  }

  openHistory(doc: DocumentRow): void {
    this.historyFor = doc;
  }

  closeHistory(): void {
    this.historyFor = null;
  }

  /**
   * - 0 runs → toast
   * - 1 run → open that dual-verify session
   * - 2+ → picker (reg1×IMPTFS, reg2×IMPTFS, …)
   */
  viewAnalysis(doc: DocumentRow): void {
    this.loadingAnalysisRuns = true;
    this.analysisLoadError = null;
    this.analysisFor = doc;
    this.analysisRuns = [];
    this.api.listDocumentAnalysisRuns(doc.id).subscribe({
      next: (res) => {
        this.loadingAnalysisRuns = false;
        const runs = (res.runs ?? []).slice().sort((a, b) => {
          const aActive = isActiveDocumentRun(a);
          const bActive = isActiveDocumentRun(b);
          if (aActive !== bActive) return aActive ? -1 : 1;
          return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
        });
        this.analysisRuns = runs;
      },
      error: (err: HttpErrorResponse) => {
        this.loadingAnalysisRuns = false;
        this.analysisLoadError = err.error?.message ?? 'Could not load analysis history.';
      },
    });
  }

  closeAnalysisPicker(): void {
    this.analysisFor = null;
    this.analysisRuns = [];
    this.analysisLoadError = null;
    this.loadingAnalysisRuns = false;
  }

  isRunInProgress(run: DocumentAnalysisRunDto): boolean {
    return isActiveDocumentRun(run);
  }

  openAnalysisRun(run: DocumentAnalysisRunDto): void {
    this.closeAnalysisPicker();

    if (run.sessionAvailable === false) {
      this.toast.show(
        'This analysis session was removed and can no longer be opened. Delete the entry or run a new analysis.',
        'warning',
        5000,
      );
      return;
    }

    if (run.isActive && run.dualVerifySessionId) {
      this.router.navigate(['/analyse-v2'], {
        queryParams: { session: run.dualVerifySessionId },
      });
      return;
    }

    if (run.dualVerifySessionId) {
      this.router.navigate(['/gap-analysis'], {
        queryParams: { session: run.dualVerifySessionId },
      });
      return;
    }
    if (run.complianceSessionId) {
      this.router.navigate(['/gap-analysis'], {
        queryParams: { saved: `compliance:${run.complianceSessionId}` },
      });
      return;
    }
    this.toast.show('This analysis has no session link', 'warning');
  }

  confirmDeleteAnalysisRun(run: DocumentAnalysisRunDto, event: Event): void {
    event.stopPropagation();
    if (!this.analysisFor) {
      this.toast.show('Open document analyses first', 'warning');
      return;
    }
    const label = run.regulationFileName
      ? `${run.regulationFileName} × ${run.internalFileName || 'compliance'}`
      : run.label;
    const ok = window.confirm(
      `Delete analysis "${label}" permanently?\n\nThis removes the history entry and linked session from the database.`,
    );
    if (!ok) return;

    this.deletingRunId = run.id;
    const onDone = (message: string) => {
      this.deletingRunId = null;
      this.analysisRuns = this.analysisRuns.filter((r) => r.id !== run.id);
      if (!this.analysisRuns.length) this.closeAnalysisPicker();
      this.toast.show(message, 'success');
      this.refresh();
    };
    const onFail = (message: string) => {
      this.deletingRunId = null;
      this.toast.show(message, 'error');
    };

    this.api.deleteDocumentAnalysisRun(this.analysisFor.id, run.id).subscribe({
      next: () => onDone('Analysis deleted'),
      error: (e) => onFail(e?.error?.message ?? 'Could not delete analysis'),
    });
  }

  openFile(doc: DocumentRow): void {
    if (!doc.fromStorage) {
      this.toast.show('File is local-only (no Storage path)', 'warning');
      return;
    }
    this.api.getDocumentSignedUrl(doc.id).subscribe({
      next: (r) => {
        if (r.url) window.open(r.url, '_blank', 'noopener');
      },
      error: () => this.toast.show('Could not get download link', 'error'),
    });
  }

  private uploadFile(file: File, confirmVersionBump: boolean): void {
    this.uploading = true;
    const form = new FormData();
    form.append('file', file);
    form.append('docKind', 'document');
    form.append('workspaceId', this.workspace.current().id);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadDocument(form).subscribe({
      next: (res) => {
        this.uploading = false;
        if ('data' in res && res.success && res.data) {
          const mapped = this.mapDto(res.data);
          this.documents = [mapped, ...this.documents.filter((d) => d.id !== mapped.id)];
          this.toast.show(res.message ?? `Uploaded ${mapped.version}`, 'success');
          this.clearFileInput();
          return;
        }
        this.clearFileInput();
      },
      error: (err: HttpErrorResponse) => {
        this.uploading = false;
        const body = err.error as {
          duplicate?: boolean;
          nextVersion?: string;
          existing?: StoredDocumentDto;
          message?: string;
        } | null;

        if (err.status === 409 && body?.duplicate && body.existing) {
          this.pendingUpload = {
            file,
            title: body.existing.title,
            existing: this.mapDto(body.existing),
            nextVersion: body.nextVersion ?? 'v2',
          };
          return;
        }

        this.toast.show(body?.message ?? 'Upload to Supabase failed', 'error', 5000);
        this.clearFileInput();
      },
    });
  }

  private mapDto(d: StoredDocumentDto): DocumentRow {
    return {
      id: d.id,
      title: d.title,
      category: d.category,
      pages: d.pages,
      uploaded: d.uploaded,
      version: d.version,
      status: d.status,
      gapCount: d.gapCount,
      filter: d.filter,
      fileType: d.fileType,
      history: d.history ?? [],
      storagePath: d.storagePath,
      fromStorage: true,
      activeAnalysisCount: d.activeAnalysisCount ?? 0,
    };
  }

  private clearPending(): void {
    this.pendingUpload = null;
    this.clearFileInput();
  }

  private clearFileInput(): void {
    if (this.fileInputEl) this.fileInputEl.value = '';
  }
}
