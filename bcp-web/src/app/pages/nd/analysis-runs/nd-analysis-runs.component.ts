import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs/operators';
import { NdWorkspaceNavService } from '../../../services/nd/nd-workspace-nav.service';
import { bumpsForAnalysisRunSoftDelete } from '../../../../lib/nd/nav-badge-bumps';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { NdWorkspaceTabsComponent } from '../../../components/nd/nd-workspace-tabs.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdRunRoleBadgeComponent } from '../../../components/nd/nd-run-role-badge.component';
import { NdRunHistoryPanelComponent } from '../../../components/nd/nd-run-history-panel.component';
import { NdRunTableActionsComponent } from '../../../components/nd/nd-run-table-actions.component';
import { formatDate } from '../../../../lib/nd/utils';
import { ndNewAnalysisRoute, isDemoOwnedAnalysisRun } from '../../../../lib/nd/demo-analysis-routes';
import {
  isPermanentDemoAnalysisDelete,
  wasAnalysisRunPermanentlyDeleted,
} from '../../../../lib/nd/analysis-run-delete';
import {
  canDeleteRun,
  canEditRunPlans,
  canReviewRun as canReviewAnalysisRun,
  canSendRunForReview,
} from '../../../../lib/nd/analysis-run-actions';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery, analysisRunNeedsExecutionView } from '../../../../lib/nd/run-links';
import {
  analysisRunDisplayStatusLabel,
  analysisRunWorkflowLabel,
  analysisRunSubmittedByLabel,
  analysisRunSubmittedByCaption,
  analysisRunSubmittedDate,
} from '../../../../lib/nd/analysis-run-status';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';
import { isNdRunProcessing } from '../../../../lib/nd/nd-run-activity';
import { runGapStatsFromSummary, type RunGapStatsSummary } from '../../../../lib/nd/run-gap-stats';

type RunSortColumn = 'name' | 'points' | 'created' | 'source' | 'workflow' | 'status' | 'maker';

@Component({
  selector: 'app-nd-analysis-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdWorkspaceTabsComponent, NdStatusBadgeComponent, NdRunRoleBadgeComponent, NdRunHistoryPanelComponent, NdRunTableActionsComponent],
  templateUrl: './nd-analysis-runs.component.html',
  styleUrls: ['./nd-analysis-runs.component.scss', '../nd-shared.scss'],
})
export class NdAnalysisRunsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  allRuns: AnalysisRunSummary[] = [];
  loading = true;
  loadError = '';
  mineOnly = false;
  correctionOnly = false;
  pageTitle = 'Analysis runs';
  subtitle = 'All compliance analysis runs';
  page = 1;
  pageSize = 20;
  totalCount = 0;
  totalPages = 0;

  searchQuery = '';
  statusFilter = '';
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
  deleteMessage = '';
  deleteError = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();

    this.route.queryParamMap.subscribe((params) => {
      const role = this.auth.getRole();
      this.mineOnly = role === 'maker' ? params.get('mine') !== '0' : params.get('mine') === '1';
      this.correctionOnly = params.get('correction') === '1';
      this.pageTitle = this.correctionOnly
        ? 'Pending correction'
        : this.mineOnly && role === 'maker'
          ? 'All analysis'
          : 'All analysis';
      this.subtitle = this.correctionOnly
        ? 'Analyses sent back to the maker by checker or reviewer — edit and resubmit when ready'
        : this.mineOnly && role === 'maker'
          ? 'Runs you created'
          : 'All compliance analysis runs across the workspace';
      this.page = 1;
      void this.load();
    });

    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe(() => {
        if (this.router.url.includes('/nd/analysis-runs')) {
          this.page = 1;
          void this.load();
        }
      });

    this.workspaceNav.refreshRequested.subscribe(() => {
      if (this.router.url.includes('/nd/analysis-runs')) {
        this.page = 1;
        void this.load();
      }
    });
  }

  async load(): Promise<void> {
    this.loading = true;
    this.loadError = '';
    const res = await this.api.getAnalysisRuns(
      this.correctionOnly
        ? {
            ndOnly: true,
            summaryOnly: true,
            status: 'pulled_back',
            page: this.page,
            pageSize: this.pageSize,
            ...(this.mineOnly ? { mineOnly: true } : {}),
          }
        : this.mineOnly
          ? { mineOnly: true, ndOnly: true, summaryOnly: true, page: this.page, pageSize: this.pageSize }
          : { ndOnly: true, summaryOnly: true, page: this.page, pageSize: this.pageSize },
    );
    if (res.success && res.data) {
      let finalRes = res;
      const rows = Array.isArray(res.data) ? res.data : [];
      if (rows.length === 0 && this.auth.isDemoViewer()) {
        const seed = await this.api.createDemoAnalysisFromCbuaeSeed();
        if (seed.success) {
          const retry = await this.api.getAnalysisRuns(
            this.correctionOnly
              ? {
                  ndOnly: true,
                  summaryOnly: true,
                  status: 'pulled_back',
                  page: this.page,
                  pageSize: this.pageSize,
                  ...(this.mineOnly ? { mineOnly: true } : {}),
                }
              : this.mineOnly
                ? { mineOnly: true, ndOnly: true, summaryOnly: true, page: this.page, pageSize: this.pageSize }
                : { ndOnly: true, summaryOnly: true, page: this.page, pageSize: this.pageSize },
          );
          if (retry.success && retry.data) finalRes = retry;
        }
      }
      this.allRuns = finalRes.data as AnalysisRunSummary[];
      this.totalCount = finalRes.pagination?.total ?? this.allRuns.length;
      this.totalPages = finalRes.pagination?.totalPages ?? 1;
      if (this.page > this.totalPages && this.totalPages > 0) {
        this.page = this.totalPages;
        this.loading = false;
        await this.load();
        return;
      }
    } else {
      this.allRuns = [];
      this.totalCount = 0;
      this.totalPages = 0;
      this.loadError = res.message ?? 'Could not load analysis runs from the API.';
      this.toast.show(this.loadError, 'error', 6000);
    }
    this.loading = false;
  }

  goToPage(next: number): void {
    if (next < 1 || next > this.totalPages || next === this.page) return;
    this.page = next;
    void this.load();
  }

  get pageRangeLabel(): string {
    if (!this.totalCount) return '0 runs';
    const start = (this.page - 1) * this.pageSize + 1;
    const end = Math.min(this.page * this.pageSize, this.totalCount);
    return `${start}–${end} of ${this.totalCount}`;
  }

  get workspaceTabActive(): 'all_analysis' | 'pending_correction' {
    return this.correctionOnly ? 'pending_correction' : 'all_analysis';
  }

  get showWorkspaceTabs(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'checker' || role === 'reviewer' || role === 'super_admin';
  }

  get visibleRuns(): AnalysisRunSummary[] {
    const query = this.searchQuery.trim().toLowerCase();
    let list = this.allRuns.filter((run) => {
      if (this.correctionOnly && run.status.toLowerCase() !== 'pulled_back') return false;
      if (query && !run.name.toLowerCase().includes(query) && !(run.makerName ?? '').toLowerCase().includes(query)) {
        return false;
      }
      if (this.statusFilter && !this.matchesStatusFilter(run.status)) return false;
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
    return Boolean(this.searchQuery.trim() || this.statusFilter || this.sourceFilter);
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
    this.statusFilter = '';
    this.sourceFilter = '';
  }

  private matchesStatusFilter(status: string): boolean {
    const normalized = status.toLowerCase();
    switch (this.statusFilter) {
      case 'draft':
        return normalized === 'draft';
      case 'running':
        return normalized === 'running' || normalized === 'processing' || normalized === 'queued';
      case 'failed':
        return normalized === 'failed' || normalized === 'cancelled';
      case 'submit_pending':
        return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(normalized);
      case 'completed':
        return normalized === 'reviewer_approved';
      case 'review':
        return normalized === 'submitted_for_review' || normalized === 'checker_approved';
      default:
        return normalized === this.statusFilter.toLowerCase();
    }
  }

  private sourceLabel(run: AnalysisRunSummary): string {
    if (run.source === 'legacy_dual_verify') return 'Legacy DV';
    if (run.source === 'legacy_analysis') return 'Legacy';
    return 'ND';
  }

  get canCreate(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  get newAnalysisLink(): string[] {
    return ndNewAnalysisRoute();
  }

  private runLinkOpts(): { demoViewer: boolean } {
    return { demoViewer: this.auth.isDemoViewer() };
  }

  isLegacy(run: AnalysisRunSummary): boolean {
    return isLegacyAnalysisRun(run);
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.auth.getRole(), this.runLinkOpts());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.auth.getRole(), this.runLinkOpts());
  }

  needsExecutionView(run: AnalysisRunSummary): boolean {
    return analysisRunNeedsExecutionView(run);
  }

  workflowStatusLabel = analysisRunWorkflowLabel;
  submittedByLabel = analysisRunSubmittedByLabel;
  submittedByCaption = analysisRunSubmittedByCaption;
  submittedDate = analysisRunSubmittedDate;

  openHistory(run: AnalysisRunSummary, event?: Event): void {
    event?.stopPropagation();
    event?.preventDefault();
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

  formatDate = formatDate;

  canSendForReview(run: AnalysisRunSummary): boolean {
    return canSendRunForReview(run, this.auth.getRole());
  }

  canReviewRun(run: AnalysisRunSummary): boolean {
    return canReviewAnalysisRun(run, this.auth.getRole());
  }

  canEditPlans(run: AnalysisRunSummary): boolean {
    return canEditRunPlans(run, this.auth.getRole());
  }

  canDelete(run: AnalysisRunSummary): boolean {
    return canDeleteRun(run, this.auth.getRole(), this.auth.profile()?.id);
  }

  async submitRunForReview(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    event?.preventDefault();
    if (!this.canSendForReview(run)) return;
    this.submittingRunId = run.id;
    const status = run.status.toLowerCase();
    const res =
      status === 'pulled_back'
        ? await this.api.resubmitForReview(run.id)
        : await this.api.submitForReview(run.id);
    this.submittingRunId = null;
    if (res.success) {
      this.toast.show('Submitted to checker for review', 'success');
      await this.load();
      this.workspaceNav.requestNavBadgeRefresh();
    } else {
      this.toast.show(res.message ?? 'Could not submit for review', 'error');
    }
  }

  async deleteRun(run: AnalysisRunSummary, event?: Event): Promise<void> {
    event?.stopPropagation();
    const permanentDemoDelete = isPermanentDemoAnalysisDelete(run, this.auth.isDemoViewer());
    const confirmMsg = permanentDemoDelete
      ? `Permanently delete demo analysis "${run.name}"? It will be removed from the database and cannot be restored.`
      : `Delete "${run.name}"? It will be hidden from the workspace but can be restored by a super admin.`;
    if (!confirm(confirmMsg)) {
      return;
    }
    this.deletingId = run.id;
    this.deleteMessage = '';
    this.deleteError = '';
    const bumps = bumpsForAnalysisRunSoftDelete(
      run,
      this.auth.getRole() === 'super_admin',
      !permanentDemoDelete,
    );
    this.workspaceNav.bumpNavBadges(bumps);
    this.allRuns = this.allRuns.filter((r) => r.id !== run.id);
    this.totalCount = Math.max(0, this.totalCount - 1);
    this.deletingId = null;
    const res = await this.api.softDeleteAnalysisRun(run.id);
    if (res.success) {
      const permanentlyDeleted =
        permanentDemoDelete || wasAnalysisRunPermanentlyDeleted(res);
      this.deleteMessage = permanentlyDeleted
        ? `"${run.name}" permanently removed.`
        : `"${run.name}" removed.`;
      if (permanentlyDeleted) {
        this.workspaceNav.notifyAnalysisRunPermanentlyDeleted(run);
      } else {
        this.workspaceNav.notifyAnalysisRunSoftDeleted(run);
      }
      window.setTimeout(() => this.workspaceNav.requestNavBadgeRefresh(), 2500);
      if (!this.allRuns.length && this.page > 1) {
        this.page -= 1;
        await this.load();
      }
    } else {
      this.deleteError = res.message ?? 'Delete failed';
      this.allRuns = [...this.allRuns, run].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
      this.totalCount += 1;
      this.workspaceNav.bumpNavBadges({
        analysisRunsAll: bumps.analysisRunsAll ? -bumps.analysisRunsAll : undefined,
        analysisRunsInProgress: bumps.analysisRunsInProgress ? -bumps.analysisRunsInProgress : undefined,
        analysisRunsCorrection: bumps.analysisRunsCorrection ? -bumps.analysisRunsCorrection : undefined,
        adminDeletedRuns: bumps.adminDeletedRuns ? -bumps.adminDeletedRuns : undefined,
      });
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
    this.allRuns = this.allRuns.map((r) =>
      r.id === run.id ? { ...r, status: 'cancelled' as const } : r,
    );
    const res = await this.api.stopAnalysisRun(run.id);
    this.stoppingId = null;
    if (res.success) {
      this.toast.show('Analysis stopped', 'warning');
      this.workspaceNav.bumpNavBadges({
        analysisRunsInProgress: isNdRunProcessing(run) ? -1 : undefined,
      });
      await this.load();
      this.workspaceNav.requestNavBadgeRefresh();
    } else {
      this.toast.show(res.message ?? 'Stop signalled — refresh in a moment', 'warning');
      await this.load();
    }
  }
}
