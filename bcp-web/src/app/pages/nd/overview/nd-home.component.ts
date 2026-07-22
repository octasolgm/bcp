import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { DashboardComponent } from '../../dashboard/dashboard.component';
import { NdOverviewComponent } from './nd-overview.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { ndAnalysisRunTarget } from '../../../../lib/nd/run-links';
import type { AnalysisRunSummary } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-home',
  standalone: true,
  imports: [DashboardComponent, NdOverviewComponent],
  template: `
    @if (redirecting || roleLoading) {
      <p class="nd-home-loading">Loading…</p>
    } @else if (showComplianceDashboard) {
      <app-dashboard />
    } @else {
      <app-nd-overview />
    }
  `,
  styles: [
    `
      .nd-home-loading {
        color: var(--text-muted);
        font-size: 0.875rem;
        padding: 1rem 0;
      }
    `,
  ],
})
export class NdHomeComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);

  showComplianceDashboard = false;
  redirecting = false;
  roleLoading = true;

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.queryParamMap.get('run');
    if (runId) {
      await this.redirectToRun(runId);
      return;
    }

    await this.auth.refreshProfile();
    const role = this.auth.getRole();
    this.showComplianceDashboard = role === 'maker' || role === 'super_admin';
    this.roleLoading = false;
  }

  private async redirectToRun(runId: string): Promise<void> {
    this.redirecting = true;
    await this.auth.refreshProfile();
    const role = this.auth.getRole();

    const res = await this.api.getAnalysisRun(runId);
    if (res.success && res.data) {
      const detail = res.data as {
        status?: string;
        source?: AnalysisRunSummary['source'];
        legacyHref?: string | null;
        name?: string;
        totalPointsCount?: number;
        processedPointsCount?: number;
        createdAt?: string;
      };
      const target = ndAnalysisRunTarget(
        {
          id: runId,
          name: detail.name ?? '',
          status: detail.status ?? '',
          totalPointsCount: detail.totalPointsCount ?? 0,
          processedPointsCount: detail.processedPointsCount ?? 0,
          createdAt: detail.createdAt ?? '',
          source: detail.source,
          legacyHref: detail.legacyHref,
        },
        role,
      );
      await this.router.navigate(target.routerLink, {
        queryParams: target.queryParams,
        replaceUrl: true,
      });
      return;
    }

    await this.router.navigate(['/nd/gap-analysis'], {
      queryParams: { run: runId },
      replaceUrl: true,
    });
  }
}
