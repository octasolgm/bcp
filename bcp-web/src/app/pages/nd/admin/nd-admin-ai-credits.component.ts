import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService, type NdAiCreditEntry, type NdAiCreditSummary } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

/**
 * What this workspace has left of its prepaid AI credits, and what spent them. Visible to the
 * workspace's own admin as well as the platform admin; nobody sees another workspace's numbers, and
 * the provider cost behind a credit is never shown here.
 */
@Component({
  selector: 'app-nd-admin-ai-credits',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-admin-ai-credits.component.html',
  styleUrls: ['./nd-admin-ai-credits.component.scss', '../nd-shared.scss'],
})
export class NdAdminAiCreditsComponent implements OnInit {
  private readonly api = inject(NdApiService);
  readonly auth = inject(NdAuthService);

  summary: NdAiCreditSummary | null = null;
  history: NdAiCreditEntry[] = [];
  loading = true;
  error = '';
  /** Filters apply to the activity list only; the balance is always the full picture. */
  kindFilter = '';
  periodFilter: '7d' | '30d' | 'all' = '30d';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    await this.load();
  }

  get workspaceName(): string {
    return this.auth.profile()?.workspace?.name ?? 'this workspace';
  }

  async load(): Promise<void> {
    this.loading = true;
    const [summaryRes, historyRes] = await Promise.all([
      this.api.getMyAiCredits(),
      this.api.getMyAiCreditHistory(50),
    ]);
    if (summaryRes.success && summaryRes.data) this.summary = summaryRes.data;
    else this.error = summaryRes.message ?? 'Could not load AI credits';
    if (historyRes.success && historyRes.data) this.history = historyRes.data;
    this.loading = false;
  }

  get visibleHistory(): NdAiCreditEntry[] {
    const cutoff =
      this.periodFilter === 'all'
        ? 0
        : Date.now() - (this.periodFilter === '7d' ? 7 : 30) * 86400000;
    return this.history.filter((e) => {
      if (this.kindFilter && e.kind !== this.kindFilter) return false;
      return cutoff === 0 || new Date(e.createdAt).getTime() >= cutoff;
    });
  }

  get hasFilters(): boolean {
    return !!this.kindFilter || this.periodFilter !== '30d';
  }

  clearFilters(): void {
    this.kindFilter = '';
    this.periodFilter = '30d';
  }

  /** Width of the used-portion bar, capped so an overspend still renders inside the track. */
  get usedBarPct(): number {
    if (!this.summary || this.summary.unlimited) return 0;
    return Math.min(100, Math.max(0, this.summary.usedPct));
  }

  get statusKind(): 'ok' | 'low' | 'empty' | 'unlimited' {
    if (!this.summary || this.summary.unlimited) return 'unlimited';
    if (this.summary.isExhausted) return 'empty';
    if (this.summary.isLow) return 'low';
    return 'ok';
  }

  entryLabel(e: NdAiCreditEntry): string {
    if (e.kind === 'topup') return 'Credits added';
    if (e.kind === 'adjustment') return 'Adjustment';
    return e.model ? `Analysis - ${e.model}` : 'Analysis';
  }
}
