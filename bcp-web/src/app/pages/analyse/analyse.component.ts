import { Component, HostListener, inject, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { forkJoin, of } from 'rxjs';
import { catchError, finalize } from 'rxjs/operators';
import { ToastService } from '../../services/toast.service';
import { WorkspaceService } from '../../services/workspace.service';
import {
  ApiService,
  type GovPoint,
  type StoredDocumentDto,
} from '../../services/api.service';
import {
  filterComparableGovLeafPoints,
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  pointMatchesPrefix,
  type GovPointChapterGroup,
} from '../../../lib/gov-point-filter';

type AnalysisState = 'idle' | 'running' | 'complete';
type RegViewMode = 'grid' | 'list';
type RegPanelMode = 'uploaded' | 'upload';
type CompliancePanelMode = 'uploaded' | 'upload';

type RegCard = {
  id: string;
  title: string;
  source: string;
  clauses: number;
  type: string;
  /** When set, selecting this card toggles the matching stored regulation */
  matchHash?: string;
  matchTitle?: RegExp;
  /** Linked stored document when this row comes from uploads */
  documentId?: string;
};

type AnalysisStep = { label: string; done: boolean; active: boolean };

@Component({
  selector: 'app-analyse',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './analyse.component.html',
  styleUrl: './analyse.component.scss',
})
export class AnalyseComponent implements OnInit, OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly workspace = inject(WorkspaceService);

  /** Only real seeded regulation — no fake AML/FATF/Cabinet cards. */
  readonly TFS_HASH =
    'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff';
  readonly IMPTFS_HASH =
    '6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717';

  readonly regCards: RegCard[] = [
    {
      id: 'tfs',
      title: 'TFS Guidelines',
      source: 'CBUAE · July 2021',
      clauses: 96,
      type: 'Guidance',
      matchHash: 'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff',
      matchTitle: /tfs guidelines/i,
    },
  ];

  /** Soft highlight for Step 1 cards that match selected regulation files */
  highlightedCardIds = new Set<string>();

  /** Step 1 opens in list view; analysis starts idle/initial */
  regViewMode: RegViewMode = 'list';
  regPanelMode: RegPanelMode = 'uploaded';
  regSearch = '';
  regDropdownOpen = false;

  regulationDocs: StoredDocumentDto[] = [];
  /** Multi-select of already-uploaded regulation file ids */
  selectedRegIds = new Set<string>();
  selectedRegDocs: StoredDocumentDto[] = [];

  storageConfigured = false;
  loadingRegs = false;
  loadingPoints = false;
  uploadingReg = false;
  uploadingCompliance = false;
  loadingCompliance = false;
  attachingCompliance = false;
  compliancePanelMode: CompliancePanelMode = 'uploaded';
  complianceSearch = '';
  complianceDocs: StoredDocumentDto[] = [];
  selectedComplianceDocId: string | null = null;

  pendingRegBump: { file: File; nextVersion: string; title: string } | null = null;
  pendingComplianceBump: { file: File; nextVersion: string; title: string } | null = null;

  analysisState: AnalysisState = 'idle';
  /** Collapsed while analysing / showing results — frees space for progress + briefing. */
  pointsCollapsed = false;
  progress = 0;
  analysisSteps: AnalysisStep[] = [
    { label: 'Parsing and chunking document', done: false, active: false },
    { label: 'Loading regulation clauses', done: false, active: false },
    { label: 'Cross-referencing requirements', done: false, active: false },
    { label: 'Identifying gaps and risk levels', done: false, active: false },
    { label: 'Generating remediation actions', done: false, active: false },
  ];

  govSearch = '';
  rawGovPoints: GovPoint[] = [];
  govPoints: GovPoint[] = [];
  chapterGroups: GovPointChapterGroup[] = [];
  expandedChapters = new Set<string>();
  selected = new Set<string>();
  govSourceLabel = '';
  error = '';

  complianceFile: File | null = null;
  complianceDoc: StoredDocumentDto | null = null;
  complianceFileName = '';
  complianceFileSize = '';
  /** Seeded IMPTFS from Documents library (if present). */
  seededImptfs: StoredDocumentDto | null = null;

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  /** Same defaults as dual-verify workbench — do not change Kafka payload shape. */
  granularity: 'leaf' | 'section' = 'leaf';
  aiModel = 'gemini-3.5-flash';
  forceRefresh = false;
  sessionId: string | null = null;
  progressDone = 0;
  progressTotal = 0;
  /** Findings preview from completed dual-verify points */
  findingsPreview: Array<{ severity: string; title: string; section: string; pointId: string }> = [];
  /** pointId → dual-verify job status for the current session */
  sessionPointStatus = new Map<string, string>();
  retryingPointId: string | null = null;

  ngOnInit(): void {
    this.checkStorage();
    this.refreshRegulations(() => {
      this.autoSelectTfs();
    });
    this.refreshComplianceDocs(() => {
      this.autoSelectImptfs();
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    if (this.regDropdownOpen) this.closeRegDropdown();
  }

  get workspaceId(): string {
    return this.workspace.current().id;
  }

  get coverageRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    const statusOf = (id: string) => {
      if (this.sessionPointStatus.has(id)) return this.sessionPointStatus.get(id)!;
      return this.selected.has(id) && this.sessionId ? 'queued' : 'not-run';
    };
    return this.govPoints.map((p) => ({
      pointId: p.point_id,
      title: p.title || p.text,
      status: statusOf(p.point_id),
      selected: this.selected.has(p.point_id),
    }));
  }

  get coverageCounts(): {
    total: number;
    selected: number;
    run: number;
    notRun: number;
    failed: number;
    completed: number;
  } {
    const rows = this.coverageRows;
    const notRun = rows.filter((r) => r.status === 'not-run').length;
    const failed = rows.filter((r) => r.status === 'failed').length;
    const completed = rows.filter((r) => r.status === 'completed').length;
    const run = rows.filter((r) => r.status !== 'not-run').length;
    return {
      total: rows.length,
      selected: this.selected.size,
      run,
      notRun,
      failed,
      completed,
    };
  }

  get canManagePoints(): boolean {
    return !!this.sessionId && (this.analysisState === 'complete' || this.analysisState === 'running');
  }

  formatCoverageStatus(status: string): string {
    switch (status) {
      case 'completed':
        return 'Analysed';
      case 'failed':
        return 'Failed';
      case 'running':
        return 'Running';
      case 'queued':
        return 'Queued';
      case 'cancelled':
        return 'Cancelled';
      default:
        return 'Not run';
    }
  }

  get selectedRegLabel(): string {
    if (!this.selectedRegDocs.length) return 'selected regulation';
    return this.selectedRegDocs.map((d) => d.title || d.originalFileName).join(', ');
  }

  /** Real uploaded regulations only (seeded TFS + any later uploads). */
  get catalogCards(): RegCard[] {
    if (!this.regulationDocs.length) {
      return this.regCards.map((c) => ({ ...c }));
    }
    return this.regulationDocs.map((d) => {
      const preset = this.regCards.find((c) => this.cardMatchesDoc(c, d));
      return {
        id: preset?.id ?? `doc:${d.id}`,
        title: d.title || d.originalFileName || preset?.title || 'Regulation',
        source: preset?.source ?? `${d.version} · ${d.uploaded}`,
        clauses: d.pointCount ?? preset?.clauses ?? 0,
        type: d.category || preset?.type || 'Regulation',
        documentId: d.id,
        matchHash: preset?.matchHash ?? d.fileHash ?? undefined,
        matchTitle: preset?.matchTitle,
      };
    });
  }

  get filteredCatalogCards(): RegCard[] {
    const q = this.regSearch.trim().toLowerCase();
    if (!q) return this.catalogCards;
    return this.catalogCards.filter(
      (c) =>
        c.title.toLowerCase().includes(q) ||
        c.source.toLowerCase().includes(q) ||
        c.type.toLowerCase().includes(q),
    );
  }

  get filteredComplianceDocs(): StoredDocumentDto[] {
    const q = this.complianceSearch.trim().toLowerCase();
    if (!q) return this.complianceDocs;
    return this.complianceDocs.filter((d) => {
      const label = `${d.title} ${d.originalFileName} ${d.category} ${d.version}`.toLowerCase();
      return label.includes(q);
    });
  }

  get filteredRegulationDocs(): StoredDocumentDto[] {
    const q = this.regSearch.trim().toLowerCase();
    if (!q) return this.regulationDocs;
    return this.regulationDocs.filter((d) => {
      const label = `${d.title} ${d.originalFileName} ${d.category}`.toLowerCase();
      return label.includes(q);
    });
  }

  get dropdownSummary(): string {
    const n = this.selectedRegIds.size;
    if (!n) return 'Select regulation files…';
    if (n === 1) {
      const d = this.selectedRegDocs[0];
      return d ? this.fileLabel(d) : '1 file selected';
    }
    return `${n} files selected`;
  }

  get visibleChapterGroups(): GovPointChapterGroup[] {
    const q = this.govSearch.trim().toLowerCase();
    if (!q) return this.chapterGroups;
    return this.chapterGroups
      .map((ch) => ({
        ...ch,
        sections: ch.sections
          .map((sec) => ({
            ...sec,
            points: sec.points.filter(
              (p) =>
                p.point_id.toLowerCase().includes(q) ||
                (p.title ?? '').toLowerCase().includes(q) ||
                (p.text ?? '').toLowerCase().includes(q),
            ),
          }))
          .filter((sec) => sec.points.length > 0),
        points: ch.points.filter(
          (p) =>
            p.point_id.toLowerCase().includes(q) ||
            (p.title ?? '').toLowerCase().includes(q) ||
            (p.text ?? '').toLowerCase().includes(q),
        ),
      }))
      .filter((ch) => ch.sections.length > 0);
  }

  get selectedCount(): number {
    return this.selected.size;
  }

  get canRun(): boolean {
    return (
      !!this.complianceFile &&
      this.selected.size > 0 &&
      this.govPoints.length > 0 &&
      this.analysisState !== 'running' &&
      !this.loadingPoints &&
      !this.uploadingReg &&
      !this.uploadingCompliance &&
      !this.attachingCompliance &&
      !this.loadingCompliance
    );
  }

  get runBlockedReason(): string | null {
    if (this.uploadingCompliance || this.attachingCompliance) {
      return 'Wait for the compliance document to finish attaching.';
    }
    if (!this.complianceFile) {
      return 'In Step 2 pick Already uploaded or Upload a compliance document.';
    }
    if (!this.selectedRegDocs.length && !this.govPoints.length) {
      return 'In Step 1 pick Already uploaded or Upload regulation doc.';
    }
    if (!this.govPoints.length) return 'Select regulation file(s) in Step 1 to load regulation points.';
    if (!this.selected.size) return 'Select at least one regulation point on the right.';
    return null;
  }

  get logText(): string {
    const lines = [
      `✓ Compliance doc: ${this.complianceFileSize || this.complianceFileName || '—'}`,
      `✓ Regulation: ${this.selectedRegLabel} (${this.govPoints.length} clauses)`,
    ];
    if (this.selectedRegDocs.length) {
      for (const d of this.selectedRegDocs) {
        lines.push(`  · ${d.originalFileName || d.title}`);
      }
    }
    lines.push('-- Initial findings --', 'Cross-referencing requirements…');
    return lines.join('\n');
  }

  get briefingText(): string {
    return `**BRIEFING:**
Analysis of ${this.complianceFileName || 'compliance document'} against ${this.selectedRegLabel}.

**Manual Scope:** ${this.complianceFileSize || '—'} · UAE/DIFC Branch

**Key Compliance Gaps:**
${this.findingsPreview
  .slice(0, 4)
  .map((f, i) => `${i + 1}. ${f.title} (${f.section})`)
  .join('\n') || '1. Review dual-verify report for detailed gap findings'}`;
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

  refreshRegulations(after?: () => void): void {
    this.loadingRegs = true;
    this.api.listStoredDocuments('regulation', this.workspaceId).subscribe({
      next: (r) => {
        this.loadingRegs = false;
        this.regulationDocs = r.data ?? [];
        this.syncHighlightedCards();
        // Keep multi-select in sync with refreshed list
        this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
        after?.();
      },
      error: () => {
        this.loadingRegs = false;
        this.regulationDocs = [];
        after?.();
      },
    });
  }

  private autoSelectTfs(): void {
    const tfsDoc =
      this.regulationDocs.find(
        (d) => (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase(),
      ) ?? this.regulationDocs.find((d) => /tfs guidelines/i.test(`${d.title} ${d.originalFileName}`));
    if (tfsDoc) {
      if (!this.selectedRegIds.has(tfsDoc.id)) {
        this.selectedRegIds.add(tfsDoc.id);
        this.syncSelectedDocs();
        this.loadPointsForSelectedFiles();
      }
      return;
    }
    // No TFS in DB yet — seed then select
    this.seedAndSelectTfs();
  }

  refreshComplianceDocs(after?: () => void): void {
    this.loadingCompliance = true;
    this.api.listStoredDocuments('document', this.workspaceId).subscribe({
      next: (r) => {
        this.loadingCompliance = false;
        this.complianceDocs = r.data ?? [];
        this.seededImptfs =
          this.complianceDocs.find(
            (d) => (d.fileHash ?? '').toLowerCase() === this.IMPTFS_HASH.toLowerCase(),
          ) ??
          this.complianceDocs.find((d) =>
            /i\s*m\s*p\s*t\s*f\s*s|imptfs/i.test(`${d.title} ${d.originalFileName}`),
          ) ??
          null;
        if (
          this.selectedComplianceDocId &&
          !this.complianceDocs.some((d) => d.id === this.selectedComplianceDocId)
        ) {
          this.selectedComplianceDocId = null;
        }
        after?.();
      },
      error: () => {
        this.loadingCompliance = false;
        this.complianceDocs = [];
        this.seededImptfs = null;
        after?.();
      },
    });
  }

  private autoSelectImptfs(): void {
    if (this.complianceFile || !this.seededImptfs) return;
    this.selectComplianceDoc(this.seededImptfs, { silent: true });
  }

  openAlreadyUploadedCompliance(): void {
    this.compliancePanelMode = 'uploaded';
    this.error = '';
    this.refreshComplianceDocs();
  }

  openUploadCompliance(): void {
    this.compliancePanelMode = 'upload';
    this.error = '';
  }

  /** Pick an already-uploaded compliance PDF — no re-upload to storage. */
  selectComplianceDoc(doc: StoredDocumentDto, opts?: { silent?: boolean }): void {
    if (this.attachingCompliance) return;
    this.selectedComplianceDocId = doc.id;
    this.compliancePanelMode = 'uploaded';
    this.attachComplianceDoc(doc, opts);
  }

  private attachComplianceDoc(doc: StoredDocumentDto, opts?: { silent?: boolean }): void {
    this.attachingCompliance = true;
    this.error = '';
    this.api.getDocumentSignedUrl(doc.id).subscribe({
      next: async (r) => {
        try {
          if (!r.url) throw new Error('No download URL');
          const res = await fetch(r.url);
          if (!res.ok) throw new Error(`Download failed (${res.status})`);
          const blob = await res.blob();
          const name = doc.originalFileName || doc.title || 'compliance.pdf';
          const file = new File([blob], name, { type: blob.type || 'application/pdf' });
          this.complianceFile = file;
          this.complianceFileName = name;
          this.complianceFileSize = `${Math.round(file.size / 1024)} KB`;
          this.complianceDoc = doc;
          this.analysisState = 'idle';
          if (!opts?.silent) {
            this.toast.show(`Attached ${name}`, 'success', 2200);
          }
        } catch (e) {
          this.selectedComplianceDocId = null;
          this.toast.show(
            e instanceof Error ? e.message : 'Could not attach compliance document',
            'error',
            4000,
          );
        } finally {
          this.attachingCompliance = false;
        }
      },
      error: () => {
        this.attachingCompliance = false;
        this.selectedComplianceDocId = null;
        this.toast.show('Could not get compliance download link', 'error');
      },
    });
  }

  complianceMeta(doc: StoredDocumentDto): string {
    const size = doc.sizeBytes > 0 ? `${Math.round(doc.sizeBytes / 1024)} KB` : '';
    return [doc.uploaded, size].filter(Boolean).join(' · ');
  }

  setRegViewMode(mode: RegViewMode): void {
    this.regViewMode = mode;
  }

  openAlreadyUploaded(): void {
    this.regPanelMode = 'uploaded';
    this.error = '';
    this.refreshRegulations();
  }

  openUploadRegulation(): void {
    this.regPanelMode = 'upload';
    this.regDropdownOpen = false;
    this.error = '';
  }

  toggleRegDropdown(event?: Event): void {
    event?.stopPropagation();
    this.regDropdownOpen = !this.regDropdownOpen;
    if (this.regDropdownOpen) this.refreshRegulations();
  }

  closeRegDropdown(): void {
    this.regDropdownOpen = false;
  }

  /** Step 1 card → toggle matching uploaded file(s) and load points */
  selectRegCard(card: RegCard): void {
    this.openAlreadyUploaded();
    if (card.documentId) {
      this.toggleRegFileById(card.documentId);
      return;
    }
    const matches = this.regulationDocs.filter((d) => this.cardMatchesDoc(card, d));
    if (!matches.length) {
      if (card.id === 'tfs') {
        this.toast.show('Linking TFS Guidelines from seed…', 'warning', 2500);
        this.seedAndSelectTfs();
        return;
      }
      this.toast.show(
        `No uploaded file matching “${card.title}” yet — use Upload regulation doc.`,
        'warning',
        3500,
      );
      return;
    }
    // Multi-select: toggle all matches for this card
    const allSelected = matches.every((d) => this.selectedRegIds.has(d.id));
    for (const doc of matches) {
      if (allSelected) this.selectedRegIds.delete(doc.id);
      else this.selectedRegIds.add(doc.id);
    }
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  isCardSelected(card: RegCard): boolean {
    if (card.documentId) return this.selectedRegIds.has(card.documentId);
    return this.highlightedCardIds.has(card.id);
  }

  clearRegSelection(): void {
    this.selectedRegIds.clear();
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  selectAllFilteredRegs(): void {
    for (const d of this.filteredRegulationDocs) this.selectedRegIds.add(d.id);
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  /** Multi-select toggle for already-uploaded regulation files */
  toggleRegFile(doc: StoredDocumentDto, event?: Event): void {
    event?.stopPropagation();
    this.toggleRegFileById(doc.id);
  }

  toggleRegFileById(id: string): void {
    if (this.selectedRegIds.has(id)) this.selectedRegIds.delete(id);
    else this.selectedRegIds.add(id);
    this.syncSelectedDocs();
    this.loadPointsForSelectedFiles();
  }

  isRegFileSelected(id: string): boolean {
    return this.selectedRegIds.has(id);
  }

  private syncSelectedDocs(): void {
    this.selectedRegDocs = this.regulationDocs.filter((d) => this.selectedRegIds.has(d.id));
    this.syncHighlightedCards();
  }

  private syncHighlightedCards(): void {
    this.highlightedCardIds.clear();
    for (const card of this.regCards) {
      if (this.selectedRegDocs.some((d) => this.cardMatchesDoc(card, d))) {
        this.highlightedCardIds.add(card.id);
      }
    }
  }

  private cardMatchesDoc(card: RegCard, d: StoredDocumentDto): boolean {
    if (card.matchHash && (d.fileHash ?? '').toLowerCase() === card.matchHash.toLowerCase()) return true;
    const name = `${d.originalFileName} ${d.title}`;
    return !!card.matchTitle?.test(name);
  }

  private seedAndSelectTfs(): void {
    this.uploadingReg = true;
    this.api.seedTfsGuidelines().subscribe({
      next: (r) => {
        this.uploadingReg = false;
        this.refreshRegulations();
        if (r.document) {
          this.selectedRegIds.add(r.document.id);
          this.syncSelectedDocs();
          this.loadPointsForSelectedFiles();
        }
        this.toast.show(r.message ?? 'TFS linked', 'success', 3000);
      },
      error: (e: HttpErrorResponse) => {
        this.uploadingReg = false;
        this.error = e.error?.message ?? 'Could not seed TFS Guidelines.';
      },
    });
  }

  /** Load & merge points from every selected regulation file (DB cache / extract). */
  loadPointsForSelectedFiles(): void {
    const ids = [...this.selectedRegIds];
    if (!ids.length) {
      this.rawGovPoints = [];
      this.govPoints = [];
      this.chapterGroups = [];
      this.selected.clear();
      this.govSourceLabel = '';
      return;
    }

    this.loadingPoints = true;
    this.error = '';
    if (this.analysisState === 'idle') this.pointsCollapsed = false;
    forkJoin(
      ids.map((id) =>
        this.api.loadDocumentPoints(id).pipe(
          catchError((e: HttpErrorResponse) =>
            of({
              success: false as const,
              points: [] as GovPoint[],
              message: e.error?.message ?? 'load failed',
              document: undefined as StoredDocumentDto | undefined,
            }),
          ),
        ),
      ),
    )
      .pipe(finalize(() => (this.loadingPoints = false)))
      .subscribe((results) => {
        const byId = new Map<string, GovPoint>();
        const labels: string[] = [];
        for (const r of results) {
          if (r.document) {
            const idx = this.regulationDocs.findIndex((d) => d.id === r.document!.id);
            if (idx >= 0) this.regulationDocs[idx] = r.document;
          }
          for (const p of r.points ?? []) {
            byId.set(p.point_id, p);
          }
          if (r.message) labels.push(r.message);
        }
        this.syncSelectedDocs();
        this.applyPoints([...byId.values()], labels.join(' · ') || `${byId.size} points from selected files`);
        if (!byId.size) {
          this.error = 'No extract points for the selected file(s). Upload/extract the regulation first.';
        } else {
          this.toast.show(`Loaded ${this.govPoints.length} leaf points`, 'success', 2200);
        }
      });
  }

  onRegulationUpload(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.uploadRegulationFile(file, false);
  }

  confirmRegVersionBump(): void {
    if (!this.pendingRegBump) return;
    const { file } = this.pendingRegBump;
    this.pendingRegBump = null;
    this.uploadRegulationFile(file, true);
  }

  cancelRegVersionBump(): void {
    this.pendingRegBump = null;
  }

  private uploadRegulationFile(file: File, confirmVersionBump: boolean): void {
    if (!this.storageConfigured) {
      this.error = 'Configure Supabase Storage before uploading.';
      this.toast.show(this.error, 'error', 4000);
      return;
    }
    this.uploadingReg = true;
    this.error = '';
    const form = new FormData();
    form.append('file', file);
    form.append('workspaceId', this.workspaceId);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadRegulation(form).subscribe({
      next: (r) => {
        this.uploadingReg = false;
        if (r.duplicate && r.existing) {
          this.pendingRegBump = {
            file,
            nextVersion: r.nextVersion ?? 'v2',
            title: r.existing.title,
          };
          return;
        }
        if (!r.success) {
          this.error = r.message ?? 'Regulation upload failed.';
          return;
        }
        if (r.document) {
          this.regulationDocs = [
            r.document,
            ...this.regulationDocs.filter((d) => d.id !== r.document!.id),
          ];
          this.selectedRegIds.add(r.document.id);
          this.regPanelMode = 'uploaded';
          this.syncSelectedDocs();
        }
        if (r.points?.length) {
          this.applyPoints(r.points, r.message ?? `Extracted ${r.pointCount ?? 0} points`);
        } else {
          this.loadPointsForSelectedFiles();
        }
        this.toast.show(r.message ?? 'Regulation extracted', 'success', 3500);
      },
      error: (err: HttpErrorResponse) => {
        this.uploadingReg = false;
        const body = err.error as {
          duplicate?: boolean;
          nextVersion?: string;
          existing?: StoredDocumentDto;
          message?: string;
        } | null;
        if (err.status === 409 && body?.duplicate && body.existing) {
          this.pendingRegBump = {
            file,
            nextVersion: body.nextVersion ?? 'v2',
            title: body.existing.title,
          };
          return;
        }
        this.error = body?.message ?? 'Regulation upload / extract failed.';
        this.toast.show(this.error, 'error', 5000);
      },
    });
  }

  private applyPoints(points: GovPoint[], note: string): void {
    this.rawGovPoints = points;
    const filtered = filterComparableGovLeafPoints(points).comparable;
    this.govPoints = filtered;
    this.chapterGroups = groupGovPointsByChapter(filtered);
    this.selected.clear();
    // Select all by default so Run is one click after picking files
    filtered.forEach((p) => this.selected.add(p.point_id));
    this.expandedChapters.clear();
    if (this.chapterGroups.length) {
      this.expandedChapters.add(this.chapterGroups[0].chapter);
    }
    this.govSourceLabel = note;
    // Update Step 1 card clause counts for TFS when loaded
    const tfs = this.regCards.find((c) => c.id === 'tfs');
    if (tfs && this.highlightedCardIds.has('tfs')) {
      tfs.clauses = filtered.length || tfs.clauses;
    }
  }

  onComplianceSelect(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.uploadComplianceFile(file, false);
  }

  confirmComplianceVersionBump(): void {
    if (!this.pendingComplianceBump) return;
    const { file } = this.pendingComplianceBump;
    this.pendingComplianceBump = null;
    this.uploadComplianceFile(file, true);
  }

  cancelComplianceVersionBump(): void {
    this.pendingComplianceBump = null;
  }

  private uploadComplianceFile(file: File, confirmVersionBump: boolean): void {
    this.complianceFile = file;
    this.complianceFileName = file.name;
    this.complianceFileSize = `${Math.round(file.size / 1024)} KB`;
    this.selectedComplianceDocId = null;
    this.complianceDoc = null;
    this.error = '';
    this.analysisState = 'idle';
    this.compliancePanelMode = 'upload';

    if (!this.storageConfigured) {
      this.toast.show(`Attached ${file.name} (local only)`, 'warning', 3000);
      return;
    }

    this.uploadingCompliance = true;
    const form = new FormData();
    form.append('file', file);
    form.append('docKind', 'document');
    form.append('category', 'Compliance');
    form.append('filter', 'aml');
    form.append('workspaceId', this.workspaceId);
    form.append('confirmVersionBump', String(confirmVersionBump));

    this.api.uploadDocument(form).subscribe({
      next: (res) => {
        this.uploadingCompliance = false;
        if ('duplicate' in res && res.duplicate && res.existing) {
          this.pendingComplianceBump = {
            file,
            nextVersion: res.nextVersion ?? 'v2',
            title: res.existing.title,
          };
          return;
        }
        if ('data' in res && res.success && res.data) {
          this.complianceDoc = res.data;
          this.selectedComplianceDocId = res.data.id;
          this.complianceDocs = [
            res.data,
            ...this.complianceDocs.filter((d) => d.id !== res.data!.id),
          ];
          this.compliancePanelMode = 'uploaded';
          this.toast.show(`Compliance saved · ${res.data.version}`, 'success', 2500);
        }
      },
      error: (err: HttpErrorResponse) => {
        this.uploadingCompliance = false;
        const body = err.error as {
          duplicate?: boolean;
          nextVersion?: string;
          existing?: StoredDocumentDto;
          message?: string;
        } | null;
        if (err.status === 409 && body?.duplicate && body.existing) {
          this.pendingComplianceBump = {
            file,
            nextVersion: body.nextVersion ?? 'v2',
            title: body.existing.title,
          };
          this.toast.show('This file already exists — pick it from Already uploaded or confirm a new version.', 'warning', 4500);
          return;
        }
        this.toast.show(this.apiErrorMessage(err, 'Storage upload failed — file kept locally'), 'error', 4000);
      },
    });
  }

  removeCompliance(): void {
    this.complianceFile = null;
    this.complianceFileName = '';
    this.complianceFileSize = '';
    this.complianceDoc = null;
    this.selectedComplianceDocId = null;
    this.analysisState = 'idle';
    this.pointsCollapsed = false;
  }

  togglePointsCollapsed(): void {
    this.pointsCollapsed = !this.pointsCollapsed;
  }

  toggleChapter(chapter: string): void {
    if (this.expandedChapters.has(chapter)) this.expandedChapters.delete(chapter);
    else this.expandedChapters.add(chapter);
  }

  isChapterExpanded(chapter: string): boolean {
    return this.expandedChapters.has(chapter);
  }

  toggle(id: string): void {
    if (this.selected.has(id)) this.selected.delete(id);
    else this.selected.add(id);
  }

  togglePointsByPrefix(prefix: string): void {
    const matching = this.govPoints.filter((p) =>
      pointMatchesPrefix(p.point_id, prefix, p.section),
    );
    const allSelected = matching.every((p) => this.selected.has(p.point_id));
    for (const p of matching) {
      if (allSelected) this.selected.delete(p.point_id);
      else this.selected.add(p.point_id);
    }
  }

  chapterAllSelected(chapter: string, points: GovPoint[]): boolean {
    return points.length > 0 && points.every((p) => this.selected.has(p.point_id));
  }

  sectionAllSelected(points: GovPoint[]): boolean {
    return points.length > 0 && points.every((p) => this.selected.has(p.point_id));
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  selectAll(): void {
    this.govPoints.forEach((p) => this.selected.add(p.point_id));
  }

  clearSelection(): void {
    this.selected.clear();
  }

  formatChapterLabel = formatChapterLabel;
  formatSectionGroupLabel = formatSectionGroupLabel;
  formatGovPointDisplayId = formatGovPointDisplayId;

  truncate(text: string, n: number): string {
    const t = (text ?? '').trim();
    return t.length <= n ? t : `${t.slice(0, n)}…`;
  }

  fileLabel(doc: StoredDocumentDto): string {
    return doc.originalFileName || doc.title;
  }

  pointMeta(doc: StoredDocumentDto): string {
    const pts = doc.pointCount != null ? `${doc.pointCount} pts` : 'not extracted';
    return `${doc.version} · ${pts}`;
  }

  /**
   * Starts the same dual-verify Kafka job as the advanced workbench
   * (`DualVerifyComponent.startPipeline`) — same FormData fields/values.
   * Analyse only changes UI; do not alter this payload.
   */
  runAnalysis(): void {
    const blocked = this.runBlockedReason;
    if (blocked) {
      this.error = blocked;
      this.toast.show(blocked, 'error', 3000);
      return;
    }

    const ids = [...this.selected];
    const selectedGovPoints = this.govPoints.filter((p) => this.selected.has(p.point_id));

    // Exact same fields as dual-verify.component.ts startPipeline()
    const form = new FormData();
    form.append('pointIds', JSON.stringify(ids));
    form.append(
      'govPointsJson',
      JSON.stringify(
        selectedGovPoints.map((p) => ({
          pointId: p.point_id,
          title: p.title ?? null,
          text: p.text,
          section: p.section ?? null,
        })),
      ),
    );
    form.append('granularity', this.granularity);
    form.append('govDocId', 'gov-tfs-guidelines');
    form.append('internalDocId', 'internal-imptfs');
    form.append('phase2Model', this.aiModel);
    form.append('forceRefresh', String(this.forceRefresh));
    form.append('internalFile', this.complianceFile!);
    // History metadata only — ignored by Kafka CreateJobRequest; used to list runs per document
    if (this.complianceDoc?.id) {
      form.append('analysisInternalDocumentId', this.complianceDoc.id);
    }
    if (this.selectedRegDocs[0]?.id) {
      form.append('analysisRegulationDocumentId', this.selectedRegDocs[0].id);
    }
    form.append('analysisWorkspaceId', this.workspaceId);
    const regLabel =
      this.selectedRegDocs.length > 1
        ? this.selectedRegDocs
            .map((d) => d.originalFileName || d.title)
            .filter(Boolean)
            .join(' + ')
        : this.selectedRegDocs[0]?.originalFileName
          || this.selectedRegDocs[0]?.title
          || 'TFS Guidelines.pdf';
    form.append('analysisRegulationFileName', regLabel);
    form.append(
      'analysisInternalFileName',
      this.complianceFileName || this.complianceDoc?.originalFileName || 'I M P T F S.pdf',
    );

    this.stopPolling();
    this.analysisState = 'running';
    this.pointsCollapsed = true;
    this.sessionPointStatus.clear();
    for (const id of ids) this.sessionPointStatus.set(id, 'queued');
    this.error = '';
    this.progress = 8;
    this.progressDone = 0;
    this.progressTotal = ids.length;
    this.findingsPreview = [];
    this.resetSteps();
    this.markStep(0, true);
    this.analysisSteps[1].label = `Loading regulation clauses (${this.govPoints.length} found)`;

    this.api.startJob(form).subscribe({
      next: (r) => {
        this.sessionId = (r.data as { id: string }).id;
        this.toast.show('Dual-verify job started', 'success', 2000);
        this.markStep(0, false);
        this.markStep(1, true);
        this.progress = 25;
        this.poll(this.sessionId!);
      },
      error: (e: HttpErrorResponse) => {
        this.analysisState = 'idle';
        this.pointsCollapsed = false;
        this.error = this.apiErrorMessage(e, 'Start failed');
        this.toast.show(this.error, 'error', 5000);
      },
    });
  }

  openFullReport(): void {
    if (this.sessionId) {
      this.router.navigate(['/gap-analysis'], { queryParams: { session: this.sessionId } });
      return;
    }
    // Fall back to the seeded TFS × IMPTFS working document (real 32-point run).
    this.router.navigate(['/gap-analysis'], {
      queryParams: { saved: 'compliance:a339de5e-06b9-4067-bd97-e7d8086bf31e' },
    });
  }

  openFinding(pointId: string): void {
    if (this.sessionId) {
      this.router.navigate(['/dual-verify'], {
        queryParams: { session: this.sessionId, point: pointId },
      });
    }
  }

  /** Opens the existing dual-verify page unchanged (same route / UI / Kafka flow). */
  openAdvancedWorkbench(): void {
    // Prefer continuing the session from Analyse when one is active; otherwise the plain workbench.
    if (this.sessionId) {
      this.router.navigate(['/dual-verify'], { queryParams: { session: this.sessionId } });
      return;
    }
    this.router.navigate(['/dual-verify']);
  }

  /** Re-run one point (or enqueue if not yet in the session). */
  retrySinglePoint(pointId: string): void {
    if (!this.sessionId) return;
    this.enqueueRetry([pointId], true);
  }

  /** Re-run every point already in this session. */
  retryAllRunPoints(): void {
    if (!this.sessionId) return;
    const ids = [...this.sessionPointStatus.keys()];
    if (!ids.length) {
      this.toast.show('No analysed points to re-run yet', 'warning');
      return;
    }
    this.enqueueRetry(ids, true);
  }

  /** Analyse remaining regulation points that were not selected / not run. */
  runRemainingPoints(): void {
    if (!this.sessionId) return;
    const ids = this.coverageRows
      .filter((r) => r.status === 'not-run')
      .map((r) => r.pointId);
    if (!ids.length) {
      this.toast.show('All loaded points already have a run status', 'info');
      return;
    }
    for (const id of ids) this.selected.add(id);
    this.enqueueRetry(ids, false);
  }

  retryFailedOnly(): void {
    if (!this.sessionId) return;
    const ids = this.coverageRows.filter((r) => r.status === 'failed').map((r) => r.pointId);
    if (!ids.length) {
      this.toast.show('No failed points', 'info');
      return;
    }
    this.enqueueRetry(ids, false);
  }

  private enqueueRetry(pointIds: string[], forceRefresh: boolean): void {
    if (!this.sessionId || !pointIds.length) return;
    this.retryingPointId = pointIds.length === 1 ? pointIds[0] : '__batch__';
    this.error = '';
    this.analysisState = 'running';
    this.pointsCollapsed = true;

    const form = new FormData();
    form.append('pointIds', JSON.stringify(pointIds));
    form.append('forceRefresh', String(forceRefresh));
    const points = this.govPoints.filter((p) => pointIds.includes(p.point_id));
    if (points.length) {
      form.append(
        'govPointsJson',
        JSON.stringify(
          points.map((p) => ({
            pointId: p.point_id,
            title: p.title ?? null,
            text: p.text,
            section: p.section ?? null,
          })),
        ),
      );
    }
    if (this.complianceFile) form.append('internalFile', this.complianceFile);

    this.api.retryPoints(this.sessionId, form).subscribe({
      next: (r) => {
        const n = r.data?.requeued ?? pointIds.length;
        this.toast.show(`Re-queued ${n} point(s)`, 'success', 2200);
        this.retryingPointId = null;
        for (const id of pointIds) this.sessionPointStatus.set(id, 'queued');
        this.poll(this.sessionId!);
      },
      error: (e: HttpErrorResponse) => {
        this.retryingPointId = null;
        this.analysisState = 'complete';
        this.error = e.error?.message ?? 'Could not re-queue points';
        this.toast.show(this.error, 'error', 4000);
      },
    });
  }

  private poll(sessionId: string): void {
    this.stopPolling();
    this.pollTimer = setInterval(() => {
      this.api.getJob(sessionId).subscribe({
        next: (r) => {
          const s = r.data?.session;
          const points = r.data?.points ?? [];
          if (!s) return;
          this.progressDone = s.completedPoints ?? 0;
          this.progressTotal = s.totalPoints ?? this.progressTotal;
          for (const p of points) {
            if (p.pointId) this.sessionPointStatus.set(p.pointId, p.status);
          }
          const pct =
            this.progressTotal > 0
              ? Math.round((this.progressDone / this.progressTotal) * 100)
              : 10;
          this.progress = Math.min(95, Math.max(25, pct));

          if (this.progressDone > 0) {
            this.markStep(1, false);
            this.markStep(2, true);
          }
          if (pct > 50) {
            this.markStep(2, false);
            this.markStep(3, true);
          }
          if (pct > 80) {
            this.markStep(3, false);
            this.markStep(4, true);
          }

          if (s.status === 'completed' || s.status === 'failed' || s.status === 'cancelled') {
            this.stopPolling();
            this.progress = 100;
            this.analysisSteps.forEach((st) => {
              st.done = true;
              st.active = false;
            });
            if (s.status === 'completed') {
              this.analysisState = 'complete';
              this.findingsPreview = points
                .filter((p) => p.status === 'completed' || p.agreementJson)
                .slice(0, 8)
                .map((p) => ({
                  severity: this.severityFromPoint(p.agreementJson),
                  title: p.pointTitle || p.pointId,
                  section: `§${p.pointId}`,
                  pointId: p.pointId,
                }));
              this.toast.show('Analysis complete', 'success');
            } else {
              this.analysisState = 'idle';
              const failed = points.filter((p) => p.status === 'failed');
              const err = failed.find((p) => p.errorMessage)?.errorMessage;
              this.error = err
                ? `Analysis failed: ${err}`
                : `Session ${s.status}`;
              this.toast.show(this.error, 'error', 6000);
            }
          }
        },
        error: () => {
          /* keep polling */
        },
      });
    }, 2000);
  }

  private severityFromPoint(
    agreement?: { status?: string; label?: string; summary?: string } | null,
  ): string {
    const v = `${agreement?.status ?? ''} ${agreement?.label ?? ''}`.toLowerCase();
    if (!v.trim()) return 'high';
    if (v.includes('critical') || v.includes('non')) return 'critical';
    if (v.includes('partial') || v.includes('gap')) return 'high';
    if (v.includes('compliant') || v.includes('aligned') || v.includes('agree')) return 'medium';
    return 'high';
  }

  private markStep(index: number, active: boolean): void {
    this.analysisSteps.forEach((s, i) => {
      if (i < index) {
        s.done = true;
        s.active = false;
      } else if (i === index) {
        s.active = active;
        if (!active && this.progress >= 100) s.done = true;
        if (!active && index > 0) s.done = true;
      }
    });
    if (!active) this.analysisSteps[index].done = true;
  }

  private resetSteps(): void {
    this.analysisSteps.forEach((s) => {
      s.done = false;
      s.active = false;
    });
    this.analysisSteps[1].label = 'Loading regulation clauses';
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private apiErrorMessage(e: HttpErrorResponse, fallback = 'Request failed'): string {
    const body = e?.error as { message?: string; Message?: string } | string | null;
    if (typeof body === 'string' && body.trim()) return body;
    if (body && typeof body === 'object') {
      if (body.message?.trim()) return body.message;
      if (body.Message?.trim()) return body.Message;
    }
    return e?.message?.trim() || fallback;
  }
}
