import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReferenceComplianceCardComponent } from '../reference-compliance-card/reference-compliance-card.component';
import type { DualVerifyReportItem } from '../../../lib/dual-verify-report';
import { agreementBadgeClass, type AgreementStatus } from '../../../lib/landing-ai/dual-verify-merge';

@Component({
  selector: 'app-dual-verify-result-card',
  standalone: true,
  imports: [CommonModule, ReferenceComplianceCardComponent],
  templateUrl: './dual-verify-result-card.component.html',
  styleUrl: './dual-verify-result-card.component.scss',
})
export class DualVerifyResultCardComponent {
  @Input({ required: true }) item!: DualVerifyReportItem;

  agreementClass(status?: string): string {
    if (!status) return 'agreement-neutral';
    const map: Record<string, string> = {
      aligned: 'agreement-aligned',
      confidence_gap: 'agreement-gap',
      status_mismatch: 'agreement-mismatch',
      both_non_compliant: 'agreement-warn',
    };
    return map[status] ?? agreementBadgeClass(status as AgreementStatus).split(' ')[0] ?? 'agreement-neutral';
  }
}
