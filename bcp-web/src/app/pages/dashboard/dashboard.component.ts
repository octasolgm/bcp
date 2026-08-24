import { Component, OnInit } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { forkJoin, Observable, of } from 'rxjs';
import { catchError, finalize, timeout } from 'rxjs/operators';
import {
  ApiService,
  DashboardMetrics,
  DualVerifyHealth,
  DualVerifySessionSummary,
} from '../../services/api.service';
import { NdApiService, type NdDashboardStats } from '../../services/nd/nd-api.service';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { environment } from '../../../environments/environment';
import { shellRoute, shellRouteSegments } from '../../services/app-route-prefix';
import {
  isLegacyAnalysisRun,
  ndAnalysisRunLink,
  ndAnalysisRunQuery,
  ndAnalysisRunTarget,
} from '../../../lib/nd/run-links';
import {
  analysisRunWorkflowLabel,
  sortAnalysisRunsByRecent,
} from '../../../lib/nd/analysis-run-status';
import { formatDate } from '../../../lib/nd/utils';
import type { AnalysisRunSummary } from '../../../lib/nd/types';
import { runGapStatsFromSummary, type RunGapStatsSummary } from '../../../lib/nd/run-gap-stats';
import {
  mergeGapRiskCounts,
} from '../../../lib/nd/dashboard-gap-risk';
import {
  emptyGapRiskCounts,
  RISK_STANDARD_SUMMARY,
  type GapRiskCounts,
  type RiskTier,
} from '../../../lib/nd/risk-priority-score';
import { COMPLIANCE_SEVERITY_LABELS } from '../../../lib/nd/point-compliance-status';
import type { DashboardMetricId } from '../../../lib/nd/dashboard-metric';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdRunHistoryPanelComponent } from '../../components/nd/nd-run-history-panel.component';
import { NdRunTableActionsComponent } from '../../components/nd/nd-run-table-actions.component';
import { NdMisPanelComponent } from '../../components/nd/nd-mis-panel.component';
import { NdRunRoleBadgeComponent } from '../../components/nd/nd-run-role-badge.component';
import {
  buildReportStats,
  type ReportStats,
} from '../../../lib/ai-lab/parse-compliance-results';
import {
  parsedResultsFromReport,
  progressPointToReportItem,
} from '../../../lib/dual-verify-report';
import type { DualVerifyAgreement } from '../../../lib/landing-ai/dual-verify-merge';

function normalizeDashboardMetrics(raw: DashboardMetrics & Record<string, unknown>): DashboardMetrics {
  const legacyBreakdown = Array.isArray(raw['riskBreakdown'])
    ? (raw['riskBreakdown'] as { name: string; value: number; color: string }[])
        .filter((r) => /compliant|partial|non/i.test(r.name))
    : [];
  return {
    compliant: Number(raw.compliant ?? raw['compliantItems'] ?? 0),
    partial: Number(raw.partial ?? 0),
    nonCompliant: Number(raw.nonCompliant ?? 0),
    totalFindings: Number(raw.totalFindings ?? 0),
    lastAnalysisDate: String(raw.lastAnalysisDate ?? ''),
    complianceBreakdown:
      raw.complianceBreakdown?.length
        ? raw.complianceBreakdown
        : legacyBreakdown.map((r) => ({
            name: r.name,
            value: r.value,
            color: r.color,
          })),
    recentAnalyses: (raw.recentAnalyses ?? []).map((a) => {
      const row = a as Record<string, unknown>;
      return {
        id: String(row['id'] ?? ''),
        title: String(row['title'] ?? ''),
        date: String(row['date'] ?? ''),
        findings: Number(row['findings'] ?? 0),
        compliant: Number(row['compliant'] ?? 0),
        partial: Number(row['partial'] ?? 0),
        nonCompliant: Number(row['nonCompliant'] ?? 0),
      };
    }),
  };
}

type RecentRow = {
  id: string;
  title: string;
  date: string;
  findings: number;
  compliant: number;
  partial: number;
  nonCompliant: number;
  kind: 'kafka' | 'sync' | 'demo' | 'nd';
  status?: string;
  routerLink?: string[];
  queryParams: Record<string, string>;
};

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    NdStatusBadgeComponent,
    NdRunHistoryPanelComponent,
    NdRunRoleBadgeComponent,
    NdRunTableActionsComponent,
    NdMisPanelComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss', '../nd/nd-shared.scss'],
})
export class DashboardComponent implements OnInit {
  readonly apiUrl = environment.apiUrl;
  readonly complianceLabels = COMPLIANCE_SEVERITY_LABELS;

  seed: DashboardMetrics | null = null;
  health: DualVerifyHealth | null = null;
  sessions: DualVerifySessionSummary[] = [];
  sessionStats: ReportStats | null = null;

  healthLoading = true;
  metricsLoading = true;
  sessionsLoading = true;
  statsLoading = false;

  healthError: string | null = null;
  metricsError: string | null = null;
  sessionsError: string | null = null;

  ndRuns: AnalysisRunSummary[] = [];
  ndRunsTotal = 0;
  ndRunsLoading = false;
  ndRunsLoadError = '';
  ndDashboardStats: NdDashboardStats | null = null;
  ndStatsLoading = false;
  gapRiskCounts: GapRiskCounts = emptyGapRiskCounts();
  gapRiskLoading = false;
  historyOpen = false;
  historyRunId: string | null = null;
  historyRunName = '';
  historyRunStats: RunGapStatsSummary | null = null;

  workflowLabel = analysisRunWorkflowLabel;
  formatDate = formatDate;

  private readonly seededComplianceId = 'a339de5e-06b9-4067-bd97-e7d8086bf31e';
  private readonly sessionRequestTimeoutMs = 12_000;

  constructor(
    private api: ApiService,
    private ndApi: NdApiService,
    private ndAuth: NdAuthService,
    private router: Router,
  ) {}

  get inNdShell(): boolean {
    return this.router.url.startsWith('/nd');
  }

  ngOnInit(): void {
    if (this.inNdShell) {
      this.ndRunsLoading = true;
      // ND overview uses analysis-runs summaries — skip legacy dual-verify/demo loads that
      // compete for the same Supabase connection pool and often 500 on missing seed data.
      void this.loadNdRuns();
      return;
    }
    this.loadHealth();
    this.loadMetrics();
    this.loadSessions();
  }

  private loadHealth(): void {
    this.healthLoading = true;
    this.healthError = null;
    this.api
      .getDualVerifyHealth()
      .pipe(finalize(() => (this.healthLoading = false)))
      .subscribe({
        next: (r) => {
          this.health = r.data;
        },
        error: () => {
          this.healthError =
            `Cannot reach the API at ${environment.apiUrl}. Start it with: cd bcp-api; .\\scripts\\restart-api.ps1 — then refresh.`;
        },
      });
  }

  private loadMetrics(): void {
    this.metricsLoading = true;
    this.metricsError = null;
    this.api
      .getDashboard()
      .pipe(finalize(() => (this.metricsLoading = false)))
      .subscribe({
        next: (r) => {
          this.seed = normalizeDashboardMetrics(
            r.data as DashboardMetrics & Record<string, unknown>,
          );
        },
        error: () => {
          this.metricsError = 'Metrics unavailable';
          this.seed = null;
        },
      });
  }

  /** Runs with at least one scored compliance point. */
  private get ndRunsWithMetrics(): AnalysisRunSummary[] {
    return this.ndRuns.filter((r) => {
      const total = (r.compliant ?? 0) + (r.partial ?? 0) + (r.nonCompliant ?? 0);
      return total > 0;
    });
  }

  /** Sum compliant / partial / non-compliant across all ND analysis runs. */
  private get aggregatedNdMetrics(): { compliant: number; partial: number; nonCompliant: number } {
    let compliant = 0;
    let partial = 0;
    let nonCompliant = 0;
    for (const run of this.ndRunsWithMetrics) {
      compliant += run.compliant ?? 0;
      partial += run.partial ?? 0;
      nonCompliant += run.nonCompliant ?? 0;
    }
    return { compliant, partial, nonCompliant };
  }

  /** Latest ND run with compliance breakdown (for links / last analysis date). */
  private get primaryNdRun(): AnalysisRunSummary | null {
    const withMetrics = this.ndRunsWithMetrics;
    if (!this.inNdShell || !withMetrics.length) return null;
    return [...withMetrics].sort(
      (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    )[0];
  }

  private async loadNdRuns(): Promise<void> {
    this.ndRunsLoading = true;
    this.ndRunsLoadError = '';
    void this.loadNdDashboardStats();
    try {
      if (!this.ndAuth.profile()) {
        await this.ndAuth.refreshProfile();
      }
      const role = this.ndAuth.getRole();
      const listParams = { ndOnly: true, summaryOnly: true, skipGapStats: true };
      const res = await this.ndApi.getAnalysisRuns(
        role === 'maker' ? { ...listParams, mineOnly: true } : listParams,
      );
      if (res.success && res.data) {
        this.ndRuns = sortAnalysisRunsByRecent(res.data as AnalysisRunSummary[]);
        if (!this.ndDashboardStats) {
          this.ndRunsTotal = this.ndRuns.length;
        }
      } else {
        this.ndRuns = [];
        this.ndRunsLoadError = res.message ?? 'Could not load analysis runs from the API.';
      }
    } catch {
      this.ndRuns = [];
      this.ndRunsLoadError = 'Could not load analysis runs from the API.';
    } finally {
      this.ndRunsLoading = false;
    }
  }

  private async loadNdDashboardStats(): Promise<void> {
    if (!this.inNdShell) return;
    this.ndStatsLoading = true;
    this.gapRiskLoading = true;
    try {
      if (!this.ndAuth.profile()) {
        await this.ndAuth.refreshProfile();
      }
      const role = this.ndAuth.getRole();
      const res = await this.ndApi.getDashboardStats(role === 'maker' ? { mineOnly: true } : undefined);
      if (res.success && res.data) {
        const stats = res.data;
        this.ndDashboardStats = stats;
        this.ndRunsTotal = stats.totalRuns;
        this.gapRiskCounts = mergeGapRiskCounts(emptyGapRiskCounts(), {
          critical: stats.criticalGaps,
          medium: stats.mediumGaps,
          low: stats.lowGaps,
          total: stats.criticalGaps + stats.mediumGaps + stats.lowGaps,
        });
      }
    } catch {
      this.ndDashboardStats = null;
      this.gapRiskCounts = emptyGapRiskCounts();
    } finally {
      this.ndStatsLoading = false;
      this.gapRiskLoading = false;
    }
  }

  readonly riskStandardLabel = RISK_STANDARD_SUMMARY;

  private withSessionTimeout<T>(obs: Observable<T>, fallback: T) {
    return obs.pipe(
      timeout(this.sessionRequestTimeoutMs),
      catchError(() => of(fallback)),
    );
  }

  private loadSessions(): void {
    this.sessionsLoading = true;
    this.sessionsError = null;
    forkJoin({
      kafka: this.withSessionTimeout(this.api.listDualVerifySessions(), {
        success: true,
        data: [],
      }),
      kafkaNest: this.withSessionTimeout(this.api.listNestDualVerifySessions(), {
        success: true,
        data: [],
      }),
      compliance: this.withSessionTimeout(this.api.listComplianceSessions('dual-leaf', 10), {
        success: true,
        sessions: [],
      }),
    })
      .pipe(finalize(() => (this.sessionsLoading = false)))
      .subscribe({
        next: ({ kafka, kafkaNest, compliance }) => {
          const merged: DualVerifySessionSummary[] = [
            ...(kafkaNest.data ?? []),
            ...(kafka.data ?? []),
          ];
          const seen = new Set(merged.map((s) => s.id));
          for (const c of compliance.sessions ?? []) {
            if (c.source === 'compare_cache' || seen.has(c.id)) continue;
            seen.add(c.id);
            merged.push({
              id: c.id,
              status: 'saved',
              granularity: c.granularity ?? 'dual-leaf',
              totalPoints: c.comparedPoints,
              completedPoints: c.comparedPoints,
              failedPoints: 0,
              phase2Model: 'saved',
              transport: 'db',
              updatedAt: c.updatedAt ?? c.label,
              label: c.label,
            });
          }
          this.sessions = merged;
          this.loadLatestSessionStats();
        },
        error: () => {
          this.sessionsError = 'Sessions unavailable';
          this.sessions = [];
        },
      });
  }

  get criticalGapCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return -1;
      return this.gapRiskCounts.critical;
    }
    return this.criticalCount;
  }

  get mediumGapCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return -1;
      return this.gapRiskCounts.medium;
    }
    return this.highCount;
  }

  get lowGapCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return -1;
      return this.gapRiskCounts.low;
    }
    return 0;
  }

  get criticalGapResolved(): number {
    return this.ndDashboardStats?.criticalGapsResolved ?? 0;
  }

  get criticalGapPending(): number {
    return this.ndDashboardStats?.criticalGapsPending ?? 0;
  }

  get mediumGapResolved(): number {
    return this.ndDashboardStats?.mediumGapsResolved ?? 0;
  }

  get mediumGapPending(): number {
    return this.ndDashboardStats?.mediumGapsPending ?? 0;
  }

  get lowGapResolved(): number {
    return this.ndDashboardStats?.lowGapsResolved ?? 0;
  }

  get lowGapPending(): number {
    return this.ndDashboardStats?.lowGapsPending ?? 0;
  }

  get totalActionsCount(): number {
    if (this.ndStatsLoading) return -1;
    return this.ndDashboardStats?.totalActions ?? 0;
  }

  get totalActionsResolved(): number {
    return this.ndDashboardStats?.totalActionsResolved ?? 0;
  }

  get totalActionsPending(): number {
    return this.ndDashboardStats?.totalActionsPending ?? 0;
  }

  get criticalCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      return this.ndDashboardStats?.nonCompliant ?? 0;
    }
    const nd = this.primaryNdRun;
    if (nd) return nd.nonCompliant ?? 0;
    return this.nonCompliantCount > 0 ? Math.min(2, this.nonCompliantCount) : 2;
  }

  get highCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      return this.ndDashboardStats?.partial ?? 0;
    }
    const nd = this.primaryNdRun;
    if (nd) return nd.partial ?? 0;
    return this.partialCount > 0 ? Math.min(3, this.partialCount) : 3;
  }

  get mediumCount(): number {
    if (this.primaryNdRun) return 0;
    return 2;
  }

  get lowCount(): number {
    if (this.primaryNdRun) return 0;
    return 1;
  }

  get compliantCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      return this.ndDashboardStats?.compliant ?? 0;
    }
    const nd = this.primaryNdRun;
    if (nd) return nd.compliant ?? 0;
    return this.sessionStats?.compliant ?? this.seed?.compliant ?? 3;
  }

  get partialCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      return this.ndDashboardStats?.partial ?? 0;
    }
    const nd = this.primaryNdRun;
    if (nd) return nd.partial ?? 0;
    return this.sessionStats?.partial ?? this.seed?.partial ?? 0;
  }

  get nonCompliantCount(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      return this.ndDashboardStats?.nonCompliant ?? 0;
    }
    const nd = this.primaryNdRun;
    if (nd) return nd.nonCompliant ?? 0;
    return this.sessionStats?.nonCompliant ?? this.seed?.nonCompliant ?? 0;
  }

  get totalFindings(): number {
    if (this.inNdShell) {
      if (this.ndStatsLoading) return 0;
      const stats = this.ndDashboardStats;
      if (!stats) return 0;
      return stats.compliant + stats.partial + stats.nonCompliant;
    }
    const nd = this.primaryNdRun;
    if (nd) {
      return (nd.compliant ?? 0) + (nd.partial ?? 0) + (nd.nonCompliant ?? 0);
    }
    const fromStats = this.sessionStats?.total ?? 0;
    const fromSeed = this.seed?.totalFindings ?? 0;
    const sum = this.criticalCount + this.highCount + this.mediumCount + this.lowCount + this.compliantCount;
    return Math.max(fromStats, fromSeed, sum, 11);
  }

  get analysisCountLabel(): string {
    if (this.inNdShell && this.ndStatsLoading) return '…';
    if (this.inNdShell) return String(this.ndRunsTotal || this.ndRuns.length);
    const ndCount = this.ndRuns.filter(
      (r) => (r.compliant ?? 0) + (r.partial ?? 0) + (r.nonCompliant ?? 0) > 0,
    ).length;
    if (ndCount > 0) return String(ndCount);
    return '1';
  }

  get lastAnalysisLabel(): string {
    if (this.inNdShell && this.ndStatsLoading) return 'Loading…';
    if (this.inNdShell && this.ndDashboardStats?.lastAnalysisAt) {
      return new Date(this.ndDashboardStats.lastAnalysisAt).toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    }
    const nd = this.primaryNdRun ?? this.ndRuns[0];
    if (nd?.createdAt) {
      return new Date(nd.createdAt).toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    }
    if (this.inNdShell) return '—';
    if (this.metricsLoading) return 'Loading…';
    return this.seed?.lastAnalysisDate || 'June 22, 2026';
  }

  get compliantPercent(): number {
    const total = this.complianceDonutTotal || 1;
    return Math.round((this.compliantCount / total) * 100);
  }

  get complianceDonutTotal(): number {
    return Math.max(this.compliantCount + this.partialCount + this.nonCompliantCount, 1);
  }

  get complianceDonutSegments(): { color: string; value: number; offset: number }[] {
    const items = [
      { color: '#3b82f6', value: this.compliantCount },
      { color: '#f97316', value: this.partialCount },
      { color: '#ef4444', value: this.nonCompliantCount },
    ];
    const total = items.reduce((s, i) => s + i.value, 0) || 1;
    let offset = 0;
    return items.map((item) => {
      const seg = { ...item, offset };
      offset += (item.value / total) * 100;
      return seg;
    });
  }

  get recentNdRuns(): AnalysisRunSummary[] {
    return this.ndRuns.slice(0, 5);
  }

  /** Runs with compliance breakdown on the summary row. */
  get ndRunsWithMetricsCount(): number {
    if (this.inNdShell && this.ndDashboardStats) {
      const total =
        this.ndDashboardStats.compliant +
        this.ndDashboardStats.partial +
        this.ndDashboardStats.nonCompliant;
      return total > 0 ? 1 : 0;
    }
    return this.ndRuns.filter(
      (r) => (r.compliant ?? 0) + (r.partial ?? 0) + (r.nonCompliant ?? 0) > 0,
    ).length;
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.ndAuth.getRole());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.ndAuth.getRole());
  }

  isLegacy(run: AnalysisRunSummary): boolean {
    return isLegacyAnalysisRun(run);
  }

  openHistory(run: AnalysisRunSummary, event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    if (this.isLegacy(run)) return;
    this.historyRunId = run.id;
    this.historyRunName = run.name;
    this.historyRunStats = runGapStatsFromSummary(run);
    this.historyOpen = true;
  }

  closeHistory(): void {
    this.historyOpen = false;
    this.historyRunId = null;
    this.historyRunName = '';
    this.historyRunStats = null;
  }

  get recentRows(): RecentRow[] {
    const rows: RecentRow[] = [];
    const seen = new Set<string>();

    for (const run of this.ndRuns) {
      const target = ndAnalysisRunTarget(run, this.ndAuth.getRole());
      seen.add(run.id);
      rows.push({
        id: run.id,
        title: run.name || 'Analysis run',
        date: (run.createdAt ?? '').slice(0, 10),
        findings: run.totalPointsCount ?? 0,
        compliant: run.compliant ?? 0,
        partial: run.partial ?? 0,
        nonCompliant: run.nonCompliant ?? run.dualVerifyFailedCount ?? 0,
        kind: 'nd',
        status: run.status,
        routerLink: target.routerLink,
        queryParams: target.queryParams ?? {},
      });
    }

    // Prefer compliance analyses (real seeded / saved runs) first.
    for (const a of this.seed?.recentAnalyses ?? []) {
      if (seen.has(a.id)) continue;
      if (a.id.includes('demo')) continue;
      if (a.compliant + a.partial + a.nonCompliant === 0 && a.findings === 0) continue;
      rows.push({
        id: a.id,
        title: a.title || 'I M P T F S × TFS Guidelines',
        date: a.date,
        findings: a.findings,
        compliant: a.compliant,
        partial: a.partial,
        nonCompliant: a.nonCompliant,
        kind: 'sync',
        queryParams: { saved: `compliance:${a.id}` },
      });
    }

    for (const s of this.sessions.slice(0, 8)) {
      if ((s.completedPoints || 0) === 0 && (s.failedPoints || 0) === 0) continue;
      // Skip tiny smoke-test sessions when we already have a full compliance run.
      if (rows.some((r) => r.findings >= 30) && (s.completedPoints || 0) < 30) continue;
      rows.push({
        id: s.id,
        title: s.label || 'Dual-verify session',
        date: (s.updatedAt ?? '').slice(0, 10) || '',
        findings: s.completedPoints || 0,
        compliant: 0,
        partial: 0,
        nonCompliant: s.failedPoints,
        kind: 'kafka',
        status: s.status,
        queryParams: this.sessionRecordQuery(s),
      });
    }

    if (rows.length === 0) {
      rows.push({
        id: 'empty',
        title: 'No analyses yet',
        date: '',
        findings: 0,
        compliant: 0,
        partial: 0,
        nonCompliant: 0,
        kind: 'sync',
        queryParams: { saved: `compliance:${this.seededComplianceId}` },
      });
    }

    return rows;
  }

  sessionRecordQuery(s: DualVerifySessionSummary): Record<string, string> {
    if (s.transport === 'db' || s.status === 'saved') {
      return { saved: `compliance:${s.id}` };
    }
    return { session: s.id };
  }

  private loadLatestSessionStats(): void {
    const latest = [...this.sessions]
      .filter((s) => s.completedPoints > 0 && s.transport !== 'db' && s.status === 'completed')
      .sort((a, b) => (b.completedPoints ?? 0) - (a.completedPoints ?? 0))[0];
    // Prefer API metrics from the seeded 32-pt compliance run when dual-verify is a partial smoke test.
    if (!latest || latest.completedPoints < 30) {
      this.loadStatsFromCompliance(this.seededComplianceId);
      return;
    }

    this.statsLoading = true;
    this.api
      .getJob(latest.id)
      .pipe(finalize(() => (this.statsLoading = false)))
      .subscribe({
        next: (r) => {
          const items = (r.data?.points ?? [])
            .filter((p) => p.status === 'completed' && (p.landingMessage || p.llmMessage))
            .map((p) =>
              progressPointToReportItem({
                pointId: p.pointId,
                pointTitle: p.pointTitle,
                status: p.status,
                landingMessage: p.landingMessage,
                llmMessage: p.llmMessage,
                agreementJson: p.agreementJson as DualVerifyAgreement | undefined,
                errorMessage: p.errorMessage,
              }),
            );
          if (!items.length) {
            this.loadStatsFromCompliance(this.seededComplianceId);
            return;
          }
          this.sessionStats = buildReportStats(parsedResultsFromReport(items, 'llm'));
        },
        error: () => this.loadStatsFromCompliance(this.seededComplianceId),
      });
  }

  private loadStatsFromCompliance(id: string): void {
    this.statsLoading = true;
    this.api
      .loadComplianceSession(id)
      .pipe(finalize(() => (this.statsLoading = false)))
      .subscribe({
        next: (r) => {
          const items = ((r.results as Record<string, unknown>[]) ?? [])
            .map((row) =>
              progressPointToReportItem({
                pointId: String(row['point_id'] ?? ''),
                pointTitle: String(row['title'] ?? ''),
                status: 'completed',
                landingMessage: String(row['landingMessage'] ?? row['message'] ?? ''),
                llmMessage: String(row['llmMessage'] ?? ''),
                agreementJson: row['agreementJson'] as DualVerifyAgreement | undefined,
              }),
            )
            .filter((i) => i.pointId && (i.landingMessage || i.llmMessage));
          if (!items.length) return;
          this.sessionStats = buildReportStats(parsedResultsFromReport(items, 'llm'));
        },
        error: () => {
          /* keep seed metrics */
        },
      });
  }

  severityClass(name: string): string {
    return name.toLowerCase();
  }

  /** Drill into analyses that contribute to this dashboard card total. */
  openMetricBreakdown(metric: DashboardMetricId): void {
    if (this.inNdShell) {
      void this.router.navigate(['/nd/overview/metric'], {
        queryParams: { metric },
      });
      return;
    }
    // Legacy shell: jump straight into the preferred report with a filter.
    if (metric === 'critical' || metric === 'medium' || metric === 'low') {
      this.openByRiskTier(metric);
      return;
    }
    if (metric === 'findings') {
      this.openBySeverity('all');
      return;
    }
    if (metric === 'partial') {
      this.openBySeverity('partial');
      return;
    }
    if (metric === 'non_compliant') {
      this.openBySeverity('non-compliant');
      return;
    }
    this.openBySeverity('compliant');
  }

  openByRiskTier(tier: RiskTier): void {
    this.router.navigate(shellRouteSegments(this.router, '/gap-analysis'), {
      queryParams: {
        ...this.preferredReportQuery(),
        riskTier: tier,
      },
    });
  }

  openBySeverity(severity: string): void {
    this.router.navigate(shellRouteSegments(this.router, '/gap-analysis'), {
      queryParams: {
        ...this.preferredReportQuery(),
        ...(severity === 'all' ? {} : { filter: severity }),
      },
    });
  }

  gapAnalysisPath(): string {
    return shellRoute(this.router, '/gap-analysis');
  }

  viewAllAnalysesPath(): string {
    return this.inNdShell
      ? shellRoute(this.router, '/analysis-runs')
      : shellRoute(this.router, '/gap-analysis');
  }

  recentRowLink(row: RecentRow): string[] {
    return row.routerLink ?? [this.gapAnalysisPath()];
  }

  recentAnalysesLoading(): boolean {
    if (this.inNdShell) {
      return this.ndRunsLoading;
    }
    return (this.sessionsLoading || this.metricsLoading) && this.recentRows.length === 0;
  }

  preferredReportQueryParams(): Record<string, string> {
    return this.preferredReportQuery();
  }

  /** Prefer live ND run, then seeded / richest compliance session. */
  private preferredReportQuery(): Record<string, string> {
    const nd = this.primaryNdRun;
    if (this.inNdShell && nd) return { run: nd.id };

    const seededId = this.seededComplianceId;
    const fromSeed = this.seed?.recentAnalyses?.find((a) => a.id === seededId);
    if (fromSeed) return { saved: `compliance:${seededId}` };

    const richest = [...(this.seed?.recentAnalyses ?? [])]
      .filter((a) => !a.id.includes('demo') && a.findings > 0)
      .sort((a, b) => b.findings - a.findings)[0];
    if (richest) return { saved: `compliance:${richest.id}` };

    const dual = [...this.sessions]
      .filter((s) => s.status === 'completed' && s.completedPoints > 0 && s.transport !== 'db')
      .sort((a, b) => (b.completedPoints ?? 0) - (a.completedPoints ?? 0))[0];
    if (dual && (dual.completedPoints ?? 0) >= 30) return this.sessionRecordQuery(dual);

    return { saved: `compliance:${seededId}` };
  }
}
