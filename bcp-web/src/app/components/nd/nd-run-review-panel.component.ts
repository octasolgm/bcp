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
import type { GapEvidencePrepStep } from '../../../lib/nd/gap-evidence-activity';
import { gapEvidenceNeedsManualPrepare } from '../../../lib/nd/gap-evidence-local-pipeline';

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
  @Input() evidenceUploadDisabledHint = '';
  /** Live prepare labels keyed by storedDocumentId (parse / extract / ready). */
  @Input() evidencePrepLabels: Record<string, string> = {};
  @Input() evidencePrepSteps: Record<string, GapEvidencePrepStep> = {};
  /** Stored document ids currently running parse/extract/index for this report. */
  @Input() evidencePreparingDocIds: ReadonlySet<string> = new Set();
  @Input() evidenceUploading = false;
  @Input() evidenceRerunning = false;
  @Input() evidenceDeletingId: string | null = null;
  /** When true, only the gap-document upload/rerun block is shown (no overall review form). */
  @Input() evidenceOnly = false;
  /** When set (super admin), replaces role-default submit targets. */
  @Input() workflowSubmitTargets: SubmitTargetOption[] | null = null;
  /** Show Finalize (reviewer / super admin). */
  @Input() showFinalizeAction = false;
  @Output() submitReview = new EventEmitter<RunReviewSubmitEvent>();
  @Output() uploadEvidence = new EventEmitter<FileList>();
  @Output() deleteEvidence = new EventEmitter<string>();
  @Output() viewEvidence = new EventEmitter<string>();
  @Output() rerunAllGaps = new EventEmitter<void>();
  @Output() prepareEvidence = new EventEmitter<string>();

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
    if (changes['mode'] || changes['workflowSubmitTargets']) this.syncSubmitTargets();
    if (
      this.pendingRemoveId &&
      !this.uniqueReportAttachments.some((a) => a.storedDocumentId === this.pendingRemoveId)
    ) {
      this.pendingRemoveId = null;
    }
  }

  private syncSubmitTargets(): void {
    if (this.workflowSubmitTargets?.length) {
      this.submitTargets = this.workflowSubmitTargets;
    } else {
      this.submitTargets = this.mode === 'none' ? [] : SUBMIT_TARGETS[this.mode];
    }
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
    const prep = this.evidencePrepLabels[att.storedDocumentId]?.trim();
    if (prep) return prep;
    const kind = this.fileKind(att.fileName);
    const size = formatAttachmentSize(att.sizeBytes);
    const parse = formatParseStatus(att.parseStatus);
    const uploaded = formatAttachmentUploadedAt(att.createdAt);
    const parts = [kind, size, uploaded, parse].filter(Boolean);
    return parts.join(' · ');
  }

  prepStepFor(att: PointGapAttachment): GapEvidencePrepStep | undefined {
    return this.evidencePrepSteps[att.storedDocumentId];
  }

  prepStepIndex(att: PointGapAttachment): number {
    const step = this.prepStepFor(att);
    if (step) return prepStepRailIndex(step);
    const label = (this.evidencePrepLabels[att.storedDocumentId] ?? '').toLowerCase();
    if (/ready for gap/i.test(label)) return 4;
    if (/index|search index/i.test(label)) return 3;
    if (/structural|extract/i.test(label)) return 2;
    if (/azure|pars/i.test(label)) return 1;
    if (/upload/i.test(label)) return 0;
    if (/not yet parsed/i.test(label)) return 0;
    return -1;
  }

  showPrepStepRail(att: PointGapAttachment): boolean {
    const step = this.prepStepFor(att);
    if (step && step !== 'not_started') return true;
    return this.prepStepIndex(att) >= 0;
  }

  canPrepareEvidence(att: PointGapAttachment): boolean {
    if (!this.canUploadEvidence) return false;
    if (this.evidencePreparingDocIds.has(att.storedDocumentId)) return false;
    const step = this.prepStepFor(att);
    if (step) return gapEvidenceNeedsManualPrepare(step);
    const ps = (att.parseStatus ?? '').trim().toLowerCase();
    if (ps === 'failed') return true;
    if (ps === 'parsed' || ps === 'completed') return false;
    if (ps === 'processing' || ps === 'pending') return false;
    return true;
  }

  isPreparingEvidence(att: PointGapAttachment): boolean {
    return this.evidencePreparingDocIds.has(att.storedDocumentId);
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
    return this.showFinalizeAction || this.mode === 'reviewer';
  }

  get reviewProgressComplete(): boolean {
    if (!this.reviewProgress || this.reviewProgress.total === 0) return true;
    return this.reviewProgress.reviewed >= this.reviewProgress.total;
  }

  get showActionReviewProgress(): boolean {
    if (this.workflowSubmitTargets?.length) {
      return !!this.reviewProgress && this.reviewProgress.total > 0;
    }
    if (this.mode === 'maker') return false;
    return !!this.reviewProgress && this.reviewProgress.total > 0;
  }

  setStatus(status: RunReviewStatus): void {
    this.draft = { ...this.draft, status };
  }

  setPriority(value: number): void {
    this.draft = { ...this.draft, priority: Math.min(100, Math.max(0, value)) };
  }

  onUploadClick(input: HTMLInputElement): void {
    if (!this.canUploadEvidence || this.evidenceUploading) return;
    input.click();
  }

  onReportFilesSelected(event: Event): void {
    if (!this.canUploadEvidence) return;
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.uploadEvidence.emit(input.files);
    input.value = '';
  }

  emit(action: RunReviewSubmitEvent['action']): void {
    this.submitReview.emit({ action, draft: { ...this.draft } });
  }
}

function prepStepRailIndex(step: GapEvidencePrepStep): number {
  switch (step) {
    case 'uploading':
      return 0;
    case 'not_started':
      return -1;
    case 'parsing':
      return 1;
    case 'extracting':
      return 2;
    case 'indexing':
      return 3;
    case 'ready':
      return 4;
    case 'failed':
      return 1;
  }
}

function formatParseStatus(status?: string | null): string {
  const s = (status ?? '').trim().toLowerCase();
  if (!s) return 'Not yet parsed or extracted';
  if (s === 'parsed' || s === 'completed') return 'Ready for gap re-analysis';
  if (s === 'processing' || s === 'pending') return 'Azure parse…';
  if (s === 'failed') return 'Prepare failed — retry upload';
  return s.replace(/_/g, ' ');
}

function formatAttachmentUploadedAt(iso?: string | null): string {
  if (!iso?.trim()) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
}

function formatAttachmentSize(bytes?: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
