import { ChangeDetectionStrategy, ChangeDetectorRef, Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import {
  NdApiService,
  type NdActionPlanMis,
  type NdMisItem,
  type NdMisOwner,
} from '../../services/nd/nd-api.service';
import { formatActionPlanDate } from '../../../lib/nd/action-plan';

/**
 * Workload view over corrective actions: one row per department or person that owns
 * actions, expanding into the actions behind the count. Replaces the old seeded
 * Remediation Tracker, which was not connected to real action plans.
 */
@Component({
  selector: 'app-nd-mis-panel',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './nd-mis-panel.component.html',
  styleUrl: './nd-mis-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdMisPanelComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);

  readonly formatDate = formatActionPlanDate;

  loading = true;
  error = '';
  data: NdActionPlanMis | null = null;
  openKey: string | null = null;

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    this.cdr.markForCheck();

    const res = await this.api.getActionPlanMis();
    this.loading = false;
    if (!res.success || !res.data) {
      this.error = res.message ?? 'Could not load the action workload.';
    } else {
      this.data = res.data;
    }
    this.cdr.markForCheck();
  }

  get owners(): NdMisOwner[] {
    return this.data?.owners ?? [];
  }

  /** Owners with nothing open are noise in a workload view. */
  get activeOwners(): NdMisOwner[] {
    return this.owners.filter((o) => o.pending > 0);
  }

  toggle(key: string): void {
    this.openKey = this.openKey === key ? null : key;
    this.cdr.markForCheck();
  }

  pendingItems(owner: NdMisOwner): NdMisItem[] {
    return owner.items.filter((i) => i.status !== 'resolved');
  }

  ownerLabel(owner: NdMisOwner): string {
    if (owner.type === 'unassigned') return 'Unassigned';
    return `${owner.name} — ${owner.pending} pending action${owner.pending === 1 ? '' : 's'}`;
  }

  ownerKind(owner: NdMisOwner): string {
    return owner.type === 'user' ? 'Person' : owner.type === 'department' ? 'Department' : '—';
  }

  gapLinkParams(item: NdMisItem): Record<string, string> {
    const params: Record<string, string> = {
      run: item.analysisRunId,
      point: item.analysisPointId,
      plan: item.id,
    };
    if (item.gapIndex > 0) params['gap'] = String(item.gapIndex);
    return params;
  }
}
