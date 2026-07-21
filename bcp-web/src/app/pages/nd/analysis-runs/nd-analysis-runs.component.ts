import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs/operators';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ToastService } from '../../../services/toast.service';
import { NdWorkspaceTabsComponent } from '../../../components/nd/nd-workspace-tabs.component';
import { formatDate } from '../../../../lib/nd/utils';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery, analysisRunNeedsExecutionView } from '../../../../lib/nd/run-links';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

type RunSortColumn = 'name' | 'points' | 'created' | 'source' | 'workflow' | 'status';

@Component({
  selector: 'app-nd-analysis-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdWorkspaceTabsComponent],
  templateUrl: './nd-analysis-runs.component.html',
  styleUrls: ['./nd-analysis-runs.component.scss', '../nd-shared.scss'],
})
export class NdAnalysisRunsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  allRuns: AnalysisRunSummary[] = [];
  loading = true;
  mineOnly = false;
  correctionOnly = false;
  pageTitle = 'Analysis runs';
  subtitle = 'All compliance analysis runs';

  searchQuery = '';
  statusFilter = '';
  sourceFilter = '';
  sortColumn: RunSortColumn = 'created';
  sortDir: 'asc' | 'desc' = 'desc';
  deletingId: string | null = null;
  submittingRunId: string | null = null;
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
      void this.load();
    });

    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe(() => {
        if (this.router.url.includes('/nd/analysis-runs')) void this.load();
      });
  }

  async load(): Promise<void> {
    this.loading = true;
    const res = await this.api.getAnalysisRuns(
      this.correctionOnly
        ? {
            ndOnly: true,
            status: 'pulled_back',
            ...(this.mineOnly ? { mineOnly: true } : {}),
          }
        : this.mineOnly
          ? { mineOnly: true, ndOnly: true }
          : { ndOnly: true },
    );
    if (res.success && res.data) {
      this.allRuns = res.data as AnalysisRunSummary[];
    } else {
      this.allRuns = [];
    }
    this.loading = false;
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
      if (query && !run.name.toLowerCase().includes(query)) return false;
      if (this.statusFilter && !this.matchesStatusFilter(run.status)) return false;
      if (this.sourceFilter && (run.source ?? 'nd_analysis') !== this.sourceFilter) return false;
      return true;
    });

    const dir = this.sortDir === 'asc' ? 1 : -1;
    list = [...list].sort((a, b) => {
      switch (this.sortColumn) {
        case 'name':
          return dir * a.name.localeCompare(b.name);
        case 'points':
          return dir * (a.processedPointsCount - b.processedPointsCount || a.totalPointsCount - b.totalPointsCount);
        case 'source':
          return dir * this.sourceLabel(a).localeCompare(this.sourceLabel(b));
        case 'workflow':
          return dir * (a.workflowHolder ?? this.workflowStatusLabel(a)).localeCompare(
            b.workflowHolder ?? this.workflowStatusLabel(b),
          );
        case 'status':
          return dir * a.status.localeCompare(b.status);
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

  isLegacy(run: AnalysisRunSummary): boolean {
    return isLegacyAnalysisRun(run);
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.auth.getRole());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.auth.getRole());
  }

  needsExecutionView(run: AnalysisRunSummary): boolean {
    return analysisRunNeedsExecutionView(run);
  }

  statusClass(status: string): string {
    const s = status.toLowerCase();
    if (s === 'reviewer_approved') return 'completed';
    if (s === 'failed' || s === 'cancelled') return 'failed';
    if (s === 'submitted_for_review' || s === 'checker_approved') return 'pending';
    if (s === 'completed' || s === 'dual_verify_failed' || s === 'landing_ai_complete' || s === 'pulled_back') {
      return 'running';
    }
    return 'running';
  }

  workflowStatusLabel(run: AnalysisRunSummary): string {
    if (analysisRunNeedsExecutionView(run)) {
      const total = run.totalPointsCount ?? 0;
      const processed = run.processedPointsCount ?? 0;
      if (total > 0 && processed < total) return 'Points pending';
      if ((run.dualVerifyFailedCount ?? 0) > 0) return 'Rerun failed points';
      return 'Continue analysis';
    }
    const s = run.status.toLowerCase();
    if (['completed', 'dual_verify_failed', 'landing_ai_complete'].includes(s)) {
      return 'Submit for review pending';
    }
    if (s === 'pulled_back') return 'Resubmit pending';
    if (s === 'submitted_for_review') return 'With checker';
    if (s === 'checker_approved') return 'With reviewer';
    if (s === 'reviewer_approved') return 'Review complete';
    return '';
  }

  analysisStatusLabel(status: string): string {
    const s = status.toLowerCase();
    if (s === 'dual_verify_failed') return 'Dual verify failed';
    if (s === 'landing_ai_complete') return 'Analysis complete';
    return status.replace(/_/g, ' ');
  }

  formatDate = formatDate;

  canSendForReview(run: AnalysisRunSummary): boolean {
    if (analysisRunNeedsExecutionView(run)) return false;
    if (this.isLegacy(run)) return false;
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(
      run.status.toLowerCase(),
    );
  }

  async submitRunForReview(run: AnalysisRunSummary, event: Event): Promise<void> {
    event.stopPropagation();
    event.preventDefault();
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
    } else {
      this.toast.show(res.message ?? 'Could not submit for review', 'error');
    }
  }

  canReviewRun(run: AnalysisRunSummary): boolean {
    if (this.isLegacy(run)) return false;
    const role = this.auth.getRole();
    if (role === 'checker' && run.status === 'submitted_for_review') return true;
    if (role === 'reviewer' && run.status === 'checker_approved') return true;
    return false;
  }

  canEditPlans(run: AnalysisRunSummary): boolean {
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    if (analysisRunNeedsExecutionView(run)) return false;
    if (this.isLegacy(run)) return false;
    return [
      'completed',
      'submitted_for_review',
      'checker_approved',
      'reviewer_approved',
      'pulled_back',
      'dual_verify_failed',
      'landing_ai_complete',
    ].includes(run.status.toLowerCase());
  }

  canDelete(run: AnalysisRunSummary): boolean {
    const role = this.auth.getRole();
    if (role === 'super_admin') return true;
    // Legacy runs have no owner recorded — any maker may remove them.
    if (role === 'maker') return this.isLegacy(run) || !run.createdBy || run.createdBy === this.auth.profile()?.id;
    return false;
  }

  async deleteRun(run: AnalysisRunSummary, event: Event): Promise<void> {
    event.stopPropagation();
    if (!confirm(`Delete "${run.name}"? It will be hidden from the workspace but can be restored by a super admin.`)) {
      return;
    }
    this.deletingId = run.id;
    this.deleteMessage = '';
    this.deleteError = '';
    const res = await this.api.softDeleteAnalysisRun(run.id);
    if (res.success) {
      this.allRuns = this.allRuns.filter((r) => r.id !== run.id);
      this.deleteMessage = `"${run.name}" removed.`;
    } else {
      this.deleteError = res.message ?? 'Delete failed';
    }
    this.deletingId = null;
  }
}
