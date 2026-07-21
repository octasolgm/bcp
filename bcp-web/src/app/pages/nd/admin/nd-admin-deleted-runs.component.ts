import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-admin-deleted-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-admin-deleted-runs.component.html',
  styleUrls: ['./nd-admin-deleted-runs.component.scss', '../nd-shared.scss'],
})
export class NdAdminDeletedRunsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  runs: AnalysisRunSummary[] = [];
  loading = true;
  restoringId: string | null = null;
  message = '';
  error = '';
  searchQuery = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
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
      this.runs = res.data as AnalysisRunSummary[];
    } else {
      this.runs = [];
      this.error = res.message ?? 'Failed to load deleted runs';
    }
    this.loading = false;
  }

  async restore(run: AnalysisRunSummary): Promise<void> {
    if (!confirm(`Restore "${run.name}"? It will reappear in the workspace with status "${run.statusBeforeDelete ?? 'draft'}".`)) {
      return;
    }
    this.restoringId = run.id;
    this.message = '';
    this.error = '';
    const res = await this.api.restoreAnalysisRun(run.id);
    if (res.success) {
      this.message = `"${run.name}" restored.`;
      this.runs = this.runs.filter((r) => r.id !== run.id);
    } else {
      this.error = res.message ?? 'Restore failed';
    }
    this.restoringId = null;
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
