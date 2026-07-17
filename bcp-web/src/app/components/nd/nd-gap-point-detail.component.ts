import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  hasDisplayableFulfilledClauses,
  parseBulletLines,
  parseCapGaps,
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  serializeCapGaps,
  type CapGap,
  type ReferenceComplianceBlock,
} from '../../../lib/ai-lab/parse-reference-response';
import { agreementBadgeClass, type AgreementStatus, type DualVerifyAgreement } from '../../../lib/landing-ai/dual-verify-merge';
import { ReferenceComplianceCardComponent } from '../reference-compliance-card/reference-compliance-card.component';
import type { ActionPlanHistoryEntry, AnalysisPoint, PointSnapshot } from '../../../lib/nd/types';
import { formatDate } from '../../../lib/nd/utils';

@Component({
  selector: 'app-nd-gap-point-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, ReferenceComplianceCardComponent],
  templateUrl: './nd-gap-point-detail.component.html',
  styleUrl: './nd-gap-point-detail.component.scss',
})
export class NdGapPointDetailComponent implements OnChanges {
  @Input({ required: true }) point!: AnalysisPoint;
  @Input() snapshot: PointSnapshot | null = null;
  @Input() canEdit = false;
  @Input() editing = false;
  @Input() saving = false;
  @Input() history: ActionPlanHistoryEntry[] = [];
  @Input() showHistoryPanel = false;
  @Input() policyDocId: string | null = null;
  @Input() regulationDocId: string | null = null;
  @Input() hideReportHeader = false;
  @Input() phaseOutputDefaultOpen = false;

  @Output() startEdit = new EventEmitter<void>();
  @Output() cancelEdit = new EventEmitter<void>();
  @Output() save = new EventEmitter<string>();
  @Output() openHistory = new EventEmitter<void>();
  @Output() closeHistory = new EventEmitter<void>();
  @Output() restoreVersion = new EventEmitter<ActionPlanHistoryEntry>();
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();

  pointHeading = '';
  regulatoryText = '';
  policyExtract = '';
  policyPage: string | null = null;
  policySection: string | null = null;
  policyRefLabel = '';
  policyRefs: { page: string; section: string | null }[] = [];
  landingMessage = '';
  llmMessage = '';
  agreement?: DualVerifyAgreement;
  primaryBlock!: ReferenceComplianceBlock;
  showFulfilled = false;
  fulfilledLines: string[] = [];
  responsibility = '';
  capGaps: CapGap[] = [];
  editGaps: CapGap[] = [];
  originalPlan = '';
  currentPlan = '';
  showCapSection = false;

  ngOnChanges(): void {
    const snap = this.snapshot;
    this.pointHeading = [
      snap?.pointNumber ? `§${snap.pointNumber}` : '',
      snap?.pointTitle ?? '',
    ]
      .filter(Boolean)
      .join(' — ');

    this.regulatoryText =
      snap?.pointContent?.trim() ||
      snap?.pointTitle?.trim() ||
      snap?.pointNumber?.trim() ||
      '—';

    this.landingMessage = this.extractMessage(this.point.landingAiResult);
    this.llmMessage = this.extractMessage(this.point.googleAiResult);
    this.agreement = this.extractAgreement(this.point.googleAiResult);

    const primaryMsg = (this.llmMessage || this.landingMessage).trim();
    this.primaryBlock = parseReferenceComplianceBlock(primaryMsg);
    this.showFulfilled = hasDisplayableFulfilledClauses(this.primaryBlock.fulfilledClauses);
    this.fulfilledLines = parseBulletLines(this.primaryBlock.fulfilledClauses ?? '');
    this.responsibility =
      this.primaryBlock.responsibility && this.primaryBlock.responsibility !== 'N/A'
        ? this.primaryBlock.responsibility
        : '';

    // Policy extract must be the cited quote from the policy document, never a
    // raw AI message dump. Pass 1 (landing) usually carries the citation; fall
    // back to pass 2 only if it also has a structured Output/Response.
    this.policyPage = null;
    this.policySection = null;
    this.policyExtract = '';
    this.policyRefs = [];
    const seenPages = new Set<string>();
    for (const msg of [this.landingMessage, this.llmMessage]) {
      if (!msg?.trim()) continue;
      const structured = parseReferenceComplianceBlock(msg);
      const source = structured.outputResponse?.trim() ?? '';
      if (!source) continue;
      const cite = parseReferenceCitation(source);
      if (cite.page && !seenPages.has(cite.page)) {
        seenPages.add(cite.page);
        this.policyRefs.push({ page: cite.page, section: cite.section });
      }
      if (!this.policyExtract) {
        this.policyExtract = cite.quote?.trim() || source;
        this.policyPage = cite.page;
        this.policySection = cite.section;
      }
    }
    if (!this.policyExtract) {
      this.policyExtract = 'No corresponding policy extract found.';
    }

    const refParts: string[] = [];
    if (this.policyPage) refParts.push(`Page ${this.policyPage}`);
    if (this.policySection) refParts.push(`Section ${this.policySection}`);
    this.policyRefLabel = refParts.join(', ');

    this.originalPlan = this.point.originalAiActionPlan?.trim() ?? '';
    this.currentPlan = this.point.finalActionPlan?.trim() ?? this.originalPlan;
    // No plan saved on the point yet — fall back to the Corrective Action Plan
    // field inside the AI output so the cards still render like dual-verify.
    const aiCap =
      this.primaryBlock.correctiveAction && this.primaryBlock.correctiveAction !== 'N/A'
        ? this.primaryBlock.correctiveAction.trim()
        : '';
    const capSource = this.currentPlan || this.originalPlan || aiCap;
    if (!this.currentPlan) this.currentPlan = aiCap;
    this.capGaps = capSource ? parseCapGaps(capSource) : [];

    const status = (this.point.finalStatus ?? '').toLowerCase();
    this.showCapSection =
      this.capGaps.length > 0 ||
      Boolean(capSource) ||
      status === 'partial_compliant' ||
      status === 'non_compliant';

    if (this.editing) {
      this.editGaps = this.capGaps.length
        ? this.capGaps.map((g) => ({ ...g }))
        : [{ index: 1, missing: '', fix: '' }];
    }
  }

  get displayComplianceStatus(): string {
    const fs = this.point.finalStatus;
    if (fs === 'compliant') return 'Fully Compliant';
    if (fs === 'partial_compliant') return 'Partial Compliant';
    if (fs === 'non_compliant') return 'Non-Compliant';
    return this.primaryBlock.status?.trim() || '—';
  }

  get compliancePillClass(): string {
    const s = (this.point.finalStatus ?? this.primaryBlock.status ?? '').toLowerCase();
    if (s.includes('non')) return 'pill-red';
    if (s.includes('partial')) return 'pill-yellow';
    if (s.includes('compliant')) return 'pill-green';
    return 'pill-neutral';
  }

  extractMessage(raw?: string | null): string {
    if (!raw?.trim()) return '';
    try {
      const parsed = JSON.parse(raw) as { message?: string };
      return parsed.message?.trim() ?? raw;
    } catch {
      return raw;
    }
  }

  extractAgreement(raw?: string | null): DualVerifyAgreement | undefined {
    if (!raw?.trim()) return undefined;
    try {
      const parsed = JSON.parse(raw) as { agreement?: DualVerifyAgreement };
      return parsed.agreement;
    } catch {
      return undefined;
    }
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

  onStartEdit(): void {
    this.startEdit.emit();
  }

  onCancelEdit(): void {
    this.cancelEdit.emit();
  }

  onOpenHistory(): void {
    this.openHistory.emit();
  }

  onCloseHistory(): void {
    this.closeHistory.emit();
  }

  addActionItem(): void {
    const next = this.editGaps.length ? Math.max(...this.editGaps.map((g) => g.index)) + 1 : 1;
    this.editGaps = [...this.editGaps, { index: next, missing: '', fix: '' }];
  }

  removeActionItem(index: number): void {
    this.editGaps = this.editGaps
      .filter((g) => g.index !== index)
      .map((g, i) => ({ ...g, index: i + 1 }));
  }

  onSave(): void {
    const content = serializeCapGaps(this.editGaps.filter((g) => g.missing.trim() || g.fix.trim()));
    if (!content.trim()) return;
    this.save.emit(content);
  }

  onViewRegPdf(): void {
    if (this.regulationDocId) this.openPdf.emit({ docId: this.regulationDocId });
  }

  onViewPolicyPdf(): void {
    if (this.policyDocId) this.openPdf.emit({ docId: this.policyDocId, page: this.policyPage });
  }

  onViewPolicyPage(page: string): void {
    if (this.policyDocId) this.openPdf.emit({ docId: this.policyDocId, page });
  }

  changeTypeLabel(type: string): string {
    if (type === 'ai_original') return 'Original AI draft';
    if (type === 'maker_edit') return 'Maker edit';
    if (type === 'maker_reverted_to_version') return 'Restored version';
    return type.replace(/_/g, ' ');
  }

  historyGaps(content: string): CapGap[] {
    return content?.trim() ? parseCapGaps(content) : [];
  }

  formatDate = formatDate;
}
