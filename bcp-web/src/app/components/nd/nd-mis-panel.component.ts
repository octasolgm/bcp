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

/** Which cut of the same action data the panel is showing. */
export type MisView = 'plans' | 'owners';

/** Clause order first (e.g. "2.10" after "2.2"), soonest target date breaks ties within a clause. */
function sortByClause(a: NdMisItem, b: NdMisItem): number {
  const clauseCmp = (a.clauseNo ?? '').localeCompare(b.clauseNo ?? '', undefined, { numeric: true });
  if (clauseCmp !== 0) return clauseCmp;
  return (a.targetDate ?? '9999').localeCompare(b.targetDate ?? '9999');
}

/** An action plan plus the owners it is assigned to, for the by-action-plan view. */
type MisPlanRow = NdMisItem & { owners: string[] };

/**
 * Two views over the same corrective actions: one listing the actions themselves, and
 * one grouping them by the department or person who owns them. Replaces the old seeded
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

  view: MisView = 'plans';
  /** Filter applied by clicking one of the three total boxes. */
  statusFilter: 'all' | 'pending' | 'resolved' = 'all';

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

  setView(view: MisView): void {
    this.view = view;
    this.openKey = null;
    this.cdr.markForCheck();
  }

  setStatusFilter(filter: 'all' | 'pending' | 'resolved'): void {
    this.statusFilter = this.statusFilter === filter ? 'all' : filter;
    this.cdr.markForCheck();
  }

  // ------------------------------------------------------------- totals

  get totalCount(): number {
    return this.data?.totals.total ?? 0;
  }

  get pendingCount(): number {
    return this.data?.totals.pending ?? 0;
  }

  get resolvedCount(): number {
    return this.data?.totals.resolved ?? 0;
  }

  get overdueCount(): number {
    return this.data?.totals.overdue ?? 0;
  }

  // ------------------------------------------------ by action plan view

  /**
   * Every action once, with the owners it is shared by. The API groups by owner, so an
   * action assigned to two departments arrives twice and is merged back here.
   */
  get planRows(): MisPlanRow[] {
    const byId = new Map<string, MisPlanRow>();
    for (const owner of this.owners) {
      const ownerName = owner.type === 'unassigned' ? 'Unassigned' : owner.name;
      for (const item of owner.items) {
        const existing = byId.get(item.id);
        if (existing) {
          if (!existing.owners.includes(ownerName)) existing.owners.push(ownerName);
        } else {
          byId.set(item.id, { ...item, owners: [ownerName] });
        }
      }
    }
    return [...byId.values()]
      .filter((row) => this.matchesStatusFilter(row.status))
      .sort((a, b) => sortByClause(a, b));
  }

  private matchesStatusFilter(status: string): boolean {
    if (this.statusFilter === 'all') return true;
    if (this.statusFilter === 'resolved') return status === 'resolved';
    return status !== 'resolved';
  }

  ownersLabel(row: MisPlanRow): string {
    if (!row.owners.length) return 'Unassigned';
    if (row.owners.length === 1) return row.owners[0];
    return `${row.owners[0]} +${row.owners.length - 1}`;
  }

  /** Short enough to scan in a list; the full text is on the report. */
  shortPlan(text: string): string {
    const t = (text ?? '').trim();
    return t.length > 120 ? `${t.slice(0, 120)}…` : t;
  }

  // ----------------------------------------------------- by owner view

  get owners(): NdMisOwner[] {
    return this.data?.owners ?? [];
  }

  /**
   * Owners to list. Under the pending filter only those with open work appear, which is
   * the workload question; otherwise everyone holding an action is listed.
   */
  get visibleOwners(): NdMisOwner[] {
    if (this.statusFilter === 'pending') return this.owners.filter((o) => o.pending > 0);
    if (this.statusFilter === 'resolved') return this.owners.filter((o) => o.resolved > 0);
    return this.owners.filter((o) => o.total > 0);
  }

  toggle(key: string): void {
    this.openKey = this.openKey === key ? null : key;
    this.cdr.markForCheck();
  }

  itemsFor(owner: NdMisOwner): NdMisItem[] {
    return owner.items.filter((i) => this.matchesStatusFilter(i.status)).sort((a, b) => sortByClause(a, b));
  }

  ownerLabel(owner: NdMisOwner): string {
    return owner.type === 'unassigned' ? 'Unassigned' : owner.name;
  }

  ownerKind(owner: NdMisOwner): string {
    return owner.type === 'user' ? 'Person' : owner.type === 'department' ? 'Department' : '—';
  }

  isResolved(status: string): boolean {
    return status === 'resolved';
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
