import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import { sortAnalysisRunsByRecent } from '../../../../lib/nd/analysis-run-status';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';
import {
  DASHBOARD_METRICS,
  gapAnalysisQueryForMetric,
  parseDashboardMetricId,
  type DashboardMetricId,
} from '../../../../lib/nd/dashboard-metric';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';

type MetricRunRow = {
  run: AnalysisRunSummary;
  count: number;
};

@Component({
  selector: 'app-nd-dashboard-metric-runs',
  standalone: true,
  imports: [CommonModule, RouterLink, NdStatusBadgeComponent],
  templateUrl: './nd-dashboard-metric-runs.component.html',
  styleUrls: ['./nd-dashboard-metric-runs.component.scss', '../nd-shared.scss'],
})
export class NdDashboardMetricRunsComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);

  readonly formatDate = formatDate;

  metricId: DashboardMetricId | null = null;
  loading = true;
  loadError = '';
  rows: MetricRunRow[] = [];
  private sub: Subscription | null = null;

  get metricLabel(): string {
    return this.metricId ? DASHBOARD_METRICS[this.metricId].label : 'Metric';
  }

  get metricHint(): string {
    return this.metricId ? DASHBOARD_METRICS[this.metricId].hint : '';
  }

  get countColumnLabel(): string {
    return this.metricId ? DASHBOARD_METRICS[this.metricId].countLabel : 'Count';
  }

  get totalCount(): number {
    return this.rows.reduce((s, r) => s + r.count, 0);
  }

  ngOnInit(): void {
    this.sub = this.route.queryParamMap.subscribe((params) => {
      this.metricId = parseDashboardMetricId(params.get('metric'));
      void this.load();
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  async load(): Promise<void> {
    if (!this.metricId) {
      this.loading = false;
      this.loadError = 'Unknown dashboard metric.';
      this.rows = [];
      return;
    }

    this.loading = true;
    this.loadError = '';
    this.rows = [];
    try {
      await this.auth.refreshProfile();
      const role = this.auth.getRole();
      // Same runs and the same tallies as the overview card (server side), across every analysis - so the
      // rows add up to exactly the number on the card.
      const res = await this.api.getDashboardMetricRunCounts(
        this.metricId,
        role === 'maker' ? { mineOnly: true } : undefined,
      );
      if (!res.success || !res.data) {
        this.loadError = res.message ?? 'Could not load analysis runs.';
        return;
      }
      const counts = new Map(res.data.counts.map((c) => [c.runId, c.count]));
      this.rows = sortAnalysisRunsByRecent(res.data.runs as AnalysisRunSummary[])
        .map((run) => ({ run, count: counts.get(run.id) ?? 0 }))
        .filter((r) => r.count > 0);
    } catch {
      this.loadError = 'Could not load analysis breakdown.';
      this.rows = [];
    } finally {
      this.loading = false;
    }
  }

  openRun(row: MetricRunRow): void {
    if (!this.metricId) return;
    void this.router.navigate(['/nd/gap-analysis'], {
      queryParams: gapAnalysisQueryForMetric(row.run.id, this.metricId),
    });
  }
}
