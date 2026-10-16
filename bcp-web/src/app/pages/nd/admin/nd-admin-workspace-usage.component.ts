import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { NdApiService, type NdMyAiUsageReport } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { aiFeatureLabel, aiKindLabel } from './nd-ai-usage-labels';

type PeriodKey = '7d' | '30d' | 'month' | 'all' | 'custom';

/**
 * A workspace admin's own AI usage: the same breakdowns as the platform AI usage page, locked to this
 * workspace by the server. Shows what the workspace was billed, never our provider cost or margin.
 */
@Component({
  selector: 'app-nd-admin-workspace-usage',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './nd-admin-workspace-usage.component.html',
  styleUrls: ['./nd-admin-ai-usage.component.scss', './nd-admin-modern.scss', '../nd-shared.scss'],
})
export class NdAdminWorkspaceUsageComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  report: NdMyAiUsageReport | null = null;
  loading = true;
  error = '';

  period: PeriodKey = '30d';
  customFrom = '';
  customTo = '';
  model = '';
  feature = '';
  kind = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get workspaceName(): string {
    return this.auth.profile()?.workspace?.name ?? 'this workspace';
  }

  get hasFilters(): boolean {
    return !!(this.model || this.feature || this.kind) || this.period !== '30d';
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
    const res = await this.api.getMyAiUsageReport({
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

  async clearFilters(): Promise<void> {
    this.period = '30d';
    this.customFrom = '';
    this.customTo = '';
    this.model = '';
    this.feature = '';
    this.kind = '';
    await this.load();
  }

  featureLabel(feature: string | null | undefined): string {
    return aiFeatureLabel(feature);
  }

  kindLabel(kind: string): string {
    return aiKindLabel(kind);
  }
}
