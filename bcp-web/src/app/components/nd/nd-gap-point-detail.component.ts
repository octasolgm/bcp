import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  parseCapGaps,
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  serializeCapGaps,
  type CapGap,
} from '../../../lib/ai-lab/parse-reference-response';
import type { ActionPlanHistoryEntry, AnalysisPoint, PointSnapshot } from '../../../lib/nd/types';
import { formatDate } from '../../../lib/nd/utils';

@Component({
  selector: 'app-nd-gap-point-detail',
  standalone: true,
  imports: [CommonModule, FormsModule],
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

  @Output() startEdit = new EventEmitter<void>();
  @Output() cancelEdit = new EventEmitter<void>();
  @Output() save = new EventEmitter<string>();
  @Output() openHistory = new EventEmitter<void>();
  @Output() closeHistory = new EventEmitter<void>();
  @Output() restoreVersion = new EventEmitter<ActionPlanHistoryEntry>();
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();

  regulatoryText = '';
  policyExtract = '';
  policyPage: string | null = null;
  policySection: string | null = null;
  policyRefLabel = '';
  capGaps: CapGap[] = [];
  editGaps: CapGap[] = [];
  originalPlan = '';
  currentPlan = '';

  ngOnChanges(): void {
    this.regulatoryText =
      this.snapshot?.pointContent?.trim() ||
      this.snapshot?.pointTitle?.trim() ||
      this.snapshot?.pointNumber?.trim() ||
      '—';

    const policyMsg = this.aiMessage(this.point.googleAiResult) || this.aiMessage(this.point.landingAiResult);
    const structured = parseReferenceComplianceBlock(policyMsg);
    const cite = parseReferenceCitation(structured.outputResponse ?? '');
    this.policyPage = cite.page;
    this.policySection = cite.section;
    this.policyExtract =
      cite.quote?.trim() || structured.outputResponse?.trim() || policyMsg || '—';

    const refParts: string[] = [];
    if (this.policyPage) refParts.push(`Page ${this.policyPage}`);
    if (this.policySection) refParts.push(`Section ${this.policySection}`);
    this.policyRefLabel = refParts.join(', ');

    this.originalPlan = this.point.originalAiActionPlan?.trim() ?? '';
    this.currentPlan = this.point.finalActionPlan?.trim() ?? this.originalPlan;
    const capSource = this.currentPlan || this.originalPlan;
    this.capGaps = capSource ? parseCapGaps(capSource) : [];

    if (this.editing) {
      this.editGaps = this.capGaps.length
        ? this.capGaps.map((g) => ({ ...g }))
        : [{ index: 1, missing: '', fix: '' }];
    }
  }

  get showGapsSection(): boolean {
    const status = (this.point.finalStatus ?? '').toLowerCase();
    return (
      this.capGaps.length > 0 ||
      Boolean(this.currentPlan) ||
      status === 'partial_compliant' ||
      status === 'non_compliant'
    );
  }

  aiMessage(raw?: string | null): string {
    if (!raw?.trim()) return '';
    try {
      const parsed = JSON.parse(raw) as { message?: string };
      return parsed.message?.trim() ?? raw;
    } catch {
      return raw;
    }
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
