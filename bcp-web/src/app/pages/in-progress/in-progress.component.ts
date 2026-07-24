import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { isNdRunProcessing } from '../../../lib/nd/nd-run-activity';
import { NdApiService } from '../../services/nd/nd-api.service';
import { shellRoute } from '../../services/app-route-prefix';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery } from '../../../lib/nd/run-links';
import {
  analysisRunWorkflowLabel,
  analysisRunSubmittedByLabel,
  analysisRunSubmittedByCaption,
  analysisRunSubmittedDate,
  analysisRunDisplayStatusLabel,
} from '../../../lib/nd/analysis-run-status';
import { canDeleteRun, canSendRunForReview } from '../../../lib/nd/analysis-run-actions';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { ToastService } from '../../services/toast.service';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdRunRoleBadgeComponent } from '../../components/nd/nd-run-role-badge.component';
import { NdRunHistoryPanelComponent } from '../../components/nd/nd-run-history-panel.component';
import { NdRunTableActionsComponent } from '../../components/nd/nd-run-table-actions.component';
import type { AnalysisRunSummary } from '../../../lib/nd/types';
import { runGapStatsFromSummary, type RunGapStatsSummary } from '../../../lib/nd/run-gap-stats';
import { formatDate } from '../../../lib/nd/utils';

type RunSortColumn = 'name' | 'points' | 'created' | 'source' | 'workflow' | 'status' | 'maker';

@Component({
  selector: 'app-in-progress',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NdStatusBadgeComponent,
    NdRunRoleBadgeComponent,
    NdRunHistoryPanelComponent,
    NdRunTableActionsComponent,
  ],
  templateUrl: './in-progress.component.html',
  styleUrls: ['./in-progress.component.scss', '../nd/nd-shared.scss', '../nd/analysis-runs/nd-analysis-runs.component.scss'],
})
export class InProgressComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly ndApi = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);

  allRuns: AnalysisRunSummary[] = [];
  loading = false;
  loadError = '';
  searchQuery = '';
  sourceFilter = '';
  sortColumn: RunSortColumn = 'created';
  sortDir: 'asc' | 'desc' = 'desc';
  deletingId: string | null = null;
  submittingRunId: string | null = null;
  stoppingId: string | null = null;
  historyOpen = false;
  historyRunId: string | null = null;
  historyRunName = '';
  historyRunStats: RunGapStatsSummary | null = null;

  get newAnalysisPath(): string {
    return shellRoute(this.router, '/analyse-v8');
  }

  get canCreate(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  ngOnInit(): void {
    void this.load();
  }

  refresh(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading = true;
    this.loadError = '';
    const role = this.auth.getRole();
    const res = await this.ndApi.getAnalysisRuns(
      role === 'maker'
        ? { mineOnly: true, ndOnly: true, summaryOnly: true }
        : { ndOnly: true, summaryOnly: true },
    );
    if (res.success && res.data) {
      this.allRuns = (res.data as AnalysisRunSummary[]).filter((r) => isNdRunProcessing(r));
    } else {
      this.allRuns = [];
      this.loadError = res.message ?? 'Could not load ND analysis runs.';
      this.toast.show(this.loadError, 'error', 6000);
    }
    this.loading = false;
  }

  get visibleRuns(): AnalysisRunSummary[] {
    const query = this.searchQuery.trim().toLowerCase();
    let list = this.allRuns.filter((run) => {
      if (query && !run.name.toLowerCase().includes(query) && !(run.makerName ?? '').toLowerCase().includes(query)) {
        return false;
      }
      if (this.sourceFilter && (run.source ?? 'nd_analysis') !== this.sourceFilter) return false;
      return true;
    });

    const dir = this.sortDir === 'asc' ? 1 : -1;
    list = [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'maker':
          return dir * (a.makerName ?? '').localeCompare(b.makerName ?? '');
        case 'name':
          return dir * a.name.localeCompare(b.name);
        case 'points':
          return dir * (a.processedPointsCount - b.processedPointsCount || a.totalPointsCount - b.totalPointsCount);
        case 'source':
          return dir * this.sourceLabel(a).localeCompare(this.sourceLabel(b));
        case 'workflow':
          return dir * (a.workflowHolder ?? analysisRunWorkflowLabel(a)).localeCompare(
            b.workflowHolder ?? analysisRunWorkflowLabel(b),
          );
        case 'status':
          return dir * analysisRunDisplayStatusLabel(a.status).localeCompare(
            analysisRunDisplayStatusLabel(b.status),
          );
        case 'created':
        default:
          return dir * (Date.parse(a.createdAt) - Date.parse(b.createdAt));
      }
    });

    return list;
  }

  get hasActiveFilters(): boolean {
    return Boolean(this.searchQuery.trim() || this.sourceFilter);
  }

  toggleSort(column: RunSortColumn): void {
    if (this.sortColumn === column) {
      this.sortDir = this.sortDir === 'asc' ? 'desc' : 'asc';
      return;
    }
    this.sortColumn = column;
    this.sortDir = column === 'created' ? 'desc' : 'asc';
  }

  sortIndicator(column: RunSortColumn): string {
    if (this.sortColumn !== column) return '';
    return this.sortDir === 'asc' ? '↑' : '↓';
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.sourceFilter = '';
  }

  private sourceLabel(run: AnalysisRunSummary): string {
    if (run.source === 'legacy_dual_verify') return 'Legacy DV';
    if (run.source === 'legacy_analysis') return 'Legacy';
    return 'ND';
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.auth.getRole());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.auth.getRole());
  }

  workflowStatusLabel = analysisRunWorkflowLabel;
  submittedByLabel = analysisRunSubmittedByLabel;
  submittedByCaption = analysisRunSubmittedByCaption;
  submittedDate = analysisRunSubmittedDate;
  formatDate = formatDate;

  openHistory(run: AnalysisRunSummary, event?: Event): void {
    event?.stopPropagation();
    event?.preventDefault();
    if (isLegacyAnalysisRun(run)) return;
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

  async submitRunForReview(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    event?.preventDefault();
    if (!canSendRunForReview(run, this.auth.getRole())) return;
    this.submittingRunId = run.id;
    const status = run.status.toLowerCase();
    const res =
      status === 'pulled_back'
        ? await this.ndApi.resubmitForReview(run.id)
        : await this.ndApi.submitForReview(run.id);
    this.submittingRunId = null;
    if (res.success) {
      this.toast.show('Submitted to checker for review', 'success');
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not submit for review', 'error');
    }
  }

  async deleteRun(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    if (!canDeleteRun(run, this.auth.getRole(), this.auth.profile()?.id)) return;
    if (!confirm(`Delete "${run.name}"? It will be hidden from the workspace but can be restored by a super admin.`)) {
      return;
    }
    this.deletingId = run.id;
    const res = await this.ndApi.softDeleteAnalysisRun(run.id);
    if (res.success) {
      this.allRuns = this.allRuns.filter((r) => r.id !== run.id);
      this.toast.show(`"${run.name}" removed.`, 'success');
    } else {
      this.toast.show(res.message ?? 'Delete failed', 'error');
    }
    this.deletingId = null;
  }

  async stopRun(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    event?.preventDefault();
    if (isLegacyAnalysisRun(run)) return;
    if (!confirm(`Stop "${run.name}"?\n\nQueued points will be cancelled. A point already being analysed may finish its current pass first.`)) {
      return;
    }
    this.stoppingId = run.id;
    const res = await this.ndApi.stopAnalysisRun(run.id);
    this.stoppingId = null;
    if (res.success) {
      this.toast.show('Analysis stopped', 'warning');
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not stop analysis', 'error');
    }
  }
}
