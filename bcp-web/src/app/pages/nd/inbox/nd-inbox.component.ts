import { ChangeDetectionStrategy, ChangeDetectorRef, Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { NdApiService, type NdActionPlanInbox, type NdInboxFilter, type NdInboxItem } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { NdPageAlertComponent } from '../../../components/nd/nd-page-alert.component';
import {
  actionPlanPriorityClass,
  actionPlanScoreLabel,
  actionPlanStatusLabel,
  formatActionPlanDate,
} from '../../../../lib/nd/action-plan';

type Tab = { id: NdInboxFilter; label: string };

/**
 * Personal bucket of corrective actions. An action lands here when it is assigned to the
 * signed-in user, or to the department their profile belongs to.
 */
@Component({
  selector: 'app-nd-inbox',
  standalone: true,
  imports: [CommonModule, RouterLink, NdPageAlertComponent],
  templateUrl: './nd-inbox.component.html',
  styleUrls: ['./nd-inbox.component.scss', '../nd-shared.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdInboxComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly cdr = inject(ChangeDetectorRef);

  readonly tabs: Tab[] = [
    { id: 'pending', label: 'Pending' },
    { id: 'overdue', label: 'Overdue' },
    { id: 'resolved', label: 'Resolved' },
    { id: 'all', label: 'All' },
  ];

  readonly priorityClass = actionPlanPriorityClass;
  readonly scoreLabel = actionPlanScoreLabel;
  readonly statusLabel = actionPlanStatusLabel;
  readonly formatDate = formatActionPlanDate;

  activeTab: NdInboxFilter = 'pending';
  loading = true;
  error = '';
  data: NdActionPlanInbox | null = null;

  get profileName(): string {
    return this.auth.profile()?.fullName ?? 'You';
  }

  ngOnInit(): void {
    void this.load();
  }

  async setTab(tab: NdInboxFilter): Promise<void> {
    if (this.activeTab === tab) return;
    this.activeTab = tab;
    await this.load();
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    this.cdr.markForCheck();

    const res = await this.api.getActionPlanInbox(this.activeTab);
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not load your actions.';
      this.cdr.markForCheck();
      return;
    }
    this.data = res.data;
    this.cdr.markForCheck();
  }

  get items(): NdInboxItem[] {
    return this.data?.items ?? [];
  }

  countFor(tab: NdInboxFilter): number {
    const c = this.data?.counts;
    if (!c) return 0;
    return tab === 'all' ? c.total : tab === 'pending' ? c.pending : tab === 'resolved' ? c.resolved : c.overdue;
  }

  /** Deep link that opens the report and expands the gap this action belongs to. */
  gapLinkParams(item: NdInboxItem): Record<string, string> {
    return { run: item.analysisRunId, point: item.analysisPointId };
  }

  clauseLabel(item: NdInboxItem): string {
    const no = item.clauseNo?.trim();
    const title = item.clauseTitle?.trim();
    if (no && title) return `${no} · ${title}`;
    return no || title || 'Gap';
  }

  emptyMessage(): string {
    switch (this.activeTab) {
      case 'pending':
        return 'Nothing pending. Actions assigned to you or your department will appear here.';
      case 'overdue':
        return 'No overdue actions. Everything with a target date is still on time.';
      case 'resolved':
        return 'No resolved actions yet.';
      default:
        return 'No actions are assigned to you or your department yet.';
    }
  }
}
