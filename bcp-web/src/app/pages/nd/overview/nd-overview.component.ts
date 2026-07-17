import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import { isLegacyAnalysisRun, ndAnalysisRunLink, ndAnalysisRunQuery } from '../../../../lib/nd/run-links';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

type StatCard = { label: string; value: number | string; href?: string };

@Component({
  selector: 'app-nd-overview',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './nd-overview.component.html',
  styleUrls: ['./nd-overview.component.scss'],
})
export class NdOverviewComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  loading = true;
  stats: StatCard[] = [];
  recentRuns: AnalysisRunSummary[] = [];
  welcome = '';

  async ngOnInit(): Promise<void> {
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
      const [depts, users, regs, libs, runs, checkerQ, reviewerQ] = await Promise.all([
        this.api.getDepartments(),
        this.api.getUsers(),
        this.api.getRegulationDocuments(),
        this.api.getLibraries(),
        this.api.getAnalysisRuns(),
        this.api.getCheckerQueue(),
        this.api.getReviewerQueue(),
      ]);
      this.stats = [
        { label: 'Departments', value: depts.data?.length ?? 0, href: '/nd/admin/departments' },
        { label: 'Users', value: users.data?.length ?? 0, href: '/nd/admin/users' },
        { label: 'Regulation docs', value: regs.data?.length ?? 0, href: '/nd/regulation-documents' },
        { label: 'Regulation points libraries', value: libs.data?.length ?? 0, href: '/nd/libraries' },
        { label: 'Analysis runs', value: runs.data?.length ?? 0, href: '/nd/analysis-runs' },
        { label: 'Checker queue', value: checkerQ.data?.length ?? 0, href: '/nd/checker' },
        { label: 'Reviewer queue', value: reviewerQ.data?.length ?? 0, href: '/nd/reviewer' },
      ];
      this.recentRuns = (runs.data as AnalysisRunSummary[]) ?? [];
    } else if (role === 'maker') {
      const [regs, libs, internal, runs] = await Promise.all([
        this.api.getRegulationDocuments(),
        this.api.getLibraries(),
        this.api.getInternalDocuments(),
        this.api.getAnalysisRuns({ mineOnly: true }),
      ]);
      this.stats = [
        { label: 'Regulation docs', value: regs.data?.length ?? 0, href: '/nd/regulation-documents' },
        { label: 'Internal docs', value: internal.data?.length ?? 0, href: '/nd/internal-documents' },
        { label: 'Regulation points libraries', value: libs.data?.length ?? 0, href: '/nd/libraries' },
        { label: 'My analysis runs', value: runs.data?.length ?? 0, href: '/nd/analysis-runs?mine=1' },
      ];
      this.recentRuns = (runs.data as AnalysisRunSummary[]) ?? [];
    } else if (role === 'checker') {
      const [queue, history] = await Promise.all([
        this.api.getCheckerQueue(),
        this.api.getCheckerHistory(),
      ]);
      this.stats = [
        { label: 'Review queue', value: queue.data?.length ?? 0, href: '/nd/checker' },
        { label: 'Review history', value: history.data?.length ?? 0, href: '/nd/checker?history=1' },
      ];
      this.recentRuns = (queue.data as AnalysisRunSummary[]) ?? [];
    } else if (role === 'reviewer') {
      const [queue, history] = await Promise.all([
        this.api.getReviewerQueue(),
        this.api.getReviewerHistory(),
      ]);
      this.stats = [
        { label: 'Final review queue', value: queue.data?.length ?? 0, href: '/nd/reviewer' },
        { label: 'Final review history', value: history.data?.length ?? 0, href: '/nd/reviewer?history=1' },
      ];
      this.recentRuns = (queue.data as AnalysisRunSummary[]) ?? [];
    }

    this.loading = false;
  }

  runLink(run: AnalysisRunSummary): string[] {
    return ndAnalysisRunLink(run, this.auth.getRole());
  }

  runQuery(run: AnalysisRunSummary): Record<string, string> | undefined {
    return ndAnalysisRunQuery(run, this.auth.getRole());
  }

  isLegacy(run: AnalysisRunSummary): boolean {
    return isLegacyAnalysisRun(run);
  }

  statusClass(status: string): string {
    if (status === 'completed' || status === 'checker_approved' || status === 'reviewer_approved') {
      return 'completed';
    }
    if (status === 'failed') return 'failed';
    if (status === 'running' || status === 'processing' || status === 'draft') return 'running';
    return 'medium';
  }

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
    return 'View all →';
  }
}
