import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import {
  ApiService,
  DashboardMetrics,
  DualVerifyHealth,
  DualVerifySessionSummary,
} from '../../services/api.service';
import { complianceKeyFromBreakdownName } from '../../../lib/dual-verify-workflow';
import type { ComplianceStatusFilter } from '../../../lib/dual-verify-workflow';
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
  kind: 'kafka' | 'sync' | 'demo';
  status?: string;
  queryParams: Record<string, string>;
};

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit {
  seed: DashboardMetrics | null = null;
  health: DualVerifyHealth | null = null;
  sessions: DualVerifySessionSummary[] = [];
  sessionStats: ReportStats | null = null;
  loading = true;

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    forkJoin({
      dash: this.api.getDashboard(),
      health: this.api.getDualVerifyHealth(),
      kafka: this.api.listDualVerifySessions().pipe(catchError(() => of({ data: [] }))),
      kafkaNest: this.api.listNestDualVerifySessions().pipe(catchError(() => of({ data: [] }))),
      compliance: this.api.listComplianceSessions('dual-leaf', 10).pipe(catchError(() => of({ sessions: [] }))),
    }).subscribe({
      next: ({ dash, health, kafka, kafkaNest, compliance }) => {
        this.seed = normalizeDashboardMetrics(dash.data as DashboardMetrics & Record<string, unknown>);
        this.health = health.data;
        const merged: DualVerifySessionSummary[] = [...(kafkaNest.data ?? []), ...(kafka.data ?? [])];
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
        this.loading = false;
        this.loadLatestSessionStats();
      },
      error: () => (this.loading = false),
    });
  }

  get compliantCount(): number {
    return this.sessionStats?.compliant ?? this.seed?.compliant ?? 0;
  }

  get partialCount(): number {
    return this.sessionStats?.partial ?? this.seed?.partial ?? 0;
  }

  get nonCompliantCount(): number {
    return this.sessionStats?.nonCompliant ?? this.seed?.nonCompliant ?? 0;
  }

  get totalFindings(): number {
    const fromStats = this.sessionStats?.total ?? 0;
    const fromSeed = this.seed?.totalFindings ?? 0;
    const sum = this.compliantCount + this.partialCount + this.nonCompliantCount;
    return Math.max(fromStats, fromSeed, sum);
  }

  get hasComplianceMetrics(): boolean {
    return this.totalFindings > 0;
  }

  get complianceBreakdown(): { name: string; value: number; color: string }[] {
    if (this.sessionStats) {
      const rows: { name: string; value: number; color: string }[] = [];
      if (this.sessionStats.compliant > 0) {
        rows.push({ name: 'Compliant', value: this.sessionStats.compliant, color: '#22c55e' });
      }
      if (this.sessionStats.partial > 0) {
        rows.push({ name: 'Partial', value: this.sessionStats.partial, color: '#eab308' });
      }
      if (this.sessionStats.nonCompliant > 0) {
        rows.push({ name: 'Non-compliant', value: this.sessionStats.nonCompliant, color: '#ef4444' });
      }
      if (rows.length) return rows;
    }
    return (this.seed?.complianceBreakdown ?? []).filter((r) => r.value > 0);
  }

  private loadLatestSessionStats(): void {
    const latest = this.sessions.find(
      (s) => s.completedPoints > 0 && s.transport !== 'db' && s.status === 'completed',
    );
    if (!latest) return;

    this.api.getJob(latest.id).subscribe({
      next: (r) => {
        const items = (r.data?.points ?? [])
          .filter((p) => p.status === 'completed' && p.landingMessage && p.llmMessage)
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
        if (!items.length) return;
        this.sessionStats = buildReportStats(parsedResultsFromReport(items, 'llm'));
      },
      error: () => {
        /* dashboard still works from API metrics */
      },
    });
  }

  get persistenceMode(): string {
    return this.health?.persistence?.mode ?? 'memory';
  }

  get completedPoints(): number {
    return this.sessions.reduce((n, s) => n + s.completedPoints, 0);
  }

  get activeJobs(): number {
    return this.sessions.filter((s) =>
      ['running', 'queued', 'processing'].includes(s.status),
    ).length;
  }

  get firstActiveSession(): DualVerifySessionSummary | undefined {
    return this.sessions.find((s) =>
      ['running', 'queued', 'processing'].includes(s.status),
    );
  }

  get latestSession(): DualVerifySessionSummary | undefined {
    return this.sessions[0];
  }

  get recentRows(): RecentRow[] {
    const rows: RecentRow[] = this.sessions.slice(0, 8).map((s) => ({
      id: s.id,
      title: `Dual verify · ${s.granularity}`,
      date: (s.updatedAt ?? '').slice(0, 16).replace('T', ' '),
      findings: s.completedPoints,
      compliant: 0,
      partial: 0,
      nonCompliant: s.failedPoints,
      kind: 'kafka' as const,
      status: s.status,
      queryParams: this.sessionRecordQuery(s),
    })).filter((r) => r.findings > 0 || r.nonCompliant > 0);

    for (const a of this.seed?.recentAnalyses ?? []) {
      const isDemo = a.id.includes('demo');
      if (a.compliant + a.partial + a.nonCompliant === 0 && a.findings === 0) continue;
      rows.push({
        id: a.id,
        title: a.title,
        date: a.date,
        findings: a.findings,
        compliant: a.compliant,
        partial: a.partial,
        nonCompliant: a.nonCompliant,
        kind: isDemo ? 'demo' : 'sync',
        queryParams: { saved: `compliance:${a.id}` },
      });
    }
    return rows;
  }

  complianceKey(name: string): ComplianceStatusFilter | null {
    return complianceKeyFromBreakdownName(name);
  }

  analysisQuery(compliance?: ComplianceStatusFilter): Record<string, string> {
    const q = this.latestSession ? this.sessionRecordQuery(this.latestSession) : {};
    if (compliance) return { ...q, compliance };
    return q;
  }

  sessionRecordQuery(s: DualVerifySessionSummary): Record<string, string> {
    if (s.transport === 'db' || s.status === 'saved') {
      return { saved: `compliance:${s.id}` };
    }
    return { session: s.id };
  }

  recordQuery(sessionId?: string | null): Record<string, string> {
    if (sessionId) return { session: sessionId };
    return {};
  }
}
