import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import {
  ApiService,
  ComplianceSessionSummary,
  DualVerifyHealth,
  DualVerifySessionSummary,
  GovPoint,
  SessionProgress,
} from '../../services/api.service';
import {
  filterComparableGovLeafPoints,
  filterComparableGovPoints,
  groupGovPointsByChapter,
  type GovPointChapterGroup,
} from '../../../lib/gov-point-filter';
import {
  buildDualVerifyExecutiveSummary,
  buildReportSummary,
  mergeReportItems,
  progressPointToReportItem,
  reportItemsToSortedArray,
  savedResultToReportItem,
  type DualVerifyReportItem,
  type DualVerifyReportSummary,
} from '../../../lib/dual-verify-report';
import {
  exportBothPassesPdf,
  exportDualVerifyExcel,
  exportSummaryPdf,
} from '../../../lib/export-dual-verify-report';
import {
  isKafkaSessionId,
  pushRecentKafkaSession,
  readRecentKafkaSessions,
  type RecentKafkaSession,
} from '../../../lib/kafka-recent-sessions';

type SavedSessionOption = {
  id: string;
  label: string;
  source: 'db' | 'kafka' | 'recent';
};

@Component({
  selector: 'app-dual-verify',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './dual-verify.component.html',
  styleUrl: './dual-verify.component.scss',
})
export class DualVerifyComponent implements OnInit, OnDestroy {
  health: DualVerifyHealth | null = null;
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
  error = '';
  manualSessionId = '';
  selectedSavedSession = '';
  savedSessions: SavedSessionOption[] = [];
  reportBag = new Map<string, DualVerifyReportItem>();
  reportSummary: DualVerifyReportSummary | null = null;
  executiveSummary = '';
  pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly models = ['gemini-2.5-flash-lite', 'gemini-2.5-flash', 'gemini-3.5-flash'];

  constructor(
    private api: ApiService,
    private route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    this.route.queryParams.subscribe((p) => {
      if (p['session']) {
        this.manualSessionId = p['session'];
        this.loadSession();
      }
    });
    this.api.getDualVerifyHealth().subscribe((r) => (this.health = r.data));
    this.refreshSavedSessions();
    this.loadGovPoints();
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  get persistenceOk(): boolean {
    const m = this.health?.persistence?.mode;
    return m === 'supabase' || m === 'file';
  }

  get reportItems(): DualVerifyReportItem[] {
    return reportItemsToSortedArray(this.reportBag);
  }

  get pct(): number {
    if (!this.progress?.session.totalPoints) return 0;
    const s = this.progress.session;
    return Math.round(((s.completedPoints + s.failedPoints) / s.totalPoints) * 100);
  }

  refreshSavedSessions(): void {
    forkJoin({
      leaf: this.api.listComplianceSessions('dual-leaf', 30),
      section: this.api.listComplianceSessions('dual-section', 30),
      kafka: this.api.listDualVerifySessions(),
    }).subscribe({
      next: ({ leaf, section, kafka }) => {
        const options: SavedSessionOption[] = [];
        for (const s of [...(leaf.sessions ?? []), ...(section.sessions ?? [])]) {
          options.push({ id: s.id, label: `[DB] ${s.label}`, source: 'db' });
        }
        for (const s of kafka.data ?? []) {
          options.push({ id: s.id, label: `[Kafka] ${s.label}`, source: 'kafka' });
        }
        for (const r of readRecentKafkaSessions()) {
          options.push({
            id: r.id,
            label: `[Recent] ${r.label}`,
            source: 'recent',
          });
        }
        this.savedSessions = options;
      },
    });
  }

  loadGovPoints(): void {
    this.api.getGovPoints().subscribe({
      next: (r) => {
        const all = r.points ?? [];
        if (all.length === 0) return;
        const filtered =
          this.granularity === 'leaf'
            ? filterComparableGovLeafPoints(all).comparable
            : filterComparableGovPoints(all).comparable;
        this.govPoints = filtered;
        this.chapterGroups = groupGovPointsByChapter(filtered);
        if (this.selected.size === 0) {
          filtered.slice(0, 3).forEach((p) => this.selected.add(p.point_id));
        }
      },
      error: () => (this.error = 'Failed to load gov points'),
    });
  }

  onGranularityChange(): void {
    this.selected.clear();
    this.loadGovPoints();
  }

  seedBuiltin(): void {
    this.api.seedBuiltin().subscribe(() => this.loadGovPoints());
  }

  toggle(id: string): void {
    if (this.selected.has(id)) this.selected.delete(id);
    else this.selected.add(id);
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

  onFile(e: Event): void {
    const input = e.target as HTMLInputElement;
    this.internalFile = input.files?.[0] ?? null;
  }

  loadSavedIntoReport(): void {
    const id = this.selectedSavedSession || this.manualSessionId.trim();
    if (!id) return;

    if (isKafkaSessionId(id)) {
      this.api.getJob(id).subscribe({
        next: (r) => {
          this.mergeProgressIntoReport(r.data);
          this.sessionId = id;
          this.progress = r.data;
        },
        error: () => (this.error = 'Kafka session not found'),
      });
      return;
    }

    this.api.loadComplianceSession(id).subscribe({
      next: (r) => {
        const incoming: DualVerifyReportItem[] = [];
        for (const row of (r.results as Record<string, unknown>[]) ?? []) {
          const item = savedResultToReportItem(row as Parameters<typeof savedResultToReportItem>[0]);
          if (item) incoming.push(item);
        }
        this.reportBag = mergeReportItems(this.reportBag, incoming);
        this.updateReportMeta();
      },
      error: () => (this.error = 'Compliance session not found'),
    });
  }

  clearReport(): void {
    this.reportBag = new Map();
    this.reportSummary = null;
    this.executiveSummary = '';
  }

  startPipeline(): void {
    if (!this.persistenceOk) {
      this.error = 'Persistence not ready — configure database connection.';
      return;
    }
  if (!this.internalFile) {
      this.error = 'Attach internal PDF for Phase 2.';
      return;
    }
    const ids = [...this.selected];
    if (!ids.length) {
      this.error = 'Select at least one gov point.';
      return;
    }

    const form = new FormData();
    form.append('pointIds', JSON.stringify(ids));
    form.append('granularity', this.granularity);
    form.append('govDocId', 'gov-tfs-guidelines');
    form.append('internalDocId', 'internal-imptfs');
    form.append('phase2Model', this.aiModel);
    form.append('forceRefresh', String(this.forceRefresh));
    form.append('internalFile', this.internalFile);

    this.running = true;
    this.error = '';
    this.api.startJob(form).subscribe({
      next: (r) => {
        this.sessionId = (r.data as { id: string }).id;
        this.poll(this.sessionId!);
      },
      error: (e) => {
        this.running = false;
        this.error = e?.error?.message ?? 'Start failed';
      },
    });
  }

  poll(id: string): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    const tick = () => {
      this.api.getJob(id).subscribe({
        next: (r) => {
          this.progress = r.data;
          this.mergeProgressIntoReport(r.data);
          const st = r.data.session.status;
          if (st === 'completed' || st === 'failed') {
            this.running = false;
            if (this.pollTimer) clearInterval(this.pollTimer);
            this.persistCompletedSession(r.data);
            pushRecentKafkaSession({
              id,
              label: `${r.data.session.granularity ?? this.granularity} · ${r.data.session.completedPoints}/${r.data.session.totalPoints}`,
              completedPoints: r.data.session.completedPoints,
              totalPoints: r.data.session.totalPoints,
            });
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

  private mergeProgressIntoReport(data: SessionProgress): void {
    const incoming = data.points.map((p) =>
      progressPointToReportItem({
        pointId: p.pointId,
        pointTitle: p.pointTitle,
        status: p.status,
        landingMessage: p.landingMessage,
        llmMessage: p.llmMessage,
        agreementJson: p.agreementJson as DualVerifyReportItem['agreement'],
        errorMessage: p.errorMessage,
      }),
    );
    this.reportBag = mergeReportItems(this.reportBag, incoming);
    this.updateReportMeta();
  }

  private updateReportMeta(): void {
    const items = this.reportItems;
    this.reportSummary = buildReportSummary(items);
    this.executiveSummary = buildDualVerifyExecutiveSummary(items, this.reportSummary);
  }

  private persistCompletedSession(data: SessionProgress): void {
    const results = data.points
      .filter((p) => p.landingMessage && p.llmMessage)
      .map((p) => ({
        point_id: p.pointId,
        title: p.pointTitle,
        message: p.landingMessage,
        landingMessage: p.landingMessage,
        llmMessage: p.llmMessage,
        agreementJson: p.agreementJson,
      }));
    if (!results.length) return;
    this.api
      .saveComplianceSession({
        govFileHash: 'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff',
        internalFileHash: '6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717',
        govFileName: 'TFS Guidelines.pdf',
        internalFileName: this.internalFile?.name ?? 'I M P T F S.pdf',
        totalGovPoints: this.govPoints.length,
        comparedPoints: results.length,
        skippedPoints: 0,
        compareGranularity: this.granularity === 'leaf' ? 'dual-leaf' : 'dual-section',
        resultsJson: results,
        summaryJson: { pipeline: 'kafka-dual-verify', sessionId: data.session.id },
      })
      .subscribe();
  }

  loadSession(): void {
    const id = this.manualSessionId.trim();
    if (!id) return;
    this.selectedSavedSession = '';
    if (isKafkaSessionId(id)) {
      this.api.getJob(id).subscribe({
        next: (r) => {
          this.progress = r.data;
          this.sessionId = id;
          this.mergeProgressIntoReport(r.data);
        },
        error: () => (this.error = 'Session not found'),
      });
    }
  }

  retryFailed(): void {
    if (!this.sessionId) return;
    this.api.retryFailed(this.sessionId).subscribe(() => {
      this.running = true;
      this.poll(this.sessionId!);
    });
  }

  async exportSummary(): Promise<void> {
    if (!this.reportSummary) return;
    await exportSummaryPdf(this.reportItems, this.reportSummary);
  }

  async exportBothPdf(): Promise<void> {
    await exportBothPassesPdf(this.reportItems);
  }

  async exportExcel(): Promise<void> {
    await exportDualVerifyExcel(this.reportItems);
  }
}
