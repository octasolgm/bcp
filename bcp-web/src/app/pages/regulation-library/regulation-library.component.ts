import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { ApiService, type StoredDocumentDto } from '../../services/api.service';
import { WorkspaceService } from '../../services/workspace.service';
import { ToastService } from '../../services/toast.service';

type RegulationRow = {
  id: string;
  title: string;
  description: string;
  issuingBody: string;
  type: string;
  version: string;
  lastUpdated: string;
  status: 'active' | 'updated';
  pointCount?: number | null;
  regulationKey: string;
};

type PendingRegUpload = {
  file: File;
  title: string;
  nextVersion: string;
};

@Component({
  selector: 'app-regulation-library',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './regulation-library.component.html',
  styleUrl: './regulation-library.component.scss',
})
export class RegulationLibraryComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly workspace = inject(WorkspaceService);
  private readonly toast = inject(ToastService);

  loading = true;
  uploading = false;
  storageConfigured = false;
  regulations: RegulationRow[] = [];
  pendingUpload: PendingRegUpload | null = null;
  private fileInputEl: HTMLInputElement | null = null;

  get workspaceLabel(): string {
    return this.workspace.current().label;
  }

  ngOnInit(): void {
    this.checkStorage();
    this.refresh();
  }

  checkStorage(): void {
    this.api.getDocumentsStorageHealth().subscribe({
      next: (r) => {
        this.storageConfigured = !!r.storageConfigured;
      },
      error: () => {
        this.storageConfigured = false;
      },
    });
  }

  refresh(): void {
    this.loading = true;
    this.api.listStoredDocuments('regulation', this.workspace.current().id).subscribe({
      next: (r) => {
        this.loading = false;
        this.regulations = (r.data ?? []).map((d) => this.mapDoc(d));
      },
      error: () => {
        this.loading = false;
        this.regulations = [];
        this.toast.show('Could not load regulations from API', 'warning');
      },
    });
  }

  triggerUpload(input: HTMLInputElement): void {
    if (!this.storageConfigured) {
      this.toast.show(
        'Supabase Storage not configured. Add Supabase settings in the API and restart.',
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
    this.pendingUpload = null;
    this.clearFileInput();
  }

  private uploadFile(file: File, confirmVersionBump: boolean): void {
    this.uploading = true;
    const form = new FormData();
    form.append('file', file);
    form.append('workspaceId', this.workspace.current().id);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadRegulation(form).subscribe({
      next: (r) => {
        this.uploading = false;
        if (r.duplicate && r.existing) {
          this.pendingUpload = {
            file,
            title: r.existing.title,
            nextVersion: r.nextVersion ?? 'v2',
          };
          return;
        }
        if (!r.success) {
          this.toast.show(r.message ?? 'Regulation upload failed', 'error');
          this.clearFileInput();
          return;
        }
        if (r.document) {
          const mapped = this.mapDoc(r.document);
          this.regulations = [mapped, ...this.regulations.filter((d) => d.id !== mapped.id)];
        } else {
          this.refresh();
        }
        this.toast.show(r.message ?? 'Regulation uploaded and extracted', 'success', 3500);
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
            nextVersion: body.nextVersion ?? 'v2',
          };
          return;
        }
        this.toast.show(body?.message ?? 'Regulation upload failed', 'error', 5000);
        this.clearFileInput();
      },
    });
  }

  private mapDoc(d: StoredDocumentDto): RegulationRow {
    const isTfs = /tfs guidelines/i.test(`${d.title} ${d.originalFileName}`);
    return {
      id: d.id,
      title: d.title || d.originalFileName,
      description: isTfs
        ? 'Targeted Financial Sanctions — compliance requirements for DIFC entities'
        : `${d.category || 'Regulation'} · ${d.originalFileName}`,
      issuingBody: isTfs ? 'CBUAE' : d.category || '—',
      type: isTfs ? 'Guidance' : d.category || 'Regulation',
      version: d.version || 'v1',
      lastUpdated: d.uploaded,
      status: d.status === 'reviewed' ? 'updated' : 'active',
      pointCount: d.pointCount,
      regulationKey: isTfs ? 'tfs' : d.id,
    };
  }

  private clearFileInput(): void {
    if (this.fileInputEl) this.fileInputEl.value = '';
  }
}
