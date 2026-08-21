import {
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  OnInit,
  Output,
  SimpleChanges,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService } from '../../services/nd/nd-api.service';
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
  sameAssignee,
  type ActionPlanAssignee,
  type ActionPlanResponsibilityOptions,
  type ActionPlanResponsibilityType,
} from '../../../lib/nd/action-plan';
import type { PointGapAttachment } from '../../../lib/nd/types';

export type RunReviewPanelMode = 'none' | 'maker' | 'checker' | 'reviewer';

export type RunReviewSubmitEvent = {
  action: 'submit' | 'approve' | 'pullback' | 'finalize' | 'pullback_to_checker' | 'pullback_to_maker';
  draft: RunReviewDraft;
};

@Component({
  selector: 'app-nd-run-review-panel',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-run-review-panel.component.html',
  styleUrl: './nd-run-review-panel.component.scss',
})
export class NdRunReviewPanelComponent implements OnInit, OnChanges {
  private readonly ndApi = inject(NdApiService);
  private readonly host = inject(ElementRef<HTMLElement>);

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

  @Output() submitReview = new EventEmitter<RunReviewSubmitEvent>();
  @Output() uploadEvidence = new EventEmitter<FileList>();
  @Output() deleteEvidence = new EventEmitter<string>();
  @Output() viewEvidence = new EventEmitter<string>();
  @Output() rerunAllGaps = new EventEmitter<void>();

  draft: RunReviewDraft = emptyRunReviewDraft();
  statusOptions = RUN_REVIEW_STATUS_OPTIONS;
  readonly priorityScale = ACTION_PLAN_PRIORITY_SCALE;

  options: ActionPlanResponsibilityOptions = { departments: [], users: [] };
  responsibilityType: ActionPlanResponsibilityType = 'department';
  assignees: ActionPlanAssignee[] = [];
  ownerQuery = '';
  ownerSuggestionsOpen = false;
  pendingRemoveId: string | null = null;

  async ngOnInit(): Promise<void> {
    this.applyInitialDraft();
    const res = await this.ndApi.getActionPlanResponsibilityOptions();
    if (res.success && res.data) this.options = res.data;
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['initialDraft']) this.applyInitialDraft();
    if (
      this.pendingRemoveId &&
      !this.uniqueReportAttachments.some((a) => a.storedDocumentId === this.pendingRemoveId)
    ) {
      this.pendingRemoveId = null;
    }
  }

  private applyInitialDraft(): void {
    if (!this.initialDraft) return;
    this.draft = { ...emptyRunReviewDraft(), ...this.initialDraft };
    const labels = (this.draft.responsibility ?? '')
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
    this.assignees = labels.map((label) => ({ type: this.responsibilityType, label }));
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

  get showCheckerActions(): boolean {
    return this.mode === 'checker';
  }

  get showReviewerActions(): boolean {
    return this.mode === 'reviewer';
  }

  get showMakerActions(): boolean {
    return this.mode === 'maker';
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

  setResponsibilityType(value: ActionPlanResponsibilityType): void {
    this.responsibilityType = value;
    this.ownerQuery = '';
    this.ownerSuggestionsOpen = false;
  }

  get ownerSuggestions(): ActionPlanAssignee[] {
    const q = this.ownerQuery.trim().toLowerCase();
    const pool: ActionPlanAssignee[] =
      this.responsibilityType === 'user'
        ? this.options.users.map((u) => ({
            type: 'user' as const,
            userId: u.id,
            label: u.email ? `${u.fullName} — ${u.email}` : u.fullName,
          }))
        : this.options.departments.map((d) => ({
            type: 'department' as const,
            departmentId: d.id,
            label: d.name,
          }));
    return pool
      .filter((o) => !q || o.label.toLowerCase().includes(q))
      .filter((o) => !this.assignees.some((a) => sameAssignee(a, o)))
      .slice(0, 8);
  }

  onOwnerQueryChange(value: string): void {
    this.ownerQuery = value;
    this.ownerSuggestionsOpen = true;
  }

  addOwner(owner: ActionPlanAssignee): void {
    if (this.assignees.some((a) => sameAssignee(a, owner))) return;
    this.assignees = [...this.assignees, owner];
    this.ownerQuery = '';
    this.ownerSuggestionsOpen = false;
    this.syncResponsibility();
  }

  commitTypedOwner(): void {
    const first = this.ownerSuggestions[0];
    if (first) {
      this.addOwner(first);
      return;
    }
    const label = this.ownerQuery.trim();
    if (!label) return;
    this.addOwner({ type: this.responsibilityType, label });
  }

  removeOwner(owner: ActionPlanAssignee): void {
    this.assignees = this.assignees.filter((a) => !sameAssignee(a, owner));
    this.syncResponsibility();
  }

  ownerChipClass(owner: ActionPlanAssignee): string {
    return owner.type === 'user' ? 'ap-owner-chip is-user' : 'ap-owner-chip is-department';
  }

  private syncResponsibility(): void {
    this.draft = {
      ...this.draft,
      responsibility: this.assignees.map((a) => a.label).join(', '),
    };
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (!this.ownerSuggestionsOpen) return;
    const target = event.target as Node | null;
    const field = this.host.nativeElement.querySelector('.ap-owner-field');
    if (target && field?.contains(target)) return;
    this.ownerSuggestionsOpen = false;
  }

  onReportFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.uploadEvidence.emit(input.files);
    input.value = '';
  }

  emit(action: RunReviewSubmitEvent['action']): void {
    this.syncResponsibility();
    this.submitReview.emit({ action, draft: { ...this.draft } });
  }
}

function formatAttachmentSize(bytes?: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
