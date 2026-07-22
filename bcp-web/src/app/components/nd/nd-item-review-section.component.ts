import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ACTION_ITEM_REVIEW_OPTIONS,
  actionReviewStatusLabel,
  compareActionItemReviews,
  emptyActionItemReviewDraft,
  type ActionItemReviewDraft,
  type ActionItemReviewEntry,
  type ActionItemReviewStatus,
} from '../../../lib/nd/action-item-review';
import {
  RISK_STANDARD_SUMMARY,
  riskScoreFromRaw,
  riskScoreLabel,
  riskTierFromScore,
} from '../../../lib/nd/risk-priority-score';
import { formatDate } from '../../../lib/nd/utils';
import type { Department } from '../../../lib/nd/types';

export type ItemReviewSaveEvent = {
  reviewId?: string;
  actionIndex: number;
  status: ActionItemReviewStatus;
  comment: string;
  responsibility: string;
  dueDate: string;
  priority: string;
};

export type ItemReviewReorderEvent = {
  reviewId: string;
  actionIndex: number;
  direction: 'up' | 'down';
};

@Component({
  selector: 'app-nd-item-review-section',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-item-review-section.component.html',
  styleUrl: './nd-item-review-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdItemReviewSectionComponent {
  @Input({ required: true }) actionIndex = 0;
  @Input() title = '';
  @Input() subtitle = '';
  @Input() reviews: ActionItemReviewEntry[] = [];
  @Input() reviewMode = false;
  @Input() showPanel = false;
  @Input() disabledHint = '';
  @Input() departments: Department[] = [];
  @Input() savingActionIndex: number | null = null;
  @Input() savingReviewId: string | null = null;
  /** Use on light CAP cards where dark-theme text tokens are unreadable. */
  @Input() lightSurface = false;

  @Output() saveReview = new EventEmitter<ItemReviewSaveEvent>();
  @Output() deleteReview = new EventEmitter<string>();
  @Output() reorderReview = new EventEmitter<ItemReviewReorderEvent>();

  readonly statusOptions = ACTION_ITEM_REVIEW_OPTIONS;
  readonly riskStandardLabel = RISK_STANDARD_SUMMARY;
  readonly actionReviewStatusLabel = actionReviewStatusLabel;

  formOpen = false;
  editingReviewId: string | null = null;
  draft: ActionItemReviewDraft = emptyActionItemReviewDraft();
  draftDate = '';
  draftTime = '';
  expandedReviewIds = new Set<string>();

  get sortedReviews(): ActionItemReviewEntry[] {
    return [...this.reviews].sort((a, b) => compareActionItemReviews(a, b));
  }

  get formTitle(): string {
    return this.editingReviewId ? 'Edit review' : 'Add review';
  }

  get formHint(): string {
    return this.editingReviewId
      ? 'Update this review entry — changes are visible to everyone.'
      : 'Each save creates a new review entry visible to everyone.';
  }

  get priorityScoreLabel(): string {
    return riskScoreLabel(this.draft.priority);
  }

  get priorityTierClass(): string {
    return `tier-${riskTierFromScore(this.draft.priority)}`;
  }

  get isSaving(): boolean {
    return this.savingActionIndex === this.actionIndex;
  }

  reviewPriorityLabel(rev: ActionItemReviewEntry): string {
    return riskScoreLabel(riskScoreFromRaw(rev.priority));
  }

  reviewPriorityTierClass(rev: ActionItemReviewEntry): string {
    return `tier-${riskTierFromScore(riskScoreFromRaw(rev.priority))}`;
  }

  reviewStatusClass(status: ActionItemReviewStatus | ''): string {
    if (status === 'approve') return 'review-status-approve';
    if (status === 'need_modify') return 'review-status-modify';
    return '';
  }

  formatReviewDate(iso: string): string {
    return formatDate(iso);
  }

  formatDueDate(value: string | null | undefined): string {
    if (!value?.trim()) return '—';
    const d = new Date(value.trim());
    if (Number.isNaN(d.getTime())) return value;
    return d.toLocaleString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  commentPreview(comment: string | null | undefined): string {
    const t = comment?.trim() ?? '';
    if (!t) return '—';
    return t.length > 72 ? `${t.slice(0, 72)}…` : t;
  }

  isExpanded(reviewId: string): boolean {
    return this.expandedReviewIds.has(reviewId);
  }

  toggleExpanded(reviewId: string): void {
    const next = new Set(this.expandedReviewIds);
    if (next.has(reviewId)) next.delete(reviewId);
    else next.add(reviewId);
    this.expandedReviewIds = next;
  }

  canMoveUp(reviewId: string): boolean {
    const list = this.sortedReviews;
    return list.findIndex((r) => r.id === reviewId) > 0;
  }

  canMoveDown(reviewId: string): boolean {
    const list = this.sortedReviews;
    const index = list.findIndex((r) => r.id === reviewId);
    return index >= 0 && index < list.length - 1;
  }

  isReviewBusy(reviewId: string): boolean {
    return this.savingReviewId === reviewId || this.isSaving;
  }

  openForm(): void {
    this.editingReviewId = null;
    this.formOpen = true;
    this.draft = emptyActionItemReviewDraft();
    this.draftDate = '';
    this.draftTime = '';
  }

  openEditForm(rev: ActionItemReviewEntry, event: Event): void {
    event.stopPropagation();
    this.editingReviewId = rev.id;
    this.formOpen = true;
    this.draft = {
      status: rev.status,
      comment: rev.comment?.trim() ?? '',
      responsibility: rev.responsibility?.trim() ?? '',
      dueDate: rev.dueDate?.trim() ?? '',
      priority: riskScoreFromRaw(rev.priority),
    };
    const parsed = this.parseDueDateParts(rev.dueDate);
    this.draftDate = parsed.date;
    this.draftTime = parsed.time;
  }

  closeForm(): void {
    this.formOpen = false;
    this.editingReviewId = null;
    this.draft = emptyActionItemReviewDraft();
    this.draftDate = '';
    this.draftTime = '';
  }

  onDelete(reviewId: string, event: Event): void {
    event.stopPropagation();
    this.deleteReview.emit(reviewId);
  }

  onReorder(reviewId: string, direction: 'up' | 'down', event: Event): void {
    event.stopPropagation();
    this.reorderReview.emit({ reviewId, actionIndex: this.actionIndex, direction });
  }

  setPriority(value: number): void {
    this.draft = { ...this.draft, priority: Math.min(100, Math.max(0, value)) };
  }

  canSave(): boolean {
    return !!this.draft.status && !this.isSaving;
  }

  private combinedDueDate(): string {
    if (!this.draftDate.trim()) return '';
    const time = this.draftTime.trim() || '12:00';
    return `${this.draftDate.trim()}T${time}`;
  }

  private parseDueDateParts(value: string | null | undefined): { date: string; time: string } {
    if (!value?.trim()) return { date: '', time: '' };
    const d = new Date(value.trim());
    if (Number.isNaN(d.getTime())) return { date: '', time: '' };
    const date = d.toISOString().slice(0, 10);
    const time = d.toTimeString().slice(0, 5);
    return { date, time };
  }

  onSave(): void {
    if (!this.draft.status) return;
    this.saveReview.emit({
      reviewId: this.editingReviewId ?? undefined,
      actionIndex: this.actionIndex,
      status: this.draft.status,
      comment: this.draft.comment,
      responsibility: this.draft.responsibility,
      dueDate: this.combinedDueDate(),
      priority: String(this.draft.priority),
    });
    this.closeForm();
  }
}
