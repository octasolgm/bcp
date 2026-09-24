import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdApiService, type NdAiUsageReport } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

type PeriodKey = '7d' | '30d' | 'month' | 'all' | 'custom';

/**
 * Platform admin reporting: AI spend across every client, filtered by business, period, model, feature
 * and entry type. Credit management (adding credits) stays on the Workspaces page, next to the rest of
 * a client's settings; this page answers "who used what".
 */
@Component({
  selector: 'app-nd-admin-ai-usage',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-admin-ai-usage.component.html',
  styleUrls: ['./nd-admin-ai-usage.component.scss', '../nd-shared.scss'],
})
export class NdAdminAiUsageComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly auth = inject(NdAuthService);

  report: NdAiUsageReport | null = null;
  loading = true;
  error = '';

  workspaceId = '';
  period: PeriodKey = '30d';
  customFrom = '';
  customTo = '';
  model = '';
  feature = '';
  kind = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    // Opened from a workspace row: start filtered to that client.
    const ws = this.route.snapshot.queryParamMap.get('workspace');
    if (ws) this.workspaceId = ws;
    await this.load();
  }

  get canManage(): boolean {
    return this.auth.canManageWorkspaces();
  }

  get hasFilters(): boolean {
    return !!(this.workspaceId || this.model || this.feature || this.kind) || this.period !== '30d';
  }

  private range(): { from?: string; to?: string } {
    const now = new Date();
    const iso = (d: Date) => d.toISOString();
    switch (this.period) {
      case '7d':
        return { from: iso(new Date(now.getTime() - 7 * 86400000)) };
      case '30d':
        return { from: iso(new Date(now.getTime() - 30 * 86400000)) };
      case 'month':
        return { from: iso(new Date(now.getFullYear(), now.getMonth(), 1)) };
      case 'custom':
        return {
          from: this.customFrom ? new Date(this.customFrom).toISOString() : undefined,
          to: this.customTo ? new Date(`${this.customTo}T23:59:59`).toISOString() : undefined,
        };
      default:
        return {};
    }
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const { from, to } = this.range();
    const res = await this.api.getAiUsageReport({
      workspaceId: this.workspaceId || undefined,
      from,
      to,
      model: this.model || undefined,
      feature: this.feature || undefined,
      kind: this.kind || undefined,
    });
    if (res.success && res.data) this.report = res.data;
    else this.error = res.message ?? 'Could not load AI usage';
    this.loading = false;
  }

  async onFilterChange(): Promise<void> {
    // Keep the workspace filter in the URL so the view can be shared or reopened.
    await this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { workspace: this.workspaceId || null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
    await this.load();
  }

  async clearFilters(): Promise<void> {
    this.workspaceId = '';
    this.period = '30d';
    this.customFrom = '';
    this.customTo = '';
    this.model = '';
    this.feature = '';
    this.kind = '';
    await this.onFilterChange();
  }

  async filterToWorkspace(id: string | null | undefined): Promise<void> {
    if (!id) return;
    this.workspaceId = id;
    await this.onFilterChange();
  }

  /** True when the realised margin is under the warning line set with the price. */
  marginIsLow(marginPct: number | undefined, minMarginPct: number | undefined): boolean {
    if (marginPct === undefined || minMarginPct === undefined) return false;
    return marginPct < minMarginPct;
  }

  featureLabel(feature: string | null | undefined): string {
    switch (feature) {
      case 'analysis':
        return 'Analysis';
      case 'rerun_forward':
        return 'Forward rerun';
      case 'rerun_reverse':
        return 'Reverse rerun';
      case 'request':
        return 'Other';
      default:
        return feature ?? '-';
    }
  }

  kindLabel(kind: string): string {
    if (kind === 'topup') return 'Credits added';
    if (kind === 'adjustment') return 'Adjustment';
    return 'AI call';
  }
}
