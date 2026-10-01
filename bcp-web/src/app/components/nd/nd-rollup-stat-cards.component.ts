import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import type { ClauseRollup } from '../../../lib/nd/gap-state';

/** Gap / action tallies in the same card layout as compliance summary cards (ss2). */
@Component({
  selector: 'app-nd-rollup-stat-cards',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './nd-rollup-stat-cards.component.html',
  styleUrl: './nd-rollup-stat-cards.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdRollupStatCardsComponent {
  @Input({ required: true }) rollup!: ClauseRollup;
  /** Whole-report gap/action counts from rollup, or checker review extras (pending points, overdue). */
  @Input() variant: 'report' | 'review' = 'report';
  @Input() overdueActionPlans = 0;
}
