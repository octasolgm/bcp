import { ChangeDetectionStrategy, ChangeDetectorRef, Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import {
  NdApiService,
  type NdActionPlanInbox,
  type NdInboxFilter,
  type NdInboxItem,
  type NdReviewInbox,
  type NdReviewInboxItem,
} from '../../../services/nd/nd-api.service';
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
  reviews: NdReviewInbox | null = null;

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

    const [res, reviewRes] = await Promise.all([
      this.api.getActionPlanInbox(this.activeTab),
      this.api.getActionPlanReviewInbox(),
    ]);
    this.loading = false;

    if (reviewRes.success && reviewRes.data) this.reviews = reviewRes.data;

    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not load your actions.';
      this.cdr.markForCheck();
      return;
    }
    this.data = res.data;
    this.cdr.markForCheck();
  }

  get reviewItems(): NdReviewInboxItem[] {
    return this.reviews?.items ?? [];
  }

  reviewDirectionLabel(review: NdReviewInboxItem): string {
    if (review.direction === 'sent') return 'You sent this';
    if (review.direction === 'self') return 'Sent to you by you';
    return 'For you to review';
  }

  reviewLinkParams(review: NdReviewInboxItem): Record<string, string> {
    const params: Record<string, string> = {
      run: review.analysisRunId,
      point: review.analysisPointId,
      plan: review.actionPlanId,
    };
    if (review.gapIndex > 0) params['gap'] = String(review.gapIndex);
    return params;
  }

  get items(): NdInboxItem[] {
    return this.data?.items ?? [];
  }

  countFor(tab: NdInboxFilter): number {
    const c = this.data?.counts;
    if (!c) return 0;
    return tab === 'all' ? c.total : tab === 'pending' ? c.pending : tab === 'resolved' ? c.resolved : c.overdue;
  }

  /** Deep link that opens the report and expands the gap and action card this row is for. */
  gapLinkParams(item: NdInboxItem): Record<string, string> {
    const params: Record<string, string> = {
      run: item.analysisRunId,
      point: item.analysisPointId,
      plan: item.id,
    };
    if (item.gapIndex > 0) params['gap'] = String(item.gapIndex);
    return params;
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
