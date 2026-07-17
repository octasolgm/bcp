import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
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

@Component({
  selector: 'app-nd-regulation-documents',
  standalone: true,
  imports: [CommonModule, FormsModule, NdRegulationPointsPanelComponent, NdManualRegulationPointsPanelComponent],
  templateUrl: './nd-regulation-documents.component.html',
  styleUrls: ['./nd-regulation-documents.component.scss', '../nd-shared.scss'],
})
export class NdRegulationDocumentsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

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
  savingDeptId: string | null = null;
  error = '';
  message = '';

  selectedDoc: RegulationDocument | null = null;
  selectedPoints: RegulationPoint[] = [];
  pointsSource = '';
  pointsLoading = false;
  showPointsPanel = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.loadDepartments();
    await this.loadDocs();
  }

  get canUpload(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get canExtract(): boolean {
    return this.canUpload;
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
    });
    if (res.success && res.data) {
      const all = res.data as RegulationDocument[];
      this.docs = sortRegulationDocuments(dedupeRegulationDocuments(all));
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
      this.message = 'Document uploaded and extraction started';
      this.file = null;
      await this.loadDocs(true);
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
    this.extractingId = doc.id;
    this.error = '';
    this.message = '';
    const res = await this.api.extractRegulationDocument(doc.id);
    if (res.success) {
      const data = res.data as { pointCount?: number; extractionStatus?: string };
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
  }

  async viewPoints(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    this.selectedDoc = doc;
    this.showPointsPanel = true;
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
      `Hide "${doc.name}" from the library?\n\nNothing is deleted from the database — it is only hidden (status -1). An admin can restore it later.`,
    );
    if (!confirmed) return;

    this.hidingId = doc.id;
    this.error = '';
    this.message = '';
    const res = await this.api.hideRegulationDocument(doc.id);
    if (res.success) {
      this.message = res.message ?? 'Regulation hidden from library';
      if (this.selectedDoc?.id === doc.id) this.closePointsPanel();
      await this.loadDocs(true);
    } else {
      this.error = res.message ?? 'Failed to hide regulation';
    }
    this.hidingId = null;
  }

  async openDocument(doc: RegulationDocument, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (this.isManualDoc(doc)) return;
    this.error = '';
    const res = await this.api.getRegulationDocumentFileUrl(doc.id);
    if (res.success && res.data?.url) {
      window.open(res.data.url, '_blank', 'noopener');
      return;
    }
    this.error = res.message ?? 'Could not open regulation PDF';
  }

  docPointMeta(doc: RegulationDocument): string {
    return `${doc.pointCount ?? 0} pts`;
  }

  formatDate = formatDate;

  extractionClass(status: string): string {
    if (status === 'extracted' || status === 'manual') return 'completed';
    return 'pending';
  }

  extractionLabel(status: string): string {
    if (status === 'manual') return 'Manual';
    if (status === 'extracted') return 'Extracted';
    if (status === 'pending') return 'Pending';
    if (status === 'completed') return 'Extracted';
    return status;
  }

  isSelected(doc: RegulationDocument): boolean {
    return this.showPointsPanel && this.selectedDoc?.id === doc.id;
  }

  isManualDoc(doc: RegulationDocument): boolean {
    return doc.isManual === true || doc.source === 'manual';
  }
}
