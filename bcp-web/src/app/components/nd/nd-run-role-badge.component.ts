import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import {
  analysisRunCurrentRole,
  analysisRunCurrentRoleLabel,
  type AnalysisRunCurrentRole,
} from '../../../lib/nd/analysis-run-status';

@Component({
  selector: 'app-nd-run-role-badge',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (role; as r) {
      <span class="role-badge" [ngClass]="'role-' + r">{{ analysisRunCurrentRoleLabel(r) }}</span>
    } @else {
      <span class="muted">—</span>
    }
  `,
})
export class NdRunRoleBadgeComponent {
  @Input({ required: true }) status = '';

  readonly analysisRunCurrentRoleLabel = analysisRunCurrentRoleLabel;

  get role(): AnalysisRunCurrentRole | null {
    return analysisRunCurrentRole(this.status);
  }
}
