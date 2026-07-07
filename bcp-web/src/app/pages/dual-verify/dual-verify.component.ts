import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  ApiService,
  DualVerifyHealth,
  GovPoint,
  SessionProgress,
} from '../../services/api.service';
import {
  filterReportByCompliance,
} from '../../../lib/dual-verify-compliance';
import {
  complianceFilterLabel,
  DUAL_VERIFY_COPY,
  getRunBlockedReason,
  parseComplianceFilter,
  type ComplianceStatusFilter,
} from '../../../lib/dual-verify-workflow';
import {
  filterComparableGovLeafPoints,
  filterComparableGovPoints,
  formatGovPointDisplayId,
  formatSectionGroupLabel,
  groupGovPointsByChapter,
  pointMatchesPrefix,
  type GovPointChapterGroup,
} from '../../../lib/gov-point-filter';
import {
  buildDualVerifyExecutiveSummary,
  buildReportSummary,
  exportableReportItems,
  mergeReportItems,
  parsedResultsFromReport,
  progressPointToReportItem,
  reportItemsToSortedArray,
  savedResultToReportItem,
  type DualVerifyReportItem,
  type DualVerifyReportSummary,
} from '../../../lib/dual-verify-report';
import {
  clearReportBagStorage,
  loadReportBagFromStorage,
  saveReportBagToStorage,
} from '../../../lib/dual-verify-report-persistence';
import { buildReportStats } from '../../../lib/ai-lab/parse-compliance-results';
import {
  downloadDualVerifyBothPassesFormattedExcel,
  downloadDualVerifyCombinedPdf,
  downloadDualVerifyDetailPdf,
  downloadDualVerifyExcel,
  downloadDualVerifyFormattedExcel,
  downloadDualVerifyPass1DetailPdf,
  downloadDualVerifySummaryPdf,
} from '../../../lib/landing-ai/export-dual-verify-report';
import { DualVerifyResultCardComponent } from '../../components/dual-verify-result-card/dual-verify-result-card.component';
import {
  isKafkaSessionId,
  pushRecentKafkaSession,
  readRecentKafkaSessions,
} from '../../../lib/kafka-recent-sessions';

type SavedSessionOption = {
  id: string;
  label: string;
  source: 'compliance' | 'kafka' | 'recent';
  /** Which API owns this Kafka session (Nest vs .NET) */
  kafkaApi?: 'nestjs' | 'dotnet';
};

@Component({
  selector: 'app-dual-verify',
  standalone: true,
  imports: [CommonModule, FormsModule, DualVerifyResultCardComponent],
  templateUrl: './dual-verify.component.html',
  styleUrl: './dual-verify.component.scss',
})
export class DualVerifyComponent implements OnInit, OnDestroy {
  health: DualVerifyHealth | null = null;
  rawGovPoints: GovPoint[] = [];
  govPoints: GovPoint[] = [];
  chapterGroups: GovPointChapterGroup[] = [];
  selected = new Set<string>();
  granularity: 'leaf' | 'section' = 'leaf';
  aiModel = 'gemini-2.5-flash-lite';
  forceRefresh = false;
  internalFile: File | null = null;
  sessionId: string | null = null;
  progress: SessionProgress | null = null;
  running = false;
  loadingPoints = false;
  loadingAnalysis = false;
  seeding = false;
  error = '';
  manualSessionId = '';
  selectedSavedSession = '';
  savedSessions: SavedSessionOption[] = [];
  savedAnalysisHint = '';
  reportNote = '';
  reportBag = new Map<string, DualVerifyReportItem>();
  /** Kafka session loaded into report before an incremental run */
  loadedKafkaSessionId: string | null = null;
  /** Merged compliance session id (load + run combined) */
  combinedComplianceSessionId: string | null = null;
  reportSummary: DualVerifyReportSummary | null = null;
  executiveSummary = '';
  pollTimer: ReturnType<typeof setInterval> | null = null;
  govSearch = '';
  activeReportPointId: string | null = null;
  expandedChapters = new Set<string>();
  exporting = false;
  showAllReportCards = false;
  complianceFilter: ComplianceStatusFilter | null = null;
  govPanelCollapsed = false;
  reportListCollapsed = false;
  exportsExpanded = false;

  readonly models = ['gemini-2.5-flash-lite', 'gemini-2.5-flash', 'gemini-3.5-flash'];
  readonly copy = DUAL_VERIFY_COPY;

  constructor(
    private api: ApiService,
    private route: ActivatedRoute,
    private router: Router,
  ) {}

  readonly formatGovPointDisplayId = formatGovPointDisplayId;
  readonly formatSectionGroupLabel = formatSectionGroupLabel;

  ngOnInit(): void {
    this.route.queryParams.subscribe((p) => {
      this.complianceFilter = parseComplianceFilter(p['compliance']);
      if (p['saved']) {
        this.selectedSavedSession = p['saved'];
        void this.loadSavedIntoReport();
        return;
      }
      if (p['session']) {
        this.manualSessionId = p['session'];
        void this.loadSavedIntoReport();
      }
    });
    this.api.getDualVerifyHealth().subscribe((r) => (this.health = r.data));
    this.restoreReportFromStorage();
    this.refreshSavedSessions();
    this.loadGovPoints();
  }

  private restoreReportFromStorage(): void {
    const stored = loadReportBagFromStorage();
    if (!stored?.items.length) return;
    this.reportBag = mergeReportItems(new Map(), stored.items);
    if (stored.sessionId) this.sessionId = stored.sessionId;
    if (stored.complianceSessionId) {
      this.combinedComplianceSessionId = stored.complianceSessionId;
      this.selectedSavedSession = `compliance:${stored.complianceSessionId}`;
    }
    this.updateReportMeta();
    this.reportNote = `Restored ${stored.items.length} point(s) from last combined report.`;
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  get sessionGranularity(): string {
    return this.granularity === 'leaf' ? 'dual-leaf' : 'dual-section';
  }

  get persistenceOk(): boolean {
    const m = this.health?.persistence?.mode;
    return m === 'supabase' || m === 'file';
  }

  get reportItems(): DualVerifyReportItem[] {
    return reportItemsToSortedArray(this.reportBag);
  }

  get exportItems(): DualVerifyReportItem[] {
    return exportableReportItems(this.reportItems);
  }

  get visibleReportItems(): DualVerifyReportItem[] {
    return filterReportByCompliance(this.reportItems, this.complianceFilter);
  }

  get complianceFilterLabel(): string {
    return complianceFilterLabel(this.complianceFilter);
  }

  get pct(): number {
    if (!this.progress?.session.totalPoints) return 0;
    return Math.round((this.progressDone / this.progress.session.totalPoints) * 100);
  }

  get progressDone(): number {
    if (!this.progress?.session) return 0;
    const s = this.progress.session;
    return (s.completedPoints ?? 0) + (s.failedPoints ?? 0);
  }

  get progressRemaining(): number {
    if (!this.progress?.session?.totalPoints) return 0;
    return Math.max(0, this.progress.session.totalPoints - this.progressDone);
  }

  reportPointStatus(pointId: string): DualVerifyReportItem['status'] | null {
    return this.reportBag.get(pointId)?.status ?? null;
  }

  get canLoadSaved(): boolean {
    return Boolean(this.selectedSavedSession) || isKafkaSessionId(this.manualSessionId.trim());
  }

  get persistenceLabel(): string {
    const m = this.health?.persistence?.mode;
    if (m === 'supabase') return 'Supabase';
    if (m === 'file') return 'Disk';
    if (m === 'memory') return 'None';
    return '—';
  }

  get internalFileName(): string {
    return this.internalFile?.name ?? DUAL_VERIFY_COPY.defaultInternalPdfName;
  }

  get runBlockedReason(): string | null {
    return getRunBlockedReason({
      persistenceOk: this.persistenceOk,
      hasInternalFile: Boolean(this.internalFile),
      selectedCount: this.selected.size,
    });
  }

  get filteredGovPoints(): GovPoint[] {
    const q = this.govSearch.trim().toLowerCase();
    if (!q) return this.govPoints;
    return this.govPoints.filter(
      (p) =>
        p.point_id.toLowerCase().includes(q) ||
        (p.title ?? '').toLowerCase().includes(q) ||
        p.text.toLowerCase().includes(q),
    );
  }

  get visibleChapterGroups(): GovPointChapterGroup[] {
    const q = this.govSearch.trim();
    if (q) {
      const ids = new Set(this.filteredGovPoints.map((p) => p.point_id));
      return this.chapterGroups
        .map((ch) => ({
          ...ch,
          points: ch.points.filter((p) => ids.has(p.point_id)),
          sections: ch.sections
            .map((sec) => ({
              ...sec,
              points: sec.points.filter((p) => ids.has(p.point_id)),
            }))
            .filter((sec) => sec.points.length > 0),
        }))
        .filter((ch) => ch.points.length > 0);
    }
    return this.chapterGroups;
  }

  get pass2Stats() {
    return buildReportStats(parsedResultsFromReport(this.reportItems, 'llm'));
  }

  get exportsDisabled(): boolean {
    return this.exporting || !this.reportSummary || this.reportSummary.completed === 0;
  }

  get activeReportItem(): DualVerifyReportItem | null {
    if (!this.activeReportPointId) return null;
    return this.reportBag.get(this.activeReportPointId) ?? null;
  }

  dismissError(): void {
    this.error = '';
  }

  clearComplianceFilter(): void {
    this.complianceFilter = null;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { compliance: null },
      queryParamsHandling: 'merge',
    });
    this.updateReportMeta();
  }

  refreshSavedSessions(): void {
    forkJoin({
      leaf: this.api.listComplianceSessions('dual-leaf', 30).pipe(catchError(() => of({ sessions: [] }))),
      section: this.api.listComplianceSessions('dual-section', 30).pipe(catchError(() => of({ sessions: [] }))),
      kafkaDotnet: this.api.listDualVerifySessions().pipe(catchError(() => of({ data: [] }))),
      kafkaNest: this.api.listNestDualVerifySessions().pipe(catchError(() => of({ data: [] }))),
    }).subscribe({
      next: ({ leaf, section, kafkaDotnet, kafkaNest }) => {
        const options: SavedSessionOption[] = [];
        const seen = new Set<string>();

        const leafHint =
          'diagnostics' in leaf && typeof leaf.diagnostics?.hint === 'string'
            ? leaf.diagnostics.hint
            : '';
        if (leafHint) this.savedAnalysisHint = leafHint;

        for (const s of [...(leaf.sessions ?? []), ...(section.sessions ?? [])]) {
          if (s.source === 'compare_cache') continue;
          const key = `compliance:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({ id: key, label: `[DB] ${s.comparedPoints} pts combined`, source: 'compliance' });
        }

        for (const s of kafkaNest.data ?? []) {
          const key = `kafka:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({
            id: key,
            label: `[Kafka/Nest] ${s.label}`,
            source: 'kafka',
            kafkaApi: 'nestjs',
          });
        }

        for (const s of kafkaDotnet.data ?? []) {
          const key = `kafka:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({
            id: key,
            label: `[Kafka/.NET] ${s.label}`,
            source: 'kafka',
            kafkaApi: 'dotnet',
          });
        }

        for (const r of readRecentKafkaSessions()) {
          const key = `kafka:${r.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.unshift({
            id: key,
            label: `[Recent] ${r.label}`,
            source: 'recent',
            kafkaApi: 'dotnet',
          });
        }

        this.savedSessions = options;
        if (!this.selectedSavedSession && options.length) {
          this.selectedSavedSession = options[0].id;
        }
      },
    });
  }

  private applyGranularity(points: GovPoint[]): void {
    const filtered =
      this.granularity === 'leaf'
        ? filterComparableGovLeafPoints(points).comparable
        : filterComparableGovPoints(points).comparable;
    this.govPoints = filtered;
    this.chapterGroups = groupGovPointsByChapter(filtered);
    if (this.chapterGroups.length && this.expandedChapters.size === 0) {
      this.expandedChapters.add(this.chapterGroups[0].chapter);
    }
  }

  loadGovPoints(): void {
    this.loadingPoints = true;
    this.error = '';
    this.api.getGovPoints().subscribe({
      next: (r) => {
        this.rawGovPoints = r.points ?? [];
        this.selected.clear();
        this.applyGranularity(this.rawGovPoints);
        this.loadingPoints = false;
      },
      error: () => {
        this.loadingPoints = false;
        this.error =
          'Failed to load gov points from .NET API (:5100). Start Reguliq.Api (dotnet run in apps/reguliq-dotnet/src/Reguliq.Api).';
      },
    });
  }

  onGranularityChange(): void {
    this.selected.clear();
    this.applyGranularity(this.rawGovPoints);
  }

  seedBuiltin(): void {
    this.seeding = true;
    this.api.seedBuiltin().subscribe({
      next: () => {
        this.seeding = false;
        this.loadGovPoints();
      },
      error: () => {
        this.seeding = false;
        this.error = 'Could not reload gov points — is Reguliq.Api running on :5100?';
      },
    });
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

  sectionInReportCount(points: GovPoint[]): number {
    return points.filter((p) => this.reportBag.has(p.point_id)).length;
  }

  showSectionBar(sections: GovPointChapterGroup['sections'], key: string, chapter: string): boolean {
    return sections.length > 1 || key !== chapter;
  }

  inReport(id: string): boolean {
    return this.reportBag.has(id);
  }

  selectAll(): void {
    this.govPoints.forEach((p) => this.selected.add(p.point_id));
  }

  clearSelection(): void {
    this.selected.clear();
  }

  isChapterExpanded(chapter: string): boolean {
    if (this.govSearch.trim()) return true;
    return this.expandedChapters.has(chapter);
  }

  toggleChapter(chapter: string): void {
    if (this.expandedChapters.has(chapter)) this.expandedChapters.delete(chapter);
    else this.expandedChapters.add(chapter);
  }

  selectReportPoint(id: string): void {
    this.activeReportPointId = id;
    this.reportListCollapsed = true;
  }

  toggleGovPanel(): void {
    this.govPanelCollapsed = !this.govPanelCollapsed;
  }

  toggleReportList(): void {
    this.reportListCollapsed = !this.reportListCollapsed;
  }

  toggleExports(): void {
    this.exportsExpanded = !this.exportsExpanded;
  }

  agreementClass(status?: string): string {
    if (status === 'aligned') return 'aligned';
    if (status === 'failed' || status === 'error') return 'failed';
    if (status) return 'review';
    return 'pending';
  }

  truncate(text: string, max = 72): string {
    const t = text.replace(/\s+/g, ' ').trim();
    return t.length <= max ? t : `${t.slice(0, max)}…`;
  }

  useCurrentSessionId(): void {
    if (this.sessionId) this.manualSessionId = this.sessionId;
  }

  onFile(e: Event): void {
    const input = e.target as HTMLInputElement;
    this.internalFile = input.files?.[0] ?? null;
  }

  loadSavedIntoReport(): void {
    const manualId = this.manualSessionId.trim();
    const useManual = isKafkaSessionId(manualId);
    if (!useManual && !this.selectedSavedSession) {
      this.error = 'Select a saved session or paste a Kafka session ID.';
      return;
    }

    this.loadingAnalysis = true;
    this.error = '';

    if (useManual) {
      this.loadKafkaIntoReport(manualId, 'dotnet');
      return;
    }

    const [source, id] = this.selectedSavedSession.split(':');
    if (source === 'compliance') {
      this.api.loadComplianceSession(id, this.sessionGranularity).subscribe({
        next: (r) => {
          const incoming: DualVerifyReportItem[] = [];
          for (const row of (r.results as Record<string, unknown>[]) ?? []) {
            const item = savedResultToReportItem(
              row as Parameters<typeof savedResultToReportItem>[0],
            );
            if (item) incoming.push(item);
          }
          if (!incoming.length) {
            this.error = 'No dual-verify results in this session.';
            this.loadingAnalysis = false;
            return;
          }
          this.reportBag = mergeReportItems(this.reportBag, incoming);
          this.combinedComplianceSessionId = id;
          this.selectedSavedSession = `compliance:${id}`;
          this.updateReportMeta();
          this.reportNote = `Loaded ${incoming.length} point(s) into combined report (${this.exportItems.length} exportable).`;
          this.loadingAnalysis = false;
        },
        error: (e) => {
          this.loadingAnalysis = false;
          this.error = e?.error?.message ?? 'Compliance session not found (Nest/Supabase).';
        },
      });
      return;
    }

    if (source === 'kafka') {
      const opt = this.savedSessions.find((s) => s.id === this.selectedSavedSession);
      this.loadKafkaIntoReport(id, opt?.kafkaApi ?? 'dotnet');
    }
  }

  private loadKafkaIntoReport(id: string, api: 'nestjs' | 'dotnet'): void {
    const req = api === 'nestjs' ? this.api.getNestJob(id) : this.api.getJob(id);
    const tryFallback = () => {
      if (api === 'dotnet') {
        this.api.getNestJob(id).subscribe({
          next: (r) => this.finishKafkaLoad(r.data, id),
          error: () => {
            this.loadingAnalysis = false;
            this.error = 'Kafka session not found on .NET or Nest API.';
          },
        });
      } else {
        this.api.getJob(id).subscribe({
          next: (r) => this.finishKafkaLoad(r.data, id),
          error: () => {
            this.loadingAnalysis = false;
            this.error = 'Kafka session not found on Nest or .NET API.';
          },
        });
      }
    };

    req.subscribe({
      next: (r) => this.finishKafkaLoad(r.data, id),
      error: () => tryFallback(),
    });
  }

  private enrichReportItem(item: DualVerifyReportItem): DualVerifyReportItem {
    const gov = this.govPoints.find((g) => g.point_id === item.pointId);
    if (!gov) return item;
    return {
      ...item,
      pointTitle: item.pointTitle ?? gov.title,
      govText: item.govText ?? gov.text,
    };
  }

  private finishKafkaLoad(data: SessionProgress, id: string): void {
    const normalized = this.normalizeProgress(data);
    const incoming = normalized.points
      .filter((p) => p.status === 'completed')
      .map((p) =>
        this.enrichReportItem(
          progressPointToReportItem({
            pointId: p.pointId,
            pointTitle: p.pointTitle,
            status: p.status,
            landingMessage: p.landingMessage,
            llmMessage: p.llmMessage,
            agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
            errorMessage: p.errorMessage,
          }),
        ),
      );
    if (!incoming.length) {
      this.loadingAnalysis = false;
      this.error = 'No completed results in this Kafka session yet.';
      return;
    }
    this.reportBag = mergeReportItems(this.reportBag, incoming);
    this.loadedKafkaSessionId = id;
    this.sessionId = id;
    this.progress = normalized;
    this.updateReportMeta();
    this.reportNote = `Loaded ${incoming.length} point(s) into combined report (${this.exportItems.length} exportable).`;
    this.loadingAnalysis = false;
  }

  clearReport(): void {
    this.reportBag = new Map();
    this.reportSummary = null;
    this.executiveSummary = '';
    this.activeReportPointId = null;
    this.loadedKafkaSessionId = null;
    this.combinedComplianceSessionId = null;
    clearReportBagStorage();
    this.reportNote = 'Combined report cleared.';
  }

  startPipeline(): void {
    const blocked = this.runBlockedReason;
    if (blocked) {
      this.error = blocked;
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
    form.append('internalFile', this.internalFile!);

    this.running = true;
    this.error = '';
    this.progress = null;
    this.api.startJob(form).subscribe({
      next: (r) => {
        this.sessionId = (r.data as { id: string }).id;
        this.poll(this.sessionId!);
      },
      error: (e) => {
        this.running = false;
        const msg =
          e?.error?.message ??
          (typeof e?.error === 'string' ? e.error : null) ??
          e?.message ??
          'Start failed';
        this.error = msg;
      },
    });
  }

  poll(id: string): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    const tick = () => {
      this.api.getJob(id).subscribe({
        next: (r) => {
          const data = this.normalizeProgress(r.data);
          this.progress = data;
          this.mergeProgressIntoReport(data);
          const st = data.session.status;
          if (st === 'completed' || st === 'failed') {
            this.running = false;
            if (this.pollTimer) clearInterval(this.pollTimer);
            this.persistCompletedSession(data);
            const totalInReport = this.exportItems.length;
            pushRecentKafkaSession({
              id,
              label: `${data.session.granularity ?? this.granularity} · ${totalInReport} pts combined`,
              completedPoints: totalInReport,
              totalPoints: totalInReport,
            });
            this.reportNote = `Run finished — ${data.session.completedPoints} new point(s); combined report now has ${totalInReport} point(s).`;
            this.refreshSavedSessions();
          }
        },
        error: () => {
          this.running = false;
          if (this.pollTimer) clearInterval(this.pollTimer);
        },
      });
    };
    tick();
    this.pollTimer = setInterval(tick, 2500);
  }

  /** Normalize API progress payload (camelCase + numeric defaults). */
  private normalizeProgress(raw: SessionProgress): SessionProgress {
    const bag = raw as SessionProgress & {
      Session?: SessionProgress['session'];
      Points?: SessionProgress['points'];
    };
    const session = bag.session ?? bag.Session;
    const points = bag.points ?? bag.Points ?? [];
    if (!session) {
      return { session: raw.session, points };
    }
    return {
      session: {
        ...session,
        completedPoints: session.completedPoints ?? 0,
        failedPoints: session.failedPoints ?? 0,
        runningPoints: session.runningPoints ?? 0,
        queuedPoints: session.queuedPoints ?? 0,
        totalPoints: session.totalPoints ?? points.length,
      },
      points,
    };
  }

  private mergeProgressIntoReport(data: SessionProgress): void {
    const incoming = data.points.map((p) =>
      this.enrichReportItem(
        progressPointToReportItem({
          pointId: p.pointId,
          pointTitle: p.pointTitle,
          status: p.status,
          landingMessage: p.landingMessage,
          llmMessage: p.llmMessage,
          agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
          errorMessage: p.errorMessage,
        }),
      ),
    );
    this.reportBag = mergeReportItems(this.reportBag, incoming);
    this.updateReportMeta();
  }

  private updateReportMeta(): void {
    const items = this.reportItems;
    this.reportSummary = buildReportSummary(items);
    this.executiveSummary = buildDualVerifyExecutiveSummary(items, this.reportSummary);
    saveReportBagToStorage(this.reportBag, {
      sessionId: this.sessionId,
      complianceSessionId: this.combinedComplianceSessionId,
    });
    const visible = this.visibleReportItems;
    if (!visible.length) {
      this.activeReportPointId = null;
    } else if (
      !this.activeReportPointId ||
      !visible.some((item) => item.pointId === this.activeReportPointId)
    ) {
      this.activeReportPointId = visible[0].pointId;
    }
  }

  private persistCompletedSession(data: SessionProgress): void {
    const done = this.exportItems;
    if (!done.length) return;

    const results = done.map((item) => ({
      point_id: item.pointId,
      title: item.pointTitle,
      text: item.govText,
      message: item.landingMessage,
      landingMessage: item.landingMessage,
      llmMessage: item.llmMessage,
      agreementJson: item.agreement,
    }));

    this.api
      .saveComplianceSession({
        govFileHash: 'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff',
        internalFileHash: '6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717',
        govFileName: 'TFS Guidelines.pdf',
        internalFileName: this.internalFile?.name ?? 'I M P T F S.pdf',
        totalGovPoints: this.govPoints.length,
        comparedPoints: results.length,
        skippedPoints: 0,
        compareGranularity: this.sessionGranularity,
        resultsJson: results,
        summaryJson: {
          pipeline: 'kafka-dual-verify',
          sessionId: data.session.id,
          loadedFrom: this.loadedKafkaSessionId,
          totalInReport: results.length,
        },
      })
      .subscribe({
        next: (r) => {
          const body = r as { id?: string; comparedPoints?: number; merged?: boolean };
          if (body.id) {
            this.combinedComplianceSessionId = body.id;
            this.selectedSavedSession = `compliance:${body.id}`;
          }
          this.refreshSavedSessions();
        },
      });
  }

  retryFailed(): void {
    if (!this.sessionId) return;
    this.api.retryFailed(this.sessionId).subscribe(() => {
      this.running = true;
      this.poll(this.sessionId!);
    });
  }

  async exportCombinedPdf(): Promise<void> {
    if (!this.reportSummary) return;
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyCombinedPdf(
        items,
        this.reportSummary,
        `dual-verify-both-passes-${items.length}-points.pdf`,
      );
    } finally {
      this.exporting = false;
    }
  }

  async exportBothPassesExcel(): Promise<void> {
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyBothPassesFormattedExcel(
        items,
        `dual-verify-both-passes-${items.length}-points.xlsx`,
      );
    } finally {
      this.exporting = false;
    }
  }

  async exportSummary(): Promise<void> {
    if (!this.reportSummary) return;
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifySummaryPdf(
        items,
        this.reportSummary,
        `dual-verify-summary-${items.length}-points.pdf`,
      );
    } finally {
      this.exporting = false;
    }
  }

  async exportPass1Pdf(): Promise<void> {
    if (!this.reportSummary) return;
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyPass1DetailPdf(
        items,
        this.reportSummary,
        `dual-verify-pass1-${items.length}-points.pdf`,
      );
    } finally {
      this.exporting = false;
    }
  }

  async exportPass2Pdf(): Promise<void> {
    if (!this.reportSummary) return;
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyDetailPdf(
        items,
        this.reportSummary,
        `dual-verify-detail-${items.length}-points.pdf`,
      );
    } finally {
      this.exporting = false;
    }
  }

  async exportRawExcel(): Promise<void> {
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyExcel(items, `dual-verify-kafka-${items.length}-points.xlsx`);
    } finally {
      this.exporting = false;
    }
  }

  async exportPass2Excel(): Promise<void> {
    this.exporting = true;
    try {
      const items = this.exportItems;
      await downloadDualVerifyFormattedExcel(
        items,
        `dual-verify-formatted-${items.length}-points.xlsx`,
      );
    } finally {
      this.exporting = false;
    }
  }
}
