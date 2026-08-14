import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { formatDate } from '../../../../lib/nd/utils';
import {
  actionPlanPriorityLabel,
  formatActionPlanDate,
  normalizeActionPlanPriority,
  type ActionPlanPriority,
} from '../../../../lib/nd/action-plan';

type PriorityRunRow = {
  runId: string;
  runName: string;
  runStatus: string;
  workflowEngine: string;
  createdAt: string;
  createdByName?: string | null;
  actionPlanCount: number;
  pendingCount: number;
  resolvedCount: number;
  overdueCount: number;
  gapCount: number;
  nextTargetDate?: string | null;
  pointIds: string[];
};

@Component({
  selector: 'app-nd-action-plan-priority',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './nd-action-plan-priority.component.html',
  styleUrls: ['./nd-action-plan-priority.component.scss', '../nd-shared.scss'],
})
export class NdActionPlanPriorityComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly auth = inject(NdAuthService);

  private sub?: Subscription;

  loading = true;
  error = '';
  priority: ActionPlanPriority = 'low';
  statusFilter: 'all' | 'pending' | 'resolved' = 'all';
  rows: PriorityRunRow[] = [];

  formatDate = formatDate;
  formatTargetDate = formatActionPlanDate;

  ngOnInit(): void {
    this.sub = this.route.paramMap.subscribe((params) => {
      this.priority = normalizeActionPlanPriority(params.get('priority'));
      void this.load();
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  get priorityLabel(): string {
    return actionPlanPriorityLabel(this.priority);
  }

  get totalPlans(): number {
    return this.rows.reduce((sum, r) => sum + r.actionPlanCount, 0);
  }

  async load(): Promise<void> {
    this.loading = true;
    this.error = '';
    const res = await this.api.getActionPlansByPriority(
      this.priority,
      this.statusFilter === 'all' ? undefined : this.statusFilter,
    );
    if (!res.success || !res.data) {
      this.error = res.message || 'Could not load action plans.';
      this.rows = [];
    } else {
      this.rows = res.data.runs;
    }
    this.loading = false;
  }

  async setStatusFilter(value: 'all' | 'pending' | 'resolved'): Promise<void> {
    if (this.statusFilter === value) return;
    this.statusFilter = value;
    await this.load();
  }

  /** Opens the gap report filtered to the gaps that carry action plans of this priority. */
  openRun(row: PriorityRunRow): void {
    void this.router.navigate(['/nd/gap-analysis'], {
      queryParams: {
        run: row.runId,
        apPriority: this.priority,
        apStatus: this.statusFilter === 'all' ? null : this.statusFilter,
      },
    });
  }
}
