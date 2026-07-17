import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs/operators';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery } from '../../../../lib/nd/run-links';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

type RunSortColumn = 'name' | 'points' | 'created' | 'source' | 'status';

@Component({
  selector: 'app-nd-analysis-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-analysis-runs.component.html',
  styleUrls: ['./nd-analysis-runs.component.scss', '../nd-shared.scss'],
})
export class NdAnalysisRunsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  allRuns: AnalysisRunSummary[] = [];
  loading = true;
  mineOnly = false;
  pageTitle = 'Analysis runs';
  subtitle = 'All compliance analysis runs';

  searchQuery = '';
  statusFilter = '';
  sourceFilter = '';
  sortColumn: RunSortColumn = 'created';
  sortDir: 'asc' | 'desc' = 'desc';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    const role = this.auth.getRole();

    this.route.queryParamMap.subscribe((params) => {
      this.mineOnly = params.get('mine') === '1' || role === 'maker';
      this.pageTitle = this.mineOnly ? 'My analysis runs' : 'All analysis runs';
      this.subtitle = this.mineOnly
        ? 'Runs you created'
        : role === 'checker'
          ? 'All runs — open pending items from the review queue'
          : role === 'reviewer'
            ? 'All runs — open pending items from the final review queue'
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
    const res = await this.api.getAnalysisRuns(this.mineOnly ? { mineOnly: true } : undefined);
    if (res.success && res.data) {
      this.allRuns = res.data as AnalysisRunSummary[];
    } else {
      this.allRuns = [];
    }
    this.loading = false;
  }

  get visibleRuns(): AnalysisRunSummary[] {
    const query = this.searchQuery.trim().toLowerCase();
    let list = this.allRuns.filter((run) => {
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
        return normalized === 'failed' || normalized === 'cancelled' || normalized === 'pulled_back';
      case 'completed':
        return (
          normalized === 'completed' ||
          normalized === 'checker_approved' ||
          normalized === 'reviewer_approved'
        );
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

  statusClass(status: string): string {
    if (status === 'completed' || status === 'checker_approved' || status === 'reviewer_approved') {
      return 'completed';
    }
    if (status === 'failed' || status === 'pulled_back') return 'failed';
    if (status === 'submitted_for_review') return 'pending';
    return 'running';
  }

  formatDate = formatDate;

  canSubmitRun(run: AnalysisRunSummary): boolean {
    if (this.isLegacy(run)) return false;
    const role = this.auth.getRole();
    if (role !== 'maker' && role !== 'super_admin') return false;
    return ['completed', 'dual_verify_failed', 'landing_ai_complete', 'pulled_back'].includes(
      run.status.toLowerCase(),
    );
  }

  canReviewRun(run: AnalysisRunSummary): boolean {
    if (this.isLegacy(run)) return false;
    const role = this.auth.getRole();
    if (role === 'checker' && run.status === 'submitted_for_review') return true;
    if (role === 'reviewer' && run.status === 'checker_approved') return true;
    return false;
  }

  canEditPlans(run: AnalysisRunSummary): boolean {
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

  async submitRunForChecker(run: AnalysisRunSummary, event: Event): Promise<void> {
    event.preventDefault();
    event.stopPropagation();
    const res =
      run.status === 'pulled_back'
        ? await this.api.resubmitForReview(run.id)
        : await this.api.submitForReview(run.id);
    if (res.success) {
      await this.load();
    }
  }
}
