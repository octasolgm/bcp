import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReferenceComplianceCardComponent } from '../reference-compliance-card/reference-compliance-card.component';
import type { DualVerifyReportItem } from '../../../lib/dual-verify-report';
import { agreementBadgeClass, type AgreementStatus } from '../../../lib/landing-ai/dual-verify-merge';
import {
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  resolvePolicyExtractText,
} from '../../../lib/ai-lab/parse-reference-response';
import {
  formatPointPageRef,
  resolveRegulationPdfPage,
} from '../../../lib/nd/regulation-pdf-page';

@Component({
  selector: 'app-dual-verify-result-card',
  standalone: true,
  imports: [CommonModule, ReferenceComplianceCardComponent],
  templateUrl: './dual-verify-result-card.component.html',
  styleUrl: './dual-verify-result-card.component.scss',
})
export class DualVerifyResultCardComponent implements OnChanges {
  @Input({ required: true }) item!: DualVerifyReportItem;
  @Input() govText = '';
  @Input() complianceLabel = '';
  @Input() policyDocId: string | null = null;
  @Input() regulationDocId: string | null = null;
  @Input() regulationPageReference: string | null = null;
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();

  policyExtract = '';
  policyPage: string | null = null;
  policySection: string | null = null;
  regulationPage: number | null = null;
  regulationPageLabel: string | null = null;
  showCap = false;

  ngOnChanges(): void {
    const structured =
      parseReferenceComplianceBlock((this.item.llmMessage || this.item.landingMessage || '').trim());
    const cite = parseReferenceCitation(structured.outputResponse ?? '');
    this.policyPage = cite.page;
    this.policySection = cite.section;
    this.policyExtract =
      resolvePolicyExtractText(structured) ||
      this.item.llmMessage?.trim() ||
      '';
    const label = (this.complianceLabel || structured.status || '').toLowerCase();
    this.showCap =
      label.includes('partial') ||
      label.includes('non') ||
      /\bnon[- ]?compliant\b/.test(label);
    this.regulationPage = resolveRegulationPdfPage(this.regulationPageReference, null);
    this.regulationPageLabel = formatPointPageRef(this.regulationPageReference, this.regulationPage);
  }

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

  get regulatoryText(): string {
    return (
      this.govText?.trim() ||
      this.item.govText?.trim() ||
      this.item.pointTitle?.trim() ||
      this.item.pointId
    );
  }

  get displayComplianceStatus(): string {
    if (this.complianceLabel) return this.complianceLabel;
    const block = parseReferenceComplianceBlock((this.item.landingMessage || '').trim());
    return block.status?.trim() || '—';
  }

  onViewRegPdf(): void {
    if (!this.regulationDocId) return;
    const page = this.regulationPage != null ? String(this.regulationPage) : null;
    this.openPdf.emit({ docId: this.regulationDocId, page });
  }

  onViewPolicyPdf(): void {
    if (this.policyDocId) this.openPdf.emit({ docId: this.policyDocId, page: this.policyPage });
  }

  get hasFindingProof(): boolean {
    return Boolean(this.regulationDocId || this.policyDocId);
  }

  regSourceTooltip(): string {
    const page = this.regulationPageLabel ?? 'start of document';
    return `Open regulation PDF at ${page}`;
  }

  policySourceTooltip(): string {
    return this.policyPage ? `Open policy PDF at p. ${this.policyPage}` : 'Open policy PDF';
  }
}
