import {
  Component,
  EventEmitter,
  Input,
  OnChanges,
  OnInit,
  Output,
  SimpleChanges,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  RUN_REVIEW_STATUS_OPTIONS,
  emptyRunReviewDraft,
  type RunReviewDraft,
  type RunReviewStatus,
} from '../../../lib/nd/run-review';
import {
  ACTION_PLAN_PRIORITY_SCALE,
  actionPlanPriorityClass,
  actionPlanPriorityFromScore,
  actionPlanScoreLabel,
} from '../../../lib/nd/action-plan';
import type { PointGapAttachment } from '../../../lib/nd/types';

export type RunReviewPanelMode = 'none' | 'maker' | 'checker' | 'reviewer';

export type RunReviewSubmitEvent = {
  action: 'submit' | 'approve' | 'pullback' | 'finalize' | 'pullback_to_checker' | 'pullback_to_maker';
  draft: RunReviewDraft;
};

export type SubmitTargetOption = {
  role: 'maker' | 'checker' | 'reviewer';
  label: string;
  action: RunReviewSubmitEvent['action'];
};

/** Who a report can be sent to from each role — the sender's own role is never an option. */
const SUBMIT_TARGETS: Record<Exclude<RunReviewPanelMode, 'none'>, SubmitTargetOption[]> = {
  maker: [{ role: 'checker', label: 'Checker', action: 'submit' }],
  checker: [
    { role: 'reviewer', label: 'Reviewer', action: 'approve' },
    { role: 'maker', label: 'Maker', action: 'pullback' },
  ],
  reviewer: [
    { role: 'checker', label: 'Checker', action: 'pullback_to_checker' },
    { role: 'maker', label: 'Maker', action: 'pullback_to_maker' },
  ],
};

@Component({
  selector: 'app-nd-run-review-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-run-review-panel.component.html',
  styleUrl: './nd-run-review-panel.component.scss',
})
export class NdRunReviewPanelComponent implements OnInit, OnChanges {
  @Input({ required: true }) mode: RunReviewPanelMode = 'none';
  @Input() submitting = false;
  @Input() error = '';
  @Input() reviewProgress: { total: number; reviewed: number } | null = null;
  @Input() initialDraft: Partial<RunReviewDraft> | null = null;
  @Input() resubmitLabel = false;
  @Input() reportAttachments: PointGapAttachment[] = [];
  @Input() canUploadEvidence = false;
  @Input() evidenceUploading = false;
  @Input() evidenceRerunning = false;
  @Input() evidenceDeletingId: string | null = null;
  /** When true, only the gap-document upload/rerun block is shown (no overall review form). */
  @Input() evidenceOnly = false;
  /** Shows the Export Excel button in the panel header when the caller supports it. */
  @Input() canExport = false;
  @Input() exporting = false;

  @Output() submitReview = new EventEmitter<RunReviewSubmitEvent>();
  @Output() uploadEvidence = new EventEmitter<FileList>();
  @Output() deleteEvidence = new EventEmitter<string>();
  @Output() viewEvidence = new EventEmitter<string>();
  @Output() rerunAllGaps = new EventEmitter<void>();
  @Output() exportExcel = new EventEmitter<void>();

  draft: RunReviewDraft = emptyRunReviewDraft();
  statusOptions = RUN_REVIEW_STATUS_OPTIONS;
  readonly priorityScale = ACTION_PLAN_PRIORITY_SCALE;

  pendingRemoveId: string | null = null;

  /** Who this report can be sent to from the current role — never includes the sender's own role. */
  submitTargets: SubmitTargetOption[] = [];
  submitTargetRole = '';

  ngOnInit(): void {
    this.applyInitialDraft();
    this.syncSubmitTargets();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['initialDraft']) this.applyInitialDraft();
    if (changes['mode']) this.syncSubmitTargets();
    if (
      this.pendingRemoveId &&
      !this.uniqueReportAttachments.some((a) => a.storedDocumentId === this.pendingRemoveId)
    ) {
      this.pendingRemoveId = null;
    }
  }

  private syncSubmitTargets(): void {
    this.submitTargets = this.mode === 'none' ? [] : SUBMIT_TARGETS[this.mode];
    this.submitTargetRole = this.submitTargets[0]?.role ?? '';
  }

  get selectedSubmitTarget(): SubmitTargetOption | undefined {
    return this.submitTargets.find((t) => t.role === this.submitTargetRole);
  }

  submitToSelectedTarget(): void {
    const target = this.selectedSubmitTarget;
    if (target) this.emit(target.action);
  }

  private applyInitialDraft(): void {
    if (!this.initialDraft) return;
    this.draft = { ...emptyRunReviewDraft(), ...this.initialDraft };
  }

  get uniqueReportAttachments(): PointGapAttachment[] {
    const seen = new Set<string>();
    const out: PointGapAttachment[] = [];
    for (const att of this.reportAttachments) {
      if (seen.has(att.storedDocumentId)) continue;
      seen.add(att.storedDocumentId);
      out.push(att);
    }
    return out;
  }

  fileKind(fileName: string): string {
    const ext = fileName.split('.').pop()?.toUpperCase() ?? 'FILE';
    if (ext === 'DOCX') return 'DOC';
    return ext.slice(0, 4) || 'FILE';
  }

  fileMeta(att: PointGapAttachment): string {
    const kind = this.fileKind(att.fileName);
    const size = formatAttachmentSize(att.sizeBytes);
    return size ? `${kind} · ${size}` : kind;
  }

  isPendingRemove(id: string): boolean {
    return this.pendingRemoveId === id;
  }

  isRemoving(id: string): boolean {
    return this.evidenceDeletingId === id;
  }

  askRemove(id: string): void {
    this.pendingRemoveId = id;
  }

  cancelRemove(): void {
    this.pendingRemoveId = null;
  }

  confirmRemove(id: string): void {
    this.pendingRemoveId = null;
    this.deleteEvidence.emit(id);
  }

  get fallbackStatusLabel(): string {
    return this.draft.status.replace(/_/g, ' ');
  }

  get showLegacyStatusOption(): boolean {
    return Boolean(this.draft.status && !this.statusOptions.some((o) => o.id === this.draft.status));
  }

  get priorityScoreLabel(): string {
    return actionPlanScoreLabel(this.draft.priority);
  }

  get priorityTierClass(): string {
    return actionPlanPriorityClass(actionPlanPriorityFromScore(this.draft.priority));
  }

  get showReviewerActions(): boolean {
    return this.mode === 'reviewer';
  }

  get reviewProgressComplete(): boolean {
    if (!this.reviewProgress || this.reviewProgress.total === 0) return true;
    return this.reviewProgress.reviewed >= this.reviewProgress.total;
  }

  get showActionReviewProgress(): boolean {
    if (this.mode === 'maker') return false;
    return !!this.reviewProgress && this.reviewProgress.total > 0;
  }

  setStatus(status: RunReviewStatus): void {
    this.draft = { ...this.draft, status };
  }

  setPriority(value: number): void {
    this.draft = { ...this.draft, priority: Math.min(100, Math.max(0, value)) };
  }

  onReportFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.uploadEvidence.emit(input.files);
    input.value = '';
  }

  emit(action: RunReviewSubmitEvent['action']): void {
    this.submitReview.emit({ action, draft: { ...this.draft } });
  }
}

function formatAttachmentSize(bytes?: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
