import { Directive, HostListener, inject, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { forkJoin, of, Subscription } from 'rxjs';
import { catchError, finalize } from 'rxjs/operators';
import { ToastService } from '../../services/toast.service';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';
import { WorkspaceService } from '../../services/workspace.service';
import {
  ApiService,
  type GovPoint,
  type SessionProgress,
  type StoredDocumentDto,
} from '../../services/api.service';

type SessionPoint = SessionProgress['points'][number];
import {
  filterComparableGovLeafPoints,
  formatChapterLabel,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  pointMatchesPrefix,
  type GovPointChapterGroup,
} from '../../../lib/gov-point-filter';
import { ANALYSIS_ROUTES } from '../../navigation/analysis-routes';
import {
  progressPointToReportItem,
  type DualVerifyReportItem,
} from '../../../lib/dual-verify-report';

export type AnalysisState = 'idle' | 'running' | 'complete';
export type RegViewMode = 'grid' | 'list';
export type RegPanelMode = 'uploaded' | 'upload';
export type CompliancePanelMode = 'uploaded' | 'upload';

export type RegCard = {
  id: string;
  title: string;
  source: string;
  clauses: number;
  type: string;
  matchHash?: string;
  matchTitle?: RegExp;
  documentId?: string;
};

export type AnalysisStep = { label: string; done: boolean; active: boolean };

export type PointDetail = {
  pointId: string;
  title: string;
  status: string;
  severity: string;
  solved: boolean;
  summary: string;
  section: string;
};

/** Shared logic for original Analyse page and v2–v7 design variants. */
@Directive()
export abstract class AnalyseBase implements OnInit, OnDestroy {
  protected readonly toast = inject(ToastService);
  protected readonly route = inject(ActivatedRoute);
  protected readonly router = inject(Router);
  protected readonly api = inject(ApiService);
  protected readonly workspace = inject(WorkspaceService);
  protected readonly activeSessions = inject(ActiveAnalysisSessionsService);

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

  readonly designVersions = [...ANALYSIS_ROUTES].filter((v) => v.path !== '/analyse');

  /** V5 left-rail tab */
  activeSetupTab: 'regulation' | 'compliance' = 'regulation';

  highlightedCardIds = new Set<string>();
  regViewMode: RegViewMode = 'list';
  regPanelMode: RegPanelMode = 'uploaded';
  regSearch = '';
  regDropdownOpen = false;

  regulationDocs: StoredDocumentDto[] = [];
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
  selectedComplianceIds = new Set<string>();

  pendingRegBump: { file: File; nextVersion: string; title: string } | null = null;
  pendingComplianceBump: { file: File; nextVersion: string; title: string } | null = null;

  analysisState: AnalysisState = 'idle';
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
  seededImptfs: StoredDocumentDto | null = null;

  /** Point selected in results column for detail view */
  selectedDetailPointId: string | null = null;
  sessionPointResults = new Map<string, SessionPoint>();

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private sessionParamSub: Subscription | null = null;
  granularity: 'leaf' | 'section' = 'leaf';
  aiModel = 'gemini-3.5-flash';
  forceRefresh = false;
  sessionId: string | null = null;
  progressDone = 0;
  progressTotal = 0;
  findingsPreview: Array<{ severity: string; title: string; section: string; pointId: string }> = [];
  sessionPointStatus = new Map<string, string>();
  retryingPointId: string | null = null;
  /** Point IDs that belong to the active dual-verify session (subset of loaded gov points). */
  private sessionSelectedPointIds: Set<string> | null = null;
  private pointsLoadGen = 0;

  abstract readonly versionLabel: string;
  abstract readonly versionPath: string;

  ngOnInit(): void {
    this.checkStorage();
    const pendingSession = this.route.snapshot.queryParamMap.get('session');
    this.refreshRegulations(() => {
      if (!pendingSession) this.autoSelectTfs();
    });
    this.refreshComplianceDocs(() => {
      if (!pendingSession) this.autoSelectImptfs();
    });
    this.sessionParamSub = this.route.queryParamMap.subscribe((params) => {
      const sid = params.get('session');
      if (sid && sid !== this.sessionId) {
        this.attachToExistingSession(sid);
      } else if (!sid && this.sessionId) {
        this.sessionSelectedPointIds = null;
      }
    });
  }

  ngOnDestroy(): void {
    this.sessionParamSub?.unsubscribe();
    this.stopPolling();
  }

  get currentSessionId(): string | null {
    return this.sessionId;
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

  /** Selected regulation points only — for analysing list & result panels */
  get selectedCoverageRows(): Array<{
    pointId: string;
    title: string;
    status: string;
    selected: boolean;
  }> {
    return this.coverageRows.filter((r) => r.selected);
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
      selected: this.sessionId && this.sessionSelectedPointIds?.size
        ? this.sessionSelectedPointIds.size
        : this.selected.size,
      run,
      notRun,
      failed,
      completed,
    };
  }

  get selectedPointDetail(): PointDetail | null {
    if (!this.selectedDetailPointId) return null;
    const p = this.govPoints.find((g) => g.point_id === this.selectedDetailPointId);
    const result = this.sessionPointResults.get(this.selectedDetailPointId);
    const status =
      this.sessionPointStatus.get(this.selectedDetailPointId) ??
      (this.selected.has(this.selectedDetailPointId) && this.sessionId ? 'queued' : 'not-run');
    const agreement = result?.agreementJson;
    const severity = this.severityFromPoint(agreement);
    const solved = this.isPointSolved(agreement, status);
    return {
      pointId: this.selectedDetailPointId,
      title: p?.title || p?.text || result?.pointTitle || this.selectedDetailPointId,
      status,
      severity,
      solved,
      summary:
        agreement?.summary ||
        result?.errorMessage ||
        (status === 'not-run' ? 'This point has not been analysed yet.' : 'Analysis in progress…'),
      section: p?.section ? `§${p.section}` : `§${this.selectedDetailPointId}`,
    };
  }

  get selectedPointReportItem(): DualVerifyReportItem | null {
    if (!this.selectedDetailPointId) return null;
    const p = this.sessionPointResults.get(this.selectedDetailPointId);
    if (!p) return null;
    const gov = this.govPoints.find((g) => g.point_id === this.selectedDetailPointId);
    return progressPointToReportItem({
      pointId: p.pointId,
      pointTitle: p.pointTitle ?? gov?.title,
      status: p.status,
      landingMessage: p.landingMessage,
      llmMessage: p.llmMessage,
      agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
      errorMessage: p.errorMessage,
    });
  }

  get canManagePoints(): boolean {
    return !!this.sessionId && (this.analysisState === 'complete' || this.analysisState === 'running');
  }

  /** True while a dual-verify session is actively running (resume from in-progress). */
  get isSessionActive(): boolean {
    if (this.analysisState === 'running') return true;
    if (!this.sessionId) return false;
    if (this.analysisState === 'complete') {
      const active = [...this.sessionPointStatus.values()].some(
        (s) => s === 'queued' || s === 'running' || s === 'processing',
      );
      if (active) return true;
      return false;
    }
    const statuses = [...this.sessionPointStatus.values()];
    if (statuses.some((s) => s === 'queued' || s === 'running' || s === 'processing')) {
      return true;
    }
    const total = this.progressTotal ?? this.sessionSelectedPointIds?.size ?? 0;
    const done =
      (this.progressDone ?? 0) +
      statuses.filter((s) => s === 'failed').length;
    return total > 0 && done < total;
  }

  /** Done/total label for the in-progress control (e.g. 0/1). */
  get sessionProgressLabel(): string {
    const total = this.progressTotal || this.sessionSelectedPointIds?.size || 0;
    if (!total) return '0/0';
    return `${this.progressDone}/${total}`;
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
    if (this.sessionId && this.sessionSelectedPointIds?.size) {
      return this.sessionSelectedPointIds.size;
    }
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
      return 'Select or upload a compliance document.';
    }
    if (!this.selectedRegDocs.length && !this.govPoints.length) {
      return 'Select or upload at least one regulation document.';
    }
    if (!this.govPoints.length) return 'Select regulation file(s) to load regulation points.';
    if (!this.selected.size) return 'Select at least one regulation point.';
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

  selectPointForDetail(pointId: string): void {
    this.selectedDetailPointId = pointId;
  }

  isPointSolved(
    agreement?: { status?: string; label?: string; summary?: string } | null,
    status?: string,
  ): boolean {
    if (status && status !== 'completed') return false;
    const v = `${agreement?.status ?? ''} ${agreement?.label ?? ''}`.toLowerCase();
    if (!v.trim()) return false;
    return (
      v.includes('compliant') ||
      v.includes('aligned') ||
      v.includes('agree') ||
      v.includes('met') ||
      v.includes('satisfied')
    );
  }

  pointStatusClass(status: string): string {
    return status;
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
    if (this.route.snapshot.queryParamMap.get('session') || this.sessionId) return;
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
        for (const id of [...this.selectedComplianceIds]) {
          if (!this.complianceDocs.some((d) => d.id === id)) {
            this.selectedComplianceIds.delete(id);
          }
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

  selectComplianceDoc(
    doc: StoredDocumentDto,
    opts?: { silent?: boolean; preserveAnalysisState?: boolean },
  ): void {
    if (this.attachingCompliance) return;
    this.selectedComplianceDocId = doc.id;
    this.selectedComplianceIds.add(doc.id);
    this.compliancePanelMode = 'uploaded';
    this.attachComplianceDoc(doc, opts);
  }

  toggleComplianceFile(doc: StoredDocumentDto, event?: Event): void {
    event?.stopPropagation();
    if (this.selectedComplianceIds.has(doc.id)) {
      this.selectedComplianceIds.delete(doc.id);
      if (this.selectedComplianceDocId === doc.id) {
        const nextId = [...this.selectedComplianceIds][0];
        const nextDoc = nextId ? this.complianceDocs.find((d) => d.id === nextId) : null;
        if (nextDoc) {
          this.selectComplianceDoc(nextDoc, { silent: true });
        } else {
          this.removeCompliance();
        }
      }
      return;
    }
    const hadOthers = this.selectedComplianceIds.size > 0;
    this.selectComplianceDoc(doc, { silent: hadOthers });
  }

  isComplianceFileSelected(id: string): boolean {
    return this.selectedComplianceIds.has(id);
  }

  selectAllFilteredCompliance(): void {
    for (const doc of this.filteredComplianceDocs) {
      this.selectedComplianceIds.add(doc.id);
    }
    const last = this.filteredComplianceDocs[this.filteredComplianceDocs.length - 1];
    if (last) this.selectComplianceDoc(last, { silent: true });
  }

  clearComplianceSelection(): void {
    this.selectedComplianceIds.clear();
    this.removeCompliance();
  }

  private attachComplianceDoc(
    doc: StoredDocumentDto,
    opts?: { silent?: boolean; preserveAnalysisState?: boolean },
  ): void {
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
          if (!opts?.preserveAnalysisState) {
            this.analysisState = 'idle';
          }
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

  toggleRegDropdown(event?: Event): void {
    event?.stopPropagation();
    this.regDropdownOpen = !this.regDropdownOpen;
    if (this.regDropdownOpen) this.refreshRegulations();
  }

  closeRegDropdown(): void {
    this.regDropdownOpen = false;
  }

  selectRegCard(card: RegCard): void {
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
        `No uploaded file matching “${card.title}” yet — use Upload.`,
        'warning',
        3500,
      );
      return;
    }
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

  loadPointsForSelectedFiles(
    onLoaded?: () => void,
    opts?: { silent?: boolean; selectedIds?: Iterable<string> },
  ): void {
    const ids = [...this.selectedRegIds];
    if (!ids.length) {
      this.rawGovPoints = [];
      this.govPoints = [];
      this.chapterGroups = [];
      this.selected.clear();
      this.govSourceLabel = '';
      this.selectedDetailPointId = null;
      onLoaded?.();
      return;
    }

    const loadGen = ++this.pointsLoadGen;
    const selectionOverride =
      opts?.selectedIds != null
        ? new Set(opts.selectedIds)
        : this.sessionSelectedPointIds;

    this.loadingPoints = true;
    this.error = '';
    this.pointsCollapsed = false;
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
      .pipe(finalize(() => {
        if (loadGen === this.pointsLoadGen) this.loadingPoints = false;
      }))
      .subscribe((results) => {
        if (loadGen !== this.pointsLoadGen) return;
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
        this.applyPoints(
          [...byId.values()],
          labels.join(' · ') || `${byId.size} points from selected files`,
          selectionOverride,
        );
        if (!byId.size) {
          this.error = 'No extract points for the selected file(s). Upload/extract the regulation first.';
        } else if (!opts?.silent) {
          this.toast.show(`Loaded ${this.govPoints.length} leaf points`, 'success', 2200);
        }
        onLoaded?.();
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

  private applyPoints(points: GovPoint[], note: string, selectionOnly?: Set<string> | null): void {
    this.rawGovPoints = points;
    const filtered = filterComparableGovLeafPoints(points).comparable;
    this.govPoints = filtered;
    this.chapterGroups = groupGovPointsByChapter(filtered);
    this.selected.clear();
    if (selectionOnly?.size) {
      for (const p of filtered) {
        if (selectionOnly.has(p.point_id)) this.selected.add(p.point_id);
      }
    } else {
      filtered.forEach((p) => this.selected.add(p.point_id));
    }
    this.expandedChapters.clear();
    if (this.chapterGroups.length) {
      this.expandedChapters.add(this.chapterGroups[0].chapter);
    }
    this.govSourceLabel = note;
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
          this.toast.show('This file already exists — select it from the list or confirm a new version.', 'warning', 4500);
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
    this.selectedComplianceIds.clear();
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

  runAnalysis(): void {
    const blocked = this.runBlockedReason;
    if (blocked) {
      this.error = blocked;
      this.toast.show(blocked, 'error', 3000);
      return;
    }

    const ids = [...this.selected];
    const selectedGovPoints = this.govPoints.filter((p) => this.selected.has(p.point_id));

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
    this.sessionPointResults.clear();
    this.sessionSelectedPointIds = new Set(ids);
    this.selectedDetailPointId = null;
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
        this.activeSessions.refresh();
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

  stopAnalysis(): void {
    if (!this.sessionId || !this.isSessionActive) return;
    const label = this.complianceFileName || 'this analysis';
    const ok = window.confirm(
      `Stop "${label}"?\n\nQueued points will be cancelled. A point already being analysed may finish its current pass first.`,
    );
    if (!ok) return;

    const sessionId = this.sessionId;
    this.api.cancelSession(sessionId).subscribe({
      next: () => {
        this.stopPolling();
        this.analysisState = 'complete';
        this.progress = 100;
        this.analysisSteps.forEach((st) => {
          st.done = true;
          st.active = false;
        });
        for (const [pointId, status] of this.sessionPointStatus) {
          if (status === 'queued' || status === 'processing' || status === 'running') {
            this.sessionPointStatus.set(pointId, 'cancelled');
          }
        }
        this.toast.show('Analysis stopped', 'warning', 3500);
        this.activeSessions.refresh();
      },
      error: (e: HttpErrorResponse) => {
        this.toast.show(this.apiErrorMessage(e, 'Could not stop analysis'), 'error', 4000);
      },
    });
  }

  openFullReport(): void {
    if (this.sessionId) {
      this.router.navigate(['/gap-analysis'], { queryParams: { session: this.sessionId } });
      return;
    }
    this.router.navigate(['/gap-analysis'], {
      queryParams: { saved: 'compliance:a339de5e-06b9-4067-bd97-e7d8086bf31e' },
    });
  }

  openFinding(pointId: string): void {
    this.selectPointForDetail(pointId);
    if (this.sessionId) {
      this.router.navigate(['/dual-verify'], {
        queryParams: { session: this.sessionId, point: pointId },
      });
    }
  }

  openAdvancedWorkbench(): void {
    if (this.sessionId) {
      this.router.navigate(['/dual-verify'], { queryParams: { session: this.sessionId } });
      return;
    }
    this.router.navigate(['/dual-verify']);
  }

  retrySinglePoint(pointId: string): void {
    if (!this.sessionId) return;
    this.enqueueRetry([pointId], true);
  }

  retryAllRunPoints(): void {
    if (!this.sessionId) return;
    const ids = [...this.sessionPointStatus.keys()];
    if (!ids.length) {
      this.toast.show('No analysed points to re-run yet', 'warning');
      return;
    }
    this.enqueueRetry(ids, true);
  }

  runRemainingPoints(): void {
    if (!this.sessionId) return;
    const ids = this.coverageRows
      .filter((r) => r.status === 'not-run')
      .map((r) => r.pointId);
    if (!ids.length) {
      this.toast.show('All loaded points already have a run status', 'info');
      return;
    }
    for (const id of ids) {
      this.selected.add(id);
      this.sessionSelectedPointIds?.add(id);
    }
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
        for (const id of pointIds) {
          this.sessionPointStatus.set(id, 'queued');
          this.sessionSelectedPointIds?.add(id);
        }
        this.progressTotal = Math.max(this.progressTotal, this.sessionSelectedPointIds?.size ?? this.progressTotal);
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

  attachToExistingSession(sessionId: string): void {
    forkJoin({
      progress: this.api.getJob(sessionId),
      regs: this.api.listStoredDocuments('regulation', this.workspaceId),
      compliance: this.api.listStoredDocuments('document', this.workspaceId),
    }).subscribe({
      next: ({ progress: r, regs, compliance }) => {
        const s = r.data?.session;
        const points = r.data?.points ?? [];
        if (!s) {
          this.toast.show('Session not found', 'warning');
          return;
        }

        const sessionPointIds = points.map((p) => p.pointId).filter(Boolean);
        this.sessionSelectedPointIds = new Set(sessionPointIds);
        // Show Stop + progress immediately while regulation/compliance files load.
        this.applySessionJobState(sessionId, s, points);

        if (regs.data?.length) this.regulationDocs = regs.data;
        if (compliance.data?.length) {
          this.complianceDocs = compliance.data;
          this.seededImptfs =
            this.complianceDocs.find(
              (d) => (d.fileHash ?? '').toLowerCase() === this.IMPTFS_HASH.toLowerCase(),
            ) ??
            this.complianceDocs.find((d) =>
              /i\s*m\s*p\s*t\s*f\s*s|imptfs/i.test(`${d.title} ${d.originalFileName}`),
            ) ??
            null;
        }

        if (s.granularity === 'leaf' || s.granularity === 'section') {
          this.granularity = s.granularity;
        }
        if (s.phase2Model) this.aiModel = s.phase2Model;

        const regDoc = this.findStoredDoc(this.regulationDocs, {
          id: s.regulationDocumentId,
          hash: s.govFileHash,
          name: s.govFileName,
        });
        const complianceDoc = this.findStoredDoc(this.complianceDocs, {
          id: s.internalDocumentId,
          hash: s.internalFileHash,
          name: s.internalFileName,
        });

        this.selectedRegIds.clear();
        if (regDoc) {
          this.selectedRegIds.add(regDoc.id);
        } else if (this.regulationDocs.length) {
          const tfs =
            this.regulationDocs.find(
              (d) => (d.fileHash ?? '').toLowerCase() === this.TFS_HASH.toLowerCase(),
            ) ?? this.regulationDocs[0];
          if (tfs) this.selectedRegIds.add(tfs.id);
        }
        this.syncSelectedDocs();

        this.selectedComplianceIds.clear();
        if (complianceDoc) {
          this.selectedComplianceIds.add(complianceDoc.id);
          this.selectComplianceDoc(complianceDoc, { silent: true, preserveAnalysisState: true });
        }

        const applyJobState = () => this.applySessionJobState(sessionId, s, points);

        if (this.selectedRegIds.size) {
          this.loadPointsForSelectedFiles(applyJobState, {
            silent: true,
            selectedIds: sessionPointIds,
          });
        }
      },
      error: () => this.toast.show('Could not load analysis session', 'error'),
    });
  }

  private findStoredDoc(
    docs: StoredDocumentDto[],
    hint: { id?: string | null; hash?: string | null; name?: string | null },
  ): StoredDocumentDto | null {
    if (hint.id) {
      const byId = docs.find((d) => d.id === hint.id);
      if (byId) return byId;
    }
    if (hint.hash) {
      const h = hint.hash.toLowerCase();
      const byHash = docs.find((d) => (d.fileHash ?? '').toLowerCase() === h);
      if (byHash) return byHash;
    }
    if (hint.name) {
      const raw = hint.name.trim();
      const base = raw.replace(/\.[^.]+$/, '').toLowerCase();
      const byName = docs.find((d) => {
        const file = (d.originalFileName ?? '').toLowerCase();
        const title = (d.title ?? '').toLowerCase();
        return file === raw.toLowerCase() || title === base || file === raw.toLowerCase();
      });
      if (byName) return byName;
    }
    return null;
  }

  private applySessionJobState(
    sessionId: string,
    s: SessionProgress['session'],
    points: SessionProgress['points'],
  ): void {
    this.sessionId = sessionId;
    this.progressDone = s.completedPoints ?? 0;
    this.progressTotal = s.totalPoints ?? 0;
    this.sessionPointStatus.clear();
    this.sessionPointResults.clear();
    this.selected.clear();
    this.sessionSelectedPointIds = new Set<string>();

    for (const p of points) {
      if (!p.pointId) continue;
      this.sessionPointStatus.set(p.pointId, p.status);
      this.sessionPointResults.set(p.pointId, p);
      this.sessionSelectedPointIds.add(p.pointId);
      this.selected.add(p.pointId);
    }

    const firstResult =
      points.find((p) => p.status === 'completed' || p.status === 'failed') ?? points[0];
    this.selectedDetailPointId = firstResult?.pointId ?? null;

    const st = (s.status || '').toLowerCase();
    const done = (s.completedPoints ?? 0) + (s.failedPoints ?? 0);
    const stillRunning =
      st === 'queued' ||
      st === 'processing' ||
      st === 'running' ||
      (this.progressTotal > 0 && done < this.progressTotal && !['completed', 'failed', 'cancelled'].includes(st));

    if (stillRunning) {
      this.analysisState = 'running';
      this.pointsCollapsed = true;
      this.progress =
        this.progressTotal > 0
          ? Math.min(95, Math.max(10, Math.round((this.progressDone / this.progressTotal) * 100)))
          : 10;
      this.resetSteps();
      if (this.progressDone > 0) {
        this.markStep(0, true);
        this.markStep(1, true);
      }
      this.poll(sessionId);
    } else {
      this.analysisState = 'complete';
      this.progress = 100;
      this.findingsPreview = points
        .filter((p) => p.status === 'completed' || p.agreementJson)
        .slice(0, 8)
        .map((p) => ({
          severity: this.severityFromPoint(p.agreementJson),
          title: p.pointTitle || p.pointId,
          section: `§${p.pointId}`,
          pointId: p.pointId,
        }));
    }
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
            if (p.pointId) {
              this.sessionPointStatus.set(p.pointId, p.status);
              this.sessionPointResults.set(p.pointId, p);
            }
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
            this.activeSessions.refresh();
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

  protected severityFromPoint(
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
