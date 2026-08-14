import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs/operators';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdRunHistoryPanelComponent } from '../../../components/nd/nd-run-history-panel.component';
import { NdRunTableActionsComponent } from '../../../components/nd/nd-run-table-actions.component';
import { NdRunRoleBadgeComponent } from '../../../components/nd/nd-run-role-badge.component';
import { formatDate } from '../../../../lib/nd/utils';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery } from '../../../../lib/nd/run-links';
import { ndNewAnalysisRoute } from '../../../../lib/nd/demo-analysis-routes';
import { analysisRunWorkflowLabel, sortAnalysisRunsByRecent } from '../../../../lib/nd/analysis-run-status';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';
import { runGapStatsFromSummary, type RunGapStatsSummary } from '../../../../lib/nd/run-gap-stats';
import {
  ACTION_PLAN_PRIORITY_OPTIONS,
  normalizeActionPlanPriority,
  type ActionPlanPriority,
} from '../../../../lib/nd/action-plan';

type ActionPlanSummary = {
  total: number;
  pending: number;
  resolved: number;
  overdue: number;
  byPriority: { priority: string; total: number; pending: number; resolved: number; overdue: number; runCount: number }[];
};

type PriorityBucket = {
  priority: ActionPlanPriority;
  label: string;
  total: number;
  pending: number;
  resolved: number;
  overdue: number;
  runCount: number;
};

@Component({
  selector: 'app-nd-overview',
  standalone: true,
  imports: [CommonModule, RouterLink, NdStatusBadgeComponent, NdRunHistoryPanelComponent, NdRunRoleBadgeComponent, NdRunTableActionsComponent],
  templateUrl: './nd-overview.component.html',
  styleUrls: ['./nd-overview.component.scss', '../nd-shared.scss'],
})
export class NdOverviewComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly router = inject(Router);
  readonly auth = inject(NdAuthService);

  loading = true;
  recentRuns: AnalysisRunSummary[] = [];
  welcome = '';
  historyOpen = false;
  historyRunId: string | null = null;
  historyRunName = '';
  historyRunStats: RunGapStatsSummary | null = null;
  actionPlanSummary: ActionPlanSummary | null = null;
  priorityBuckets: PriorityBucket[] = [];

  async ngOnInit(): Promise<void> {
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe((e) => {
        if (e.urlAfterRedirects.startsWith('/nd/overview')) void this.load();
      });

    await this.load();
  }

  private async load(): Promise<void> {
    this.loading = true;
    const profile = this.auth.profile() ?? (await this.auth.refreshProfile());
    if (!profile) {
      this.loading = false;
      return;
    }

    const role = profile.role;
    this.welcome =
      role === 'checker'
        ? 'Review compliance analysis submissions.'
        : role === 'reviewer'
          ? 'Finalize approved compliance reviews.'
          : 'Manage regulation documents, regulation points libraries, and compliance analysis.';

    if (role === 'super_admin') {
      const runs = await this.api.getAnalysisRuns({ ndOnly: true, summaryOnly: true, skipGapStats: true });
      if (runs.success && runs.data) {
        this.recentRuns = sortAnalysisRunsByRecent(runs.data as AnalysisRunSummary[]);
      }
    } else if (role === 'maker') {
      const runs = await this.api.getAnalysisRuns({ mineOnly: true, ndOnly: true, summaryOnly: true, skipGapStats: true });
      if (runs.success && runs.data) {
        this.recentRuns = sortAnalysisRunsByRecent(runs.data as AnalysisRunSummary[]);
      }
    } else if (role === 'checker') {
      const queue = await this.api.getCheckerQueue();
      this.recentRuns = sortAnalysisRunsByRecent((queue.data as AnalysisRunSummary[]) ?? []);
    } else if (role === 'reviewer') {
      const queue = await this.api.getReviewerQueue();
      this.recentRuns = sortAnalysisRunsByRecent((queue.data as AnalysisRunSummary[]) ?? []);
    }

    await this.loadActionPlanSummary();

    this.loading = false;
  }

  private async loadActionPlanSummary(): Promise<void> {
    const res = await this.api.getActionPlanSummary();
    if (!res.success || !res.data || res.data.total === 0) {
      this.actionPlanSummary = null;
      this.priorityBuckets = [];
      return;
    }

    this.actionPlanSummary = res.data;
    const byPriority = new Map(res.data.byPriority.map((b) => [normalizeActionPlanPriority(b.priority), b]));
    this.priorityBuckets = ACTION_PLAN_PRIORITY_OPTIONS.map(({ value, label }) => {
      const bucket = byPriority.get(value);
      return {
        priority: value,
        label,
        total: bucket?.total ?? 0,
        pending: bucket?.pending ?? 0,
        resolved: bucket?.resolved ?? 0,
        overdue: bucket?.overdue ?? 0,
        runCount: bucket?.runCount ?? 0,
      };
    });
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.auth.getRole(), { demoViewer: this.auth.isDemoViewer() });
  }

  get newAnalysisLink(): string[] {
    return ndNewAnalysisRoute();
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.auth.getRole(), { demoViewer: this.auth.isDemoViewer() });
  }

  isLegacy(run: AnalysisRunSummary): boolean {
    return isLegacyAnalysisRun(run);
  }

  workflowLabel = analysisRunWorkflowLabel;
  formatDate = formatDate;

  get viewAllLink(): string[] {
    const role = this.auth.getRole();
    if (role === 'checker') return ['/nd/checker'];
    if (role === 'reviewer') return ['/nd/reviewer'];
    if (role === 'maker') return ['/nd/analysis-runs'];
    return ['/nd/analysis-runs'];
  }

  get viewAllLabel(): string {
    const role = this.auth.getRole();
    if (role === 'checker') return 'Pending review →';
    if (role === 'reviewer') return 'Pending final review →';
    return 'All analysis →';
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
}
