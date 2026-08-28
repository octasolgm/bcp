import { Component, OnInit, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NdApiService } from '../../services/nd/nd-api.service';
import {
  ACTION_PLAN_PRIORITY_OPTIONS,
  normalizeActionPlanPriority,
  type ActionPlanPriority,
} from '../../../lib/nd/action-plan';

type PriorityBucket = {
  priority: ActionPlanPriority;
  label: string;
  total: number;
  pending: number;
  resolved: number;
  overdue: number;
  runCount: number;
};

@Component({
  selector: 'app-nd-action-plan-priority-cards',
  standalone: true,
  imports: [RouterLink],
  template: `
    <section class="card-surface priority-section">
      <div class="section-head">
        <h2>Action plan priorities</h2>
        <span class="priority-total">
          {{ total }} action plan(s) · {{ pending }} pending
          @if (overdue > 0) {
            · {{ overdue }} overdue
          }
        </span>
      </div>
      <div class="priority-grid">
        @for (bucket of buckets; track bucket.priority) {
          <a
            class="priority-card"
            [class]="'priority-' + bucket.priority"
            [routerLink]="['/nd/action-plans', bucket.priority]"
          >
            <span class="priority-label">{{ bucket.label }} priority</span>
            <span class="priority-count">{{ bucket.total }}</span>
            <span class="priority-meta">
              {{ bucket.runCount }} analysis · {{ bucket.pending }} pending
              @if (bucket.overdue > 0) {
                · <strong class="priority-overdue">{{ bucket.overdue }} overdue</strong>
              }
            </span>
          </a>
        }
      </div>
    </section>
  `,
  styles: [`
    .priority-section {
      padding: 1.25rem;
      margin-bottom: 1rem;
    }
    .priority-section h2 {
      margin: 0;
      font-size: 0.9375rem;
      font-weight: 600;
    }
    .section-head {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 0.75rem;
      margin-bottom: 1rem;
    }
    .priority-total {
      font-size: 0.75rem;
      color: var(--text-muted);
    }
    .priority-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
      gap: 0.75rem;
    }
    .priority-card {
      display: flex;
      flex-direction: column;
      gap: 0.2rem;
      padding: 0.85rem 0.95rem;
      border: 1px solid var(--border);
      border-left-width: 4px;
      border-radius: 10px;
      background: var(--bg-card);
      text-decoration: none;
      color: inherit;
      transition: border-color 0.15s ease, transform 0.15s ease;
    }
    .priority-card:hover {
      transform: translateY(-1px);
      border-color: var(--accent);
    }
    .priority-card.priority-high { border-left-color: var(--critical); }
    .priority-card.priority-medium { border-left-color: var(--medium); }
    .priority-card.priority-low { border-left-color: var(--low); }
    .priority-label {
      font-size: 0.75rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--text-secondary);
    }
    .priority-count {
      font-size: 1.5rem;
      font-weight: 650;
      line-height: 1.1;
    }
    .priority-meta {
      font-size: 0.6875rem;
      color: var(--text-muted);
    }
    .priority-overdue {
      color: var(--critical);
    }
  `],
})
export class NdActionPlanPriorityCardsComponent implements OnInit {
  private readonly api = inject(NdApiService);

  buckets: PriorityBucket[] = ACTION_PLAN_PRIORITY_OPTIONS.map(({ value, label }) => ({
    priority: value,
    label,
    total: 0,
    pending: 0,
    resolved: 0,
    overdue: 0,
    runCount: 0,
  }));
  total = 0;
  pending = 0;
  overdue = 0;

  async ngOnInit(): Promise<void> {
    const res = await this.api.getActionPlanSummary();
    if (!res.success || !res.data) return;

    this.total = res.data.total;
    this.pending = res.data.pending;
    this.overdue = res.data.overdue;
    const byPriority = new Map(res.data.byPriority.map((b) => [normalizeActionPlanPriority(b.priority), b]));
    this.buckets = ACTION_PLAN_PRIORITY_OPTIONS.map(({ value, label }) => {
      const bucket = byPriority.get(value);
      return {
        priority: value,
        label,
        total: bucket?.total ?? 0,
        pending: bucket?.pending ?? 0,
        resolved: bucket?.resolved ?? 0,
        overdue: bucket?.overdue ?? 0,
        runCount: bucket?.runCount ?? 0,
      };
    });
  }
}
