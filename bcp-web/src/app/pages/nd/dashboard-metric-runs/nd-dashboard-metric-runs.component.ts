import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import { sortAnalysisRunsByRecent } from '../../../../lib/nd/analysis-run-status';
import type { AnalysisPoint, AnalysisRunSummary } from '../../../../lib/nd/types';
import {
  aggregateGapRiskCounts,
  type ActionItemReviewRow,
} from '../../../../lib/nd/dashboard-gap-risk';
import {
  DASHBOARD_METRICS,
  complianceCountForRun,
  gapAnalysisQueryForMetric,
  isRiskTierMetric,
  parseDashboardMetricId,
  riskCountFromGaps,
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
      const listParams = { ndOnly: true, summaryOnly: true, page: 1, pageSize: 20 };
      const res = await this.api.getAnalysisRuns(
        role === 'maker' ? { ...listParams, mineOnly: true } : listParams,
      );
      if (!res.success || !res.data) {
        this.loadError = res.message ?? 'Could not load analysis runs.';
        return;
      }

      const runs = sortAnalysisRunsByRecent(res.data as AnalysisRunSummary[]);
      const metric = this.metricId;

      if (isRiskTierMetric(metric)) {
        // Prefer server gap-risk tallies (same as overview) — avoid N× full getResults.
        const fromSummary = runs
          .map((run) => ({
            run,
            count: riskCountFromGaps(
              {
                critical: run.criticalGaps ?? 0,
                medium: run.mediumGaps ?? 0,
                low: run.lowGaps ?? 0,
                total:
                  (run.criticalGaps ?? 0) + (run.mediumGaps ?? 0) + (run.lowGaps ?? 0),
              },
              metric,
            ),
          }))
          .filter((r) => r.count > 0);
        const hasServer =
          runs.some((r) => r.criticalGaps != null || r.mediumGaps != null || r.lowGaps != null);
        if (hasServer) {
          this.rows = fromSummary;
        } else {
          const candidates = runs.filter(
            (r) =>
              (r.compliant ?? 0) + (r.partial ?? 0) + (r.nonCompliant ?? 0) > 0 ||
              (r.processedPointsCount ?? 0) > 0,
          );
          const scored: MetricRunRow[] = [];
          for (const run of candidates) {
            const detail = await this.api.getResults(run.id);
            if (!detail.success || !detail.data) continue;
            const data = detail.data as {
              points?: AnalysisPoint[];
              actionItemReviews?: ActionItemReviewRow[];
            };
            const gaps = aggregateGapRiskCounts(
              data.points ?? [],
              data.actionItemReviews ?? [],
            );
            const count = riskCountFromGaps(gaps, metric);
            if (count > 0) scored.push({ run, count });
          }
          this.rows = scored;
        }
      } else {
        this.rows = runs
          .map((run) => ({ run, count: complianceCountForRun(run, metric) }))
          .filter((r) => r.count > 0);
      }
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
