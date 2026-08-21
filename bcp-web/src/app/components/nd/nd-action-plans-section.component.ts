import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NdApiService, type NdReviewAssignee } from '../../services/nd/nd-api.service';
import {
  ACTION_PLAN_STATUS_OPTIONS,
  actionPlanPriorityClass,
  actionPlanPriorityFromScore,
  actionPlanPriorityLabel,
  actionPlanPriorityScore,
  actionPlanScoreFromPriority,
  actionPlanScoreLabel,
  actionPlanStatusLabel,
  assigneesForPlan,
  draftFromActionPlan,
  emptyActionPlanDraft,
  isActionPlanOverdue,
  sameAssignee,
  type ActionPlanAssignee,
  type ActionPlanDraft,
  type ActionPlanEntry,
  type ActionPlanPriority,
  type ActionPlanResponsibilityOptions,
  type ActionPlanReviewEntry,
  type ActionPlanResponsibilityType,
  type ActionPlanStatus,
  type ActionPlanTargetDateChange,
} from '../../../lib/nd/action-plan';
import { defaultTargetDateForGapRisk } from '../../../lib/nd/doc-analysis-ready';
import { formatDate } from '../../../lib/nd/utils';

/**
 * Actions belonging to one CAP gap. A gap can hold several actions; each action carries
 * its own plan text, status, target date, responsibility and 0–100 priority score.
 * Every role may add and edit actions; checker/reviewer/super_admin may also leave
 * comment-only reviews, which the parent surfaces in the right-hand panel.
 */
@Component({
  selector: 'app-nd-action-plans-section',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './nd-action-plans-section.component.html',
  styleUrl: './nd-action-plans-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdActionPlansSectionComponent implements OnChanges {
  private readonly api = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);
  private readonly host = inject(ElementRef<HTMLElement>);

  @Input({ required: true }) runId!: string;
  @Input({ required: true }) analysisPointId!: string;
  /** 1-based CAP gap these actions belong to. */
  @Input() gapIndex = 0;
  /** Actions already loaded by the parent, pre-filtered to this gap. */
  @Input() plans: ActionPlanEntry[] = [];
  @Input() canEdit = true;
  @Input() canReview = false;
  @Input() lightSurface = false;
  @Input() disabledHint = '';
  /** Priority inherited from the parent gap — not independently editable on the action. */
  @Input() inheritedPriority: ActionPlanPriority = 'medium';
  /** Action id from an inbox deep link — expanded and scrolled into view once it loads. */
  @Input() focusPlanId: string | null = null;

  @Output() plansChanged = new EventEmitter<void>();
  @Output() viewReviews = new EventEmitter<ActionPlanEntry>();

  readonly statusOptions = ACTION_PLAN_STATUS_OPTIONS;
  readonly priorityLabel = actionPlanPriorityLabel;
  readonly priorityClass = actionPlanPriorityClass;
  readonly scoreLabel = actionPlanScoreLabel;
  readonly planScore = actionPlanPriorityScore;
  readonly statusLabel = actionPlanStatusLabel;
  readonly isOverdue = isActionPlanOverdue;
  readonly formatDate = formatDate;

  expandedIds = new Set<string>();
  editingId: string | null = null;
  adding = false;
  draft: ActionPlanDraft = emptyActionPlanDraft();
  saving = false;
  error = '';

  options: ActionPlanResponsibilityOptions = { departments: [], users: [] };
  private optionsLoaded = false;

  historyPlanId: string | null = null;
  historyRows: ActionPlanTargetDateChange[] = [];
  historyLoading = false;

  reviewDraftPlanId: string | null = null;
  reviewDraftText = '';
  /** "department:{id}" or "user:{id}"; blank leaves the review as an unrouted note. */
  reviewAssigneeKey = '';
  editingReviewId: string | null = null;
  savingReview = false;

  private focusPlanApplied: string | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['analysisPointId'] || changes['gapIndex']) {
      this.resetEditor();
      this.historyPlanId = null;
    }
    if (!this.optionsLoaded && (this.canEdit || this.canReview)) void this.loadOptions();
    this.applyFocusPlan();
  }

  /** Expand and scroll to the action a deep link points at, once that action has loaded. */
  private applyFocusPlan(): void {
    const target = this.focusPlanId;
    if (!target || this.focusPlanApplied === target) return;
    if (!this.plans.some((p) => p.id === target)) return;

    this.focusPlanApplied = target;
    this.expandedIds = new Set(this.expandedIds).add(target);
    setTimeout(() => {
      const root = this.host.nativeElement as HTMLElement;
      const el = root.querySelector(`[data-action-plan-id="${target}"]`) as HTMLElement | null;
      el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
      el?.classList.add('ap-card-focused');
    }, 300);
  }

  private async loadOptions(): Promise<void> {
    this.optionsLoaded = true;
    const res = await this.api.getActionPlanResponsibilityOptions();
    if (res.success && res.data) this.options = res.data;
    this.cdr.markForCheck();
  }

  // ------------------------------------------------------------- display

  get sortedPlans(): ActionPlanEntry[] {
    return [...this.plans].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
  }

  isExpanded(id: string): boolean {
    return this.expandedIds.has(id);
  }

  toggleExpanded(id: string): void {
    const next = new Set(this.expandedIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.expandedIds = next;
  }

  targetDateLabel(plan: ActionPlanEntry): string {
    if (!plan.targetDate) return 'No target date';
    return this.formatDate(plan.targetDate);
  }

  responsibilityLabel(plan: ActionPlanEntry): string {
    const owners = assigneesForPlan(plan).filter((a) => a.label.trim());
    if (owners.length === 0) return 'Unassigned';
    if (owners.length === 1) {
      return `${owners[0].label} (${owners[0].type === 'user' ? 'user' : 'department'})`;
    }
    return `${owners[0].label} +${owners.length - 1} more`;
  }

  ownersOf(plan: ActionPlanEntry): ActionPlanAssignee[] {
    return assigneesForPlan(plan).filter((a) => a.label.trim());
  }

  // -------------------------------------------------------------- editor

  get inheritedPriorityLabel(): string {
    return actionPlanPriorityLabel(this.inheritedPriority);
  }

  startAdd(): void {
    this.adding = true;
    this.editingId = null;
    this.draft = {
      ...emptyActionPlanDraft(),
      priorityScore: actionPlanScoreFromPriority(this.inheritedPriority),
      targetDate: defaultTargetDateForGapRisk(this.inheritedPriority),
    };
    this.error = '';
  }

  startEdit(plan: ActionPlanEntry): void {
    this.adding = false;
    this.editingId = plan.id;
    this.draft = draftFromActionPlan(plan);
    this.error = '';
    this.expandedIds = new Set(this.expandedIds).add(plan.id);
  }

  cancelEdit(): void {
    this.resetEditor();
  }

  private resetEditor(): void {
    this.adding = false;
    this.editingId = null;
    this.draft = emptyActionPlanDraft();
    this.error = '';
  }

  setDraftStatus(value: string): void {
    this.draft = { ...this.draft, status: value as ActionPlanStatus };
  }

  planScoreClass(plan: ActionPlanEntry): string {
    return `ap-priority-${actionPlanPriorityFromScore(this.planScore(plan))}`;
  }

  reviewCountLabel(plan: ActionPlanEntry): string {
    const n = plan.reviewCount ?? plan.reviews?.length ?? 0;
    return `${n} review${n === 1 ? '' : 's'}`;
  }

  // ------------------------------------------------- responsibility picker

  /** Which tab the picker shows. Owners already added stay put when tabs switch. */
  setResponsibilityType(value: string): void {
    this.draft = { ...this.draft, responsibilityType: value as ActionPlanResponsibilityType };
    this.ownerQuery = '';
    this.ownerSuggestionsOpen = false;
  }

  ownerQuery = '';
  ownerSuggestionsOpen = false;

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (!this.ownerSuggestionsOpen) return;
    const target = event.target as Node | null;
    const field = (this.host.nativeElement as HTMLElement).querySelector('.ap-owner-field');
    if (target && field?.contains(target)) return;
    this.ownerSuggestionsOpen = false;
    this.cdr.markForCheck();
  }

  get draftAssignees(): ActionPlanAssignee[] {
    return this.draft.assignees;
  }

  /**
   * Suggestions for what the user has typed so far, limited to the active tab and
   * excluding owners already added.
   */
  get ownerSuggestions(): ActionPlanAssignee[] {
    const q = this.ownerQuery.trim().toLowerCase();
    const pool: ActionPlanAssignee[] =
      this.draft.responsibilityType === 'user'
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
      .filter((o) => !this.draft.assignees.some((a) => sameAssignee(a, o)))
      .slice(0, 8);
  }

  onOwnerQueryChange(value: string): void {
    this.ownerQuery = value;
    this.ownerSuggestionsOpen = true;
  }

  addOwner(owner: ActionPlanAssignee): void {
    if (this.draft.assignees.some((a) => sameAssignee(a, owner))) return;
    this.draft = { ...this.draft, assignees: [...this.draft.assignees, owner] };
    this.ownerQuery = '';
    this.ownerSuggestionsOpen = false;
  }

  /** Enter picks the first suggestion, or keeps the typed text as a free-text owner. */
  commitTypedOwner(): void {
    const first = this.ownerSuggestions[0];
    if (first) {
      this.addOwner(first);
      return;
    }
    const label = this.ownerQuery.trim();
    if (!label) return;
    this.addOwner({ type: this.draft.responsibilityType, label });
  }

  removeOwner(owner: ActionPlanAssignee): void {
    this.draft = {
      ...this.draft,
      assignees: this.draft.assignees.filter((a) => !sameAssignee(a, owner)),
    };
  }

  ownerChipClass(owner: ActionPlanAssignee): string {
    return owner.type === 'user' ? 'ap-owner-chip is-user' : 'ap-owner-chip is-department';
  }

  /** Re-target reason only matters when an existing plan already had a date. */
  get showRetargetReason(): boolean {
    if (this.adding || !this.editingId) return false;
    const plan = this.plans.find((p) => p.id === this.editingId);
    if (!plan?.targetDate) return false;
    return draftFromActionPlan(plan).targetDate !== this.draft.targetDate;
  }

  async submit(): Promise<void> {
    if (!this.draft.actionPlan.trim()) {
      this.error = 'Describe the action plan before saving.';
      return;
    }

    this.saving = true;
    this.error = '';
    const body = {
      actionPlan: this.draft.actionPlan.trim(),
      status: this.draft.status,
      priorityScore: actionPlanScoreFromPriority(this.inheritedPriority),
      priority: this.inheritedPriority,
      targetDate: this.draft.targetDate || null,
      responsibilityType: this.draft.responsibilityType,
      assignees: this.draft.assignees,
      comment: this.draft.comment.trim() || null,
    };

    const res = this.editingId
      ? await this.api.updateActionPlanEntry(this.runId, this.editingId, {
          ...body,
          targetDateChangeReason: this.draft.targetDateChangeReason.trim() || null,
        })
      : await this.api.createActionPlan(this.runId, {
          ...body,
          analysisPointId: this.analysisPointId,
          gapIndex: this.gapIndex,
        });

    this.saving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not save the action plan.';
      this.cdr.markForCheck();
      return;
    }

    this.resetEditor();
    this.plansChanged.emit();
    this.cdr.markForCheck();
  }

  async remove(plan: ActionPlanEntry): Promise<void> {
    if (!confirm('Delete this action plan and its reviews?')) return;
    this.saving = true;
    const res = await this.api.deleteActionPlan(this.runId, plan.id);
    this.saving = false;
    if (!res.success) {
      this.error = res.message ?? 'Could not delete the action plan.';
      this.cdr.markForCheck();
      return;
    }
    this.plansChanged.emit();
    this.cdr.markForCheck();
  }

  async move(plan: ActionPlanEntry, direction: 'up' | 'down'): Promise<void> {
    await this.api.reorderActionPlan(this.runId, plan.id, direction);
    this.plansChanged.emit();
  }

  /** Action whose status is being written right now, so only its own button spins. */
  togglingPlanId: string | null = null;
  /** Short-lived confirmation shown on the card after a resolve or reopen lands. */
  statusNoticePlanId: string | null = null;
  statusNoticeText = '';
  private statusNoticeTimer: ReturnType<typeof setTimeout> | null = null;

  isToggling(planId: string): boolean {
    return this.togglingPlanId === planId;
  }

  /** One-click resolve/reopen without opening the full editor. */
  async toggleStatus(plan: ActionPlanEntry): Promise<void> {
    if (this.togglingPlanId) return;

    const next: ActionPlanStatus = plan.status === 'resolved' ? 'pending' : 'resolved';
    this.togglingPlanId = plan.id;
    this.error = '';
    this.clearStatusNotice();
    this.cdr.markForCheck();

    const res = await this.api.updateActionPlanEntry(this.runId, plan.id, { status: next });
    this.togglingPlanId = null;

    if (!res.success) {
      this.error = res.message ?? 'Could not update the action.';
      this.cdr.markForCheck();
      return;
    }

    this.showStatusNotice(
      plan.id,
      next === 'resolved' ? 'Action resolved.' : 'Action reopened.',
    );
    this.plansChanged.emit();
    this.cdr.markForCheck();
  }

  private showStatusNotice(planId: string, text: string): void {
    this.statusNoticePlanId = planId;
    this.statusNoticeText = text;
    this.statusNoticeTimer = setTimeout(() => {
      this.clearStatusNotice();
      this.cdr.markForCheck();
    }, 4000);
  }

  private clearStatusNotice(): void {
    if (this.statusNoticeTimer) clearTimeout(this.statusNoticeTimer);
    this.statusNoticeTimer = null;
    this.statusNoticePlanId = null;
    this.statusNoticeText = '';
  }

  // ------------------------------------------------------ status history

  statusHistoryPlanId: string | null = null;
  statusHistoryRows: {
    id: string;
    previousStatus: string | null;
    newStatus: string;
    changedByName: string | null;
    createdAt: string;
  }[] = [];
  statusHistoryLoading = false;

  /** Who moved this action between pending and resolved. */
  async toggleStatusHistory(plan: ActionPlanEntry): Promise<void> {
    if (this.statusHistoryPlanId === plan.id) {
      this.statusHistoryPlanId = null;
      this.statusHistoryRows = [];
      return;
    }
    this.statusHistoryPlanId = plan.id;
    this.statusHistoryRows = [];
    this.statusHistoryLoading = true;
    this.cdr.markForCheck();

    const res = await this.api.getActionPlanStatusHistory(this.runId, plan.id);
    this.statusHistoryLoading = false;
    if (res.success && res.data) this.statusHistoryRows = res.data;
    this.cdr.markForCheck();
  }

  statusHistoryLabel(row: { previousStatus: string | null; newStatus: string }): string {
    const from = actionPlanStatusLabel(row.previousStatus ?? 'pending');
    return `${from} → ${actionPlanStatusLabel(row.newStatus)}`;
  }

  /** Falls back to the action's own resolve stamp when no history row exists yet. */
  resolvedByLabel(plan: ActionPlanEntry): string {
    if (plan.status !== 'resolved') return '';
    const who = plan.resolvedByName ?? plan.updatedByName;
    const when = plan.resolvedAt ? this.formatDate(plan.resolvedAt) : '';
    if (!who && !when) return '';
    return who ? `Resolved by ${who}${when ? ` · ${when}` : ''}` : `Resolved ${when}`;
  }

  // ------------------------------------------------------ target history

  async toggleHistory(plan: ActionPlanEntry): Promise<void> {
    if (this.historyPlanId === plan.id) {
      this.historyPlanId = null;
      this.historyRows = [];
      return;
    }
    this.historyPlanId = plan.id;
    this.historyRows = [];
    this.historyLoading = true;
    this.cdr.markForCheck();

    const res = await this.api.getActionPlanDateHistory(this.runId, plan.id);
    this.historyLoading = false;
    if (res.success && res.data) this.historyRows = res.data;
    this.cdr.markForCheck();
  }

  historyEntryLabel(row: ActionPlanTargetDateChange): string {
    const from = row.previousTargetDate ? this.formatDate(row.previousTargetDate) : 'no date';
    const to = row.newTargetDate ? this.formatDate(row.newTargetDate) : 'no date';
    return `${from} → ${to}`;
  }

  // -------------------------------------------------------------- reviews

  startReview(plan: ActionPlanEntry): void {
    this.reviewDraftPlanId = plan.id;
    this.editingReviewId = null;
    this.reviewDraftText = '';
    this.reviewAssigneeKey = '';
    this.reviewAssigneeType = 'department';
    this.reviewAssigneeQuery = '';
  }

  startEditReview(plan: ActionPlanEntry, review: ActionPlanReviewEntry): void {
    this.reviewDraftPlanId = plan.id;
    this.editingReviewId = review.id;
    this.reviewDraftText = review.comment;
    this.reviewAssigneeKey = review.assigneeType
      ? `${review.assigneeType}:${review.assigneeDepartmentId ?? review.assigneeUserId ?? ''}`
      : '';
    this.reviewAssigneeType = review.assigneeType === 'user' ? 'user' : 'department';
    this.reviewAssigneeQuery = '';
  }

  cancelReview(): void {
    this.reviewDraftPlanId = null;
    this.editingReviewId = null;
    this.reviewDraftText = '';
    this.reviewAssigneeKey = '';
    this.reviewAssigneeQuery = '';
    this.reviewAssigneeOpen = false;
  }

  /**
   * Review routing uses the same department/individual picker as an action's
   * responsibility, so both read the same way.
   */
  reviewAssigneeType: ActionPlanResponsibilityType = 'department';
  reviewAssigneeQuery = '';
  reviewAssigneeOpen = false;

  get reviewAssigneeChoices(): { key: string; label: string; group: string }[] {
    return [
      ...this.options.departments.map((d) => ({
        key: `department:${d.id}`,
        label: d.name,
        group: 'Departments',
      })),
      ...this.options.users.map((u) => ({
        key: `user:${u.id}`,
        label: u.fullName,
        group: 'People',
      })),
    ];
  }

  get reviewAssigneeLabelSelected(): string {
    return this.reviewAssigneeChoices.find((c) => c.key === this.reviewAssigneeKey)?.label ?? '';
  }

  get reviewAssigneeChipClass(): string {
    return `ap-owner-chip ap-owner-chip-${this.reviewAssigneeType}`;
  }

  get reviewAssigneeSuggestions(): { key: string; label: string; group: string }[] {
    const wanted = this.reviewAssigneeType === 'user' ? 'People' : 'Departments';
    const q = this.reviewAssigneeQuery.trim().toLowerCase();
    return this.reviewAssigneeChoices
      .filter((c) => c.group === wanted)
      .filter((c) => !q || c.label.toLowerCase().includes(q))
      .slice(0, 8);
  }

  setReviewAssigneeType(type: ActionPlanResponsibilityType): void {
    this.reviewAssigneeType = type;
    this.reviewAssigneeQuery = '';
    this.reviewAssigneeOpen = true;
  }

  onReviewAssigneeQueryChange(value: string): void {
    this.reviewAssigneeQuery = value;
    this.reviewAssigneeOpen = true;
  }

  pickReviewAssignee(choice: { key: string; label: string }): void {
    this.reviewAssigneeKey = choice.key;
    this.reviewAssigneeQuery = '';
    this.reviewAssigneeOpen = false;
  }

  clearReviewAssignee(): void {
    this.reviewAssigneeKey = '';
    this.reviewAssigneeQuery = '';
  }

  private reviewAssigneePayload(): NdReviewAssignee | undefined {
    if (!this.reviewAssigneeKey) return undefined;
    const [type, id] = this.reviewAssigneeKey.split(':');
    const label = this.reviewAssigneeChoices.find((c) => c.key === this.reviewAssigneeKey)?.label;
    if (!label || (type !== 'department' && type !== 'user')) return undefined;
    return {
      assigneeType: type,
      assigneeDepartmentId: type === 'department' ? id : null,
      assigneeUserId: type === 'user' ? id : null,
      assigneeLabel: label,
    };
  }

  reviewAssigneeLabel(review: ActionPlanReviewEntry): string {
    if (!review.assigneeLabel) return '';
    return `${review.assigneeLabel} (${review.assigneeType === 'user' ? 'person' : 'department'})`;
  }

  async submitReview(plan: ActionPlanEntry): Promise<void> {
    const comment = this.reviewDraftText.trim();
    if (!comment) return;

    const assignee = this.reviewAssigneePayload();
    this.savingReview = true;
    const res = this.editingReviewId
      ? await this.api.updateActionPlanReview(this.runId, plan.id, this.editingReviewId, comment, assignee)
      : await this.api.addActionPlanReview(this.runId, plan.id, comment, assignee);
    this.savingReview = false;

    if (!res.success) {
      this.error = res.message ?? 'Could not save the review.';
      this.cdr.markForCheck();
      return;
    }

    this.cancelReview();
    this.plansChanged.emit();
    this.cdr.markForCheck();
  }

  async removeReview(plan: ActionPlanEntry, reviewId: string): Promise<void> {
    if (!confirm('Delete this review?')) return;
    await this.api.deleteActionPlanReview(this.runId, plan.id, reviewId);
    this.plansChanged.emit();
  }

  onViewReviews(plan: ActionPlanEntry, event: Event): void {
    event.stopPropagation();
    this.viewReviews.emit(plan);
  }
}
