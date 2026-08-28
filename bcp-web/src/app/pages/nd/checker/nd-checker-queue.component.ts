import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import {
  compareDateIso,
  compareText,
  hasListFilters,
  matchesSearch,
  nextSortState,
  sortIndicator,
  type SortDir,
} from '../../../../lib/nd/list-utils';
import { NdWorkspaceTabsComponent } from '../../../components/nd/nd-workspace-tabs.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdRunRoleBadgeComponent } from '../../../components/nd/nd-run-role-badge.component';
import { NdRunHistoryPanelComponent } from '../../../components/nd/nd-run-history-panel.component';
import { NdRunTableActionsComponent } from '../../../components/nd/nd-run-table-actions.component';
import {
  analysisRunComplianceBreakdown,
  analysisRunSubmittedByLabel,
  analysisRunSubmittedByCaption,
} from '../../../../lib/nd/analysis-run-status';
import { canRecallRun } from '../../../../lib/nd/analysis-run-actions';
import { ToastService } from '../../../services/toast.service';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';
import { runGapStatsFromSummary, runWorkCounts, type RunGapStatsSummary } from '../../../../lib/nd/run-gap-stats';
import { ndAnalysisRunLink, ndAnalysisRunQuery } from '../../../../lib/nd/run-links';

type QueueSortColumn = 'name' | 'maker' | 'date' | 'status';

@Component({
  selector: 'app-nd-checker-queue',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdWorkspaceTabsComponent, NdStatusBadgeComponent, NdRunRoleBadgeComponent, NdRunHistoryPanelComponent, NdRunTableActionsComponent],
  templateUrl: './nd-checker-queue.component.html',
  styleUrls: ['./nd-checker-queue.component.scss', '../run-analysis/nd-run-analysis.component.scss', '../nd-shared.scss'],
})
export class NdCheckerQueueComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  showHistory = false;
  allRuns: AnalysisRunSummary[] = [];
  loading = true;
  searchQuery = '';
  statusFilter = '';
  sortColumn: QueueSortColumn = 'date';
  sortDir: SortDir = 'desc';
  recallingRunId: string | null = null;
  roleActingRunId: string | null = null;
  historyOpen = false;
  historyRunId: string | null = null;
  historyRunName = '';
  historyRunStats: RunGapStatsSummary | null = null;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.route.queryParamMap.subscribe((params) => {
      this.showHistory = params.get('history') === '1';
      void this.load();
    });
  }

  async load(): Promise<void> {
    this.loading = true;
    const res = this.showHistory
      ? await this.api.getCheckerHistory()
      : await this.api.getCheckerQueue();
    if (res.success && res.data) this.allRuns = res.data as AnalysisRunSummary[];
    else this.allRuns = [];
    this.loading = false;
  }

  get visibleRuns(): AnalysisRunSummary[] {
    let list = this.allRuns.filter((run) => {
      if (!matchesSearch(this.searchQuery, [run.name, run.makerName])) return false;
      if (this.statusFilter && run.status !== this.statusFilter) return false;
      return true;
    });

    return [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'name':
          return compareText(a.name, b.name, this.sortDir);
        case 'maker':
          return compareText(a.makerName ?? '', b.makerName ?? '', this.sortDir);
        case 'status':
          return compareText(a.status, b.status, this.sortDir);
        case 'date':
        default:
          return compareDateIso(a.submittedAt ?? a.createdAt, b.submittedAt ?? b.createdAt, this.sortDir);
      }
    });
  }

  get hasActiveFilters(): boolean {
    return hasListFilters(this.searchQuery, this.statusFilter);
  }

  toggleSort(column: QueueSortColumn): void {
    const next = nextSortState(this.sortColumn, column, this.sortDir, 'date');
    this.sortColumn = next.column;
    this.sortDir = next.dir;
  }

  sortMark(column: QueueSortColumn): string {
    return sortIndicator(this.sortColumn, column, this.sortDir);
  }

  clearFilters(): void {
    this.searchQuery = '';
    this.statusFilter = '';
  }

  get canViewHistory(): boolean {
    const role = this.auth.getRole();
    return role === 'checker' || role === 'super_admin';
  }

  get isViewOnly(): boolean {
    return this.auth.getRole() === 'reviewer';
  }

  runLink(run: AnalysisRunSummary): string[] {
    if (this.isViewOnly) return ['/nd/gap-analysis'];
    return ndAnalysisRunLink(run, this.auth.getRole());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    if (this.isViewOnly) return { run: run.id };
    return ndAnalysisRunQuery(run, this.auth.getRole());
  }

  canRecall(run: AnalysisRunSummary): boolean {
    return !this.isViewOnly && canRecallRun(run, this.auth.getRole(), this.auth.profile()?.id);
  }

  async recallRun(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    event?.preventDefault();
    if (!this.canRecall(run)) return;
    this.recallingRunId = run.id;
    const res = await this.api.recallFromReviewer(run.id);
    this.recallingRunId = null;
    if (res.success) {
      this.toast.show('Recalled from reviewer', 'success');
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not recall this run', 'error');
    }
  }

  async runRoleAction(payload: { run: AnalysisRunSummary; target: string }): Promise<void> {
    const { run, target } = payload;
    this.roleActingRunId = run.id;
    const res =
      target === 'maker' ? await this.api.pullBackAnalysis(run.id, {}) : await this.api.approveAnalysis(run.id, {});
    this.roleActingRunId = null;
    if (res.success) {
      this.toast.show(target === 'maker' ? 'Sent back to maker' : 'Submitted to reviewer', 'success');
      await this.load();
    } else {
      this.toast.show(res.message ?? 'Could not submit this run', 'error');
    }
  }

  formatDate = formatDate;
  complianceBreakdown = analysisRunComplianceBreakdown;
  workCounts = runWorkCounts;
  submittedByLabel = analysisRunSubmittedByLabel;
  submittedByCaption = analysisRunSubmittedByCaption;

  openHistory(run: AnalysisRunSummary, event?: Event): void {
    event?.stopPropagation();
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
