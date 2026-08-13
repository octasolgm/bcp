import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdWorkspaceNavService } from '../../../services/nd/nd-workspace-nav.service';
import { bumpsForAnalysisRunRestore } from '../../../../lib/nd/nav-badge-bumps';
import { isDemoOwnedAnalysisRun } from '../../../../lib/nd/demo-analysis-routes';
import {
  isPermanentDemoAnalysisDelete,
  wasAnalysisRunPermanentlyDeleted,
} from '../../../../lib/nd/analysis-run-delete';
import { formatDate } from '../../../../lib/nd/utils';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-admin-deleted-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-admin-deleted-runs.component.html',
  styleUrls: ['./nd-admin-deleted-runs.component.scss', '../nd-shared.scss'],
})
export class NdAdminDeletedRunsComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);
  private softDeletedSub?: Subscription;
  private permanentlyDeletedSub?: Subscription;

  runs: AnalysisRunSummary[] = [];
  loading = true;
  restoringId: string | null = null;
  deletingId: string | null = null;
  message = '';
  error = '';
  searchQuery = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
    this.softDeletedSub = this.workspaceNav.analysisRunSoftDeleted.subscribe((run) => {
      if (!this.auth.isDemoViewer() && isDemoOwnedAnalysisRun(run)) return;
      const deleted: AnalysisRunSummary = {
        ...run,
        status: 'deleted',
        statusBeforeDelete: run.status,
      };
      if (!this.runs.some((r) => r.id === run.id)) {
        this.runs = [deleted, ...this.runs];
      }
    });
    this.permanentlyDeletedSub = this.workspaceNav.analysisRunPermanentlyDeleted.subscribe((run) => {
      this.runs = this.runs.filter((r) => r.id !== run.id);
    });
  }

  ngOnDestroy(): void {
    this.softDeletedSub?.unsubscribe();
    this.permanentlyDeletedSub?.unsubscribe();
  }

  get isSuperAdmin(): boolean {
    return this.auth.getRole() === 'super_admin';
  }

  get visibleRuns(): AnalysisRunSummary[] {
    const query = this.searchQuery.trim().toLowerCase();
    if (!query) return this.runs;
    return this.runs.filter((run) => run.name.toLowerCase().includes(query));
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getAnalysisRuns({ deletedOnly: true });
    if (res.success && res.data) {
      this.runs = (res.data as AnalysisRunSummary[]).filter(
        (r) => this.auth.isDemoViewer() || !isDemoOwnedAnalysisRun(r),
      );
    } else {
      this.runs = [];
      this.error = res.message ?? 'Failed to load deleted runs';
    }
    this.loading = false;
  }

  async restore(run: AnalysisRunSummary): Promise<void> {
    if (isDemoOwnedAnalysisRun(run)) return;
    if (!confirm(`Restore "${run.name}"? It will reappear in the workspace with status "${run.statusBeforeDelete ?? 'draft'}".`)) {
      return;
    }
    this.restoringId = run.id;
    this.message = '';
    this.error = '';
    const bumps = bumpsForAnalysisRunRestore(run, true);
    this.workspaceNav.bumpNavBadges(bumps);
    const res = await this.api.restoreAnalysisRun(run.id);
    if (res.success) {
      this.message = `"${run.name}" restored.`;
      this.runs = this.runs.filter((r) => r.id !== run.id);
      window.setTimeout(() => this.workspaceNav.requestNavBadgeRefresh(), 400);
    } else {
      this.error = res.message ?? 'Restore failed';
      this.workspaceNav.bumpNavBadges({
        analysisRunsAll: bumps.analysisRunsAll ? -bumps.analysisRunsAll : undefined,
        analysisRunsInProgress: bumps.analysisRunsInProgress ? -bumps.analysisRunsInProgress : undefined,
        analysisRunsCorrection: bumps.analysisRunsCorrection ? -bumps.analysisRunsCorrection : undefined,
        adminDeletedRuns: bumps.adminDeletedRuns ? -bumps.adminDeletedRuns : undefined,
      });
    }
    this.restoringId = null;
  }

  isDemoRun(run: AnalysisRunSummary): boolean {
    return isDemoOwnedAnalysisRun(run);
  }

  async permanentlyDelete(run: AnalysisRunSummary): Promise<void> {
    if (!confirm(`Permanently delete "${run.name}"? This cannot be undone.`)) return;
    this.deletingId = run.id;
    this.message = '';
    this.error = '';
    const res = await this.api.softDeleteAnalysisRun(run.id);
    if (res.success && (wasAnalysisRunPermanentlyDeleted(res) || isPermanentDemoAnalysisDelete(run, this.auth.isDemoViewer()))) {
      this.message = `"${run.name}" permanently removed.`;
      this.runs = this.runs.filter((r) => r.id !== run.id);
      this.workspaceNav.notifyAnalysisRunPermanentlyDeleted(run);
      this.workspaceNav.bumpNavBadges({ adminDeletedRuns: -1 });
      window.setTimeout(() => this.workspaceNav.requestNavBadgeRefresh(), 400);
    } else if (res.success) {
      this.message = `"${run.name}" removed.`;
      this.runs = this.runs.filter((r) => r.id !== run.id);
      this.workspaceNav.bumpNavBadges({ adminDeletedRuns: -1 });
      window.setTimeout(() => this.workspaceNav.requestNavBadgeRefresh(), 400);
    } else {
      this.error = res.message ?? 'Permanent delete failed';
    }
    this.deletingId = null;
  }

  formatDate = formatDate;

  previousStatusLabel(run: AnalysisRunSummary): string {
    const s = (run.statusBeforeDelete ?? 'draft').replace(/_/g, ' ');
    return s.charAt(0).toUpperCase() + s.slice(1);
  }

  previousStatusClass(run: AnalysisRunSummary): string {
    const s = (run.statusBeforeDelete ?? 'draft').toLowerCase();
    if (s === 'completed') return 'completed';
    if (s === 'failed' || s === 'dual_verify_failed') return 'failed';
    if (s === 'running' || s === 'landing_ai_complete') return 'running';
    return 'pending';
  }
}
