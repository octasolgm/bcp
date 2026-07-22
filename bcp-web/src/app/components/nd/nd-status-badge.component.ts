import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { complianceSeverityLabel, type ComplianceSeverity } from '../../../lib/nd/point-compliance-status';
import {
  analysisRunDisplayStatusLabel,
  isAnalysisRunSubmitReviewPending,
  normalizeRunStatus,
} from '../../../lib/nd/analysis-run-status';

@Component({
  selector: 'app-nd-status-badge',
  standalone: true,
  imports: [CommonModule],
  template: `<span class="nd-badge" [ngClass]="badgeClass">{{ label }}</span>`,
})
export class NdStatusBadgeComponent {
  @Input({ required: true }) status!: string;

  get label(): string {
    const normalized = normalizeRunStatus(this.status);
    if (
      normalized === 'compliant' ||
      normalized === 'partial_compliant' ||
      normalized === 'non_compliant'
    ) {
      return complianceSeverityLabel(normalized as ComplianceSeverity);
    }
    return analysisRunDisplayStatusLabel(normalized);
  }

  get badgeClass(): string {
    const key = normalizeRunStatus(this.status);
    if (isAnalysisRunSubmitReviewPending(key)) return 'nd-badge-amber';
    const map: Record<string, string> = {
      completed: 'nd-badge-green',
      compliant: 'nd-badge-green',
      checker_approved: 'nd-badge-green',
      reviewer_approved: 'nd-badge-green',
      pending: 'nd-badge-gray',
      draft: 'nd-badge-gray',
      processing: 'nd-badge-blue',
      running: 'nd-badge-blue',
      submitted_for_review: 'nd-badge-blue',
      partial_compliant: 'nd-badge-amber',
      dual_verify_failed: 'nd-badge-amber',
      landing_ai_complete: 'nd-badge-green',
      failed: 'nd-badge-red',
      non_compliant: 'nd-badge-red',
      pulled_back: 'nd-badge-red',
      super_admin: 'nd-badge-blue',
      active: 'nd-badge-green',
      deactivated: 'nd-badge-red',
      pending_invitation: 'nd-badge-amber',
      checker: 'nd-badge-amber',
      reviewer: 'nd-badge-blue',
    };
    return map[key] ?? 'nd-badge-gray';
  }
}
