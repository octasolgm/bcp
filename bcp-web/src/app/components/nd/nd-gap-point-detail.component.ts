import { Component, EventEmitter, Input, OnChanges, Output, inject } from '@angular/core';
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
import {
  GAP_PRIORITY_OPTIONS,
  gapPriorityClass,
  gapPriorityLabel,
  normalizeGapPriority,
  type GapPriority,
} from '../../../lib/nd/gap-priority';
import { meaningfulCapGaps, isMeaningfulCapGap } from '../../../lib/nd/cap-gap-count';
import { agreementBadgeClass, type AgreementStatus, type DualVerifyAgreement } from '../../../lib/landing-ai/dual-verify-merge';
import { ReferenceComplianceCardComponent } from '../reference-compliance-card/reference-compliance-card.component';
import type { ActionPlanHistoryEntry, AnalysisPoint, Department, PointGapAttachment, PointSnapshot } from '../../../lib/nd/types';
import { NdApiService } from '../../services/nd/nd-api.service';
import {
  complianceSeverityLabel,
  resolveAnalysisPointSeverity,
  resolveDisplayConfidence,
  type ComplianceSeverity,
} from '../../../lib/nd/point-compliance-status';
import {
  ACTION_ITEM_REVIEW_OPTIONS,
  actionReviewStatusLabel,
  emptyActionItemReviewDraft,
  reviewsForAction,
  type ActionItemReviewDraft,
  type ActionItemReviewEntry,
  type ActionItemReviewStatus,
} from '../../../lib/nd/action-item-review';
import { formatDate } from '../../../lib/nd/utils';
import {
  formatPointPageRef,
  resolveRegulationPdfPage,
} from '../../../lib/nd/regulation-pdf-page';
import {
  buildPolicyRefProofs,
  docLabelForId,
  formatPolicyRefLabel,
  resolvePolicyDocId,
  type PolicyDocCatalogEntry,
  type PolicyRefProof,
} from '../../../lib/nd/policy-doc-resolve';

@Component({
  selector: 'app-nd-gap-point-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, ReferenceComplianceCardComponent],
  templateUrl: './nd-gap-point-detail.component.html',
  styleUrl: './nd-gap-point-detail.component.scss',
})
export class NdGapPointDetailComponent implements OnChanges {
  private readonly ndApi = inject(NdApiService);
  private lastPointId: string | null = null;
  private collapsedActionsInit = false;
  private departmentsLoaded = false;

  collapsedActionIndexes = new Set<number>();
  openReviewFormIndexes = new Set<number>();
  openDueDateCalendarIndexes = new Set<number>();
  departments: Department[] = [];
  @Input({ required: true }) point!: AnalysisPoint;
  @Input() snapshot: PointSnapshot | null = null;
  @Input() canEdit = false;
  @Input() editing = false;
  @Input() saving = false;
  @Input() history: ActionPlanHistoryEntry[] = [];
  @Input() showHistoryPanel = false;
  @Input() policyDocId: string | null = null;
  @Input() policyDocCatalog: PolicyDocCatalogEntry[] = [];
  @Input() regulationDocId: string | null = null;
  @Input() hideReportHeader = false;
  @Input() phaseOutputDefaultOpen = false;
  /** Checker/reviewer: per-action status + comment controls. */
  @Input() reviewMode = false;
  /** Show review history / add-review area for checker, reviewer, super_admin. */
  @Input() showReviewPanel = false;
  @Input() reviewDisabledHint = '';
  /** In-progress drafts for adding a new review (review mode). */
  @Input() savingActionReviewIndex: number | null = null;
  /** All saved reviews for this point (newest first per action in template). */
  @Input() savedActionItemReviews: ActionItemReviewEntry[] = [];
  @Input() runId: string | null = null;
  @Input() gapAttachments: PointGapAttachment[] = [];
  @Input() canUploadEvidence = false;
  @Input() evidenceUploading = false;
  @Input() evidenceRerunning = false;
  @Input() evidenceUploadingActionIndex: number | null = null;
  @Input() evidenceRerunningActionIndex: number | null = null;

  @Output() startEdit = new EventEmitter<void>();
  @Output() cancelEdit = new EventEmitter<void>();
  @Output() save = new EventEmitter<string>();
  @Output() openHistory = new EventEmitter<void>();
  @Output() closeHistory = new EventEmitter<void>();
  @Output() restoreVersion = new EventEmitter<ActionPlanHistoryEntry>();
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();
  @Output() saveActionItemReview = new EventEmitter<{
    actionIndex: number;
    status: ActionItemReviewStatus;
    comment: string;
    responsibility: string;
    dueDate: string;
    priority: GapPriority | '';
  }>();
  @Output() uploadEvidence = new EventEmitter<FileList>();
  @Output() uploadGapEvidence = new EventEmitter<{ actionIndex: number; files: FileList }>();
  @Output() deleteEvidence = new EventEmitter<string>();
  @Output() deleteGapEvidence = new EventEmitter<{ actionIndex: number; attachmentId: string }>();
  @Output() rerunWithEvidence = new EventEmitter<'full' | 'dual'>();
  @Output() rerunGapEvidence = new EventEmitter<{ actionIndex: number; mode: 'full' | 'dual' }>();

  readonly actionReviewOptions = ACTION_ITEM_REVIEW_OPTIONS;
  readonly actionReviewStatusLabel = actionReviewStatusLabel;
  readonly gapPriorityOptions = GAP_PRIORITY_OPTIONS;
  readonly gapPriorityLabel = gapPriorityLabel;
  readonly gapPriorityClass = gapPriorityClass;

  pointHeading = '';
  regulatoryText = '';
  policyExtract = '';
  policyPage: string | null = null;
  policySection: string | null = null;
  policyRefLabel = '';
  policyRefs: PolicyRefProof[] = [];
  regulationPage: number | null = null;
  regulationPageLabel: string | null = null;
  landingMessage = '';
  llmMessage = '';
  agreement?: DualVerifyAgreement;
  primaryBlock!: ReferenceComplianceBlock;
  showFulfilled = false;
  fulfilledLines: string[] = [];
  responsibility = '';
  capGaps: CapGap[] = [];
  originalPlan = '';
  currentPlan = '';
  showCapSection = false;
  /** When set, only this action-plan item is in inline edit mode. */
  editingGapIndex: number | null = null;
  /** True while adding a new action at the bottom of the list. */
  addingNewAction = false;
  /** Draft fields for the action being edited or added. */
  draftGap: CapGap = { index: 1, missing: '', fix: '', priority: '' };
  /** Filter history panel to a single action item. */
  historyGapIndex: number | null = null;
  resolvedSeverity: ComplianceSeverity = 'partial_compliant';
  /** Local drafts for new reviews before Save. */
  newReviewDrafts: Record<number, ActionItemReviewDraft> = {};

  ngOnChanges(): void {
    if (this.point?.id !== this.lastPointId) {
      this.lastPointId = this.point?.id ?? null;
      this.collapsedActionsInit = false;
      this.collapsedActionIndexes = new Set();
      this.openReviewFormIndexes = new Set();
      this.openDueDateCalendarIndexes = new Set();
      this.newReviewDrafts = {};
    }

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

    this.regulationPage = resolveRegulationPdfPage(snap?.pageReference, null);
    this.regulationPageLabel = formatPointPageRef(snap?.pageReference, this.regulationPage);

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
    const catalog = this.policyDocCatalog.length
      ? this.policyDocCatalog
      : this.policyDocId
        ? [{ id: this.policyDocId, title: null, originalFileName: null }]
        : [];

    this.policyRefs = buildPolicyRefProofs(this.landingMessage, this.llmMessage, catalog);
    if (this.policyRefs.length) {
      const first = this.policyRefs[0];
      this.policyPage = first.page;
      this.policySection = first.section;
    }

    for (const msg of [this.landingMessage, this.llmMessage]) {
      if (!msg?.trim()) continue;
      const structured = parseReferenceComplianceBlock(msg);
      const source = structured.outputResponse?.trim() ?? '';
      if (!source) continue;
      const cite = parseReferenceCitation(source);
      if (!this.policyExtract) {
        this.policyExtract = cite.quote?.trim() || source;
        if (!this.policyPage) {
          this.policyPage = cite.page;
          this.policySection = cite.section;
        }
      }
    }
    if (!this.policyExtract) {
      this.policyExtract = 'No corresponding policy extract found.';
    }

    const resolvedDocId =
      resolvePolicyDocId(this.primaryBlock.referencePdf, catalog) ??
      this.policyRefs.find((r) => r.docId)?.docId ??
      this.policyDocId;

    const refParts: string[] = [];
    if (catalog.length > 1 && resolvedDocId) {
      refParts.push(docLabelForId(resolvedDocId, catalog));
    }
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
    this.capGaps = capSource ? meaningfulCapGaps(capSource) : [];

    this.resolvedSeverity = resolveAnalysisPointSeverity(this.point);
    this.showCapSection =
      this.capGaps.length > 0 ||
      (this.resolvedSeverity !== 'compliant' &&
        Boolean(capSource) &&
        (this.resolvedSeverity === 'partial_compliant' || this.resolvedSeverity === 'non_compliant'));

    if (!this.editing) {
      this.resetEditState();
    }

    this.initActionCollapseState();
    if (this.showReviewPanel && !this.departmentsLoaded) void this.loadDepartments();
  }

  private initActionCollapseState(): void {
    if (!this.capGaps.length || this.collapsedActionsInit) return;
    this.collapsedActionsInit = true;
    // All actions start expanded; user can collapse via the action header.
  }

  private async loadDepartments(): Promise<void> {
    const res = await this.ndApi.getDepartments();
    if (res.success && res.data) {
      this.departments = (res.data as Department[]).filter((d) => d.isActive !== false);
    }
    this.departmentsLoaded = true;
  }

  isActionCollapsed(index: number): boolean {
    return this.collapsedActionIndexes.has(index);
  }

  toggleActionCollapsed(index: number): void {
    const next = new Set(this.collapsedActionIndexes);
    if (next.has(index)) next.delete(index);
    else next.add(index);
    this.collapsedActionIndexes = next;
  }

  isReviewFormOpen(index: number): boolean {
    return this.openReviewFormIndexes.has(index);
  }

  openReviewForm(index: number): void {
    this.openReviewFormIndexes = new Set(this.openReviewFormIndexes).add(index);
    if (!this.newReviewDrafts[index]) {
      this.newReviewDrafts = { ...this.newReviewDrafts, [index]: emptyActionItemReviewDraft() };
    }
  }

  closeReviewForm(index: number): void {
    this.clearReviewDraft(index);
  }

  isDueDateCalendarOpen(index: number): boolean {
    return this.openDueDateCalendarIndexes.has(index);
  }

  toggleDueDateCalendar(index: number): void {
    const next = new Set(this.openDueDateCalendarIndexes);
    if (next.has(index)) next.delete(index);
    else next.add(index);
    this.openDueDateCalendarIndexes = next;
  }

  dueDateLabel(index: number): string {
    const value = this.actionReviewDraft(index).dueDate;
    if (!value) return 'Select due date';
    try {
      return new Date(`${value}T12:00:00`).toLocaleDateString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
      });
    } catch {
      return value;
    }
  }

  reviewCountForGap(index: number): number {
    return this.actionReviewsForGap(index).length;
  }

  get displayComplianceStatus(): string {
    return complianceSeverityLabel(this.resolvedSeverity);
  }

  get displayConfidence(): string {
    return resolveDisplayConfidence(this.point);
  }

  get compliancePillClass(): string {
    const s = this.resolvedSeverity;
    if (s === 'non_compliant') return 'pill-red';
    if (s === 'partial_compliant') return 'pill-yellow';
    if (s === 'compliant') return 'pill-green';
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

  startAddAction(): void {
    this.addingNewAction = true;
    this.editingGapIndex = null;
    const next = this.capGaps.length ? Math.max(...this.capGaps.map((g) => g.index)) + 1 : 1;
    this.draftGap = { index: next, missing: '', fix: '', priority: '' };
    this.startEdit.emit();
  }

  onOpenHistory(gapIndex?: number): void {
    this.historyGapIndex = gapIndex ?? null;
    this.openHistory.emit();
  }

  onCloseHistory(): void {
    this.historyGapIndex = null;
    this.closeHistory.emit();
  }

  startEditSingleGap(index: number): void {
    this.addingNewAction = false;
    this.editingGapIndex = index;
    const gap = this.capGaps.find((g) => g.index === index);
    this.draftGap = gap ? { ...gap, priority: normalizeGapPriority(gap.priority) } : { index, missing: '', fix: '', priority: '' };
    this.startEdit.emit();
  }

  filteredHistory(): ActionPlanHistoryEntry[] {
    if (this.historyGapIndex == null) return this.history;
    return this.history.filter((h) => {
      const gaps = this.historyGaps(h.actionPlanContent);
      return gaps.some((g) => g.index === this.historyGapIndex);
    });
  }

  historyGapAt(entry: ActionPlanHistoryEntry, index: number): CapGap | null {
    return this.historyGaps(entry.actionPlanContent).find((g) => g.index === index) ?? null;
  }

  isEditingGap(index: number): boolean {
    return this.editing && this.editingGapIndex === index;
  }

  onCancelEdit(): void {
    this.resetEditState();
    this.cancelEdit.emit();
  }

  onSave(): void {
    if (!this.draftGap.missing.trim() && !this.draftGap.fix.trim()) return;

    let gaps: CapGap[];
    if (this.addingNewAction) {
      gaps = [...this.capGaps, { ...this.draftGap }];
    } else if (this.editingGapIndex != null) {
      gaps = this.capGaps.map((g) =>
        g.index === this.editingGapIndex ? { ...this.draftGap, index: g.index } : g,
      );
    } else {
      return;
    }

    gaps = gaps
      .filter((g) => g.missing.trim() || g.fix.trim())
      .map((g, i) => ({ ...g, index: i + 1 }));

    const content = serializeCapGaps(gaps);
    if (!content.trim()) return;

    this.resetEditState();
    this.save.emit(content);
  }

  private resetEditState(): void {
    this.editingGapIndex = null;
    this.addingNewAction = false;
    this.draftGap = { index: 1, missing: '', fix: '', priority: '' };
  }

  gapPriorityFor(gap: CapGap): GapPriority | '' {
    return normalizeGapPriority(gap.priority);
  }

  onPriorityChange(gapIndex: number, priority: GapPriority): void {
    if (!this.canEdit || this.saving) return;
    const gaps = this.capGaps.map((g) =>
      g.index === gapIndex ? { ...g, priority } : g,
    );
    const content = serializeCapGaps(gaps);
    if (content.trim()) this.save.emit(content);
  }

  setDraftPriority(priority: GapPriority | ''): void {
    this.draftGap = { ...this.draftGap, priority: priority || '' };
  }

  onViewRegPdf(): void {
    if (!this.regulationDocId) return;
    const page = this.regulationPage != null ? String(this.regulationPage) : null;
    this.openPdf.emit({ docId: this.regulationDocId, page });
  }

  onViewPolicyPdf(): void {
    if (this.policyDocId) this.openPdf.emit({ docId: this.policyDocId, page: this.policyPage });
  }

  onViewPolicyPage(page: string, docId?: string | null): void {
    const id = docId ?? this.policyDocId;
    if (id) this.openPdf.emit({ docId: id, page });
  }

  onViewGapEvidence(storedDocumentId: string): void {
    this.openPdf.emit({ docId: storedDocumentId });
  }

  get policyRefsHaveInlineQuotes(): boolean {
    return this.policyRefs.some((r) => Boolean(r.quote?.trim()));
  }

  formatPolicyRefLabel(ref: PolicyRefProof): string {
    return formatPolicyRefLabel(ref, this.policyDocCatalog.length > 1);
  }

  canOpenPolicyRef(ref: PolicyRefProof): boolean {
    return Boolean(ref.docId ?? this.policyDocId);
  }

  attachmentsForGap(actionIndex: number): PointGapAttachment[] {
    return this.gapAttachments.filter((a) => (a.actionIndex ?? null) === actionIndex);
  }

  showGapEvidenceForGap(gap: CapGap): boolean {
    if (this.resolvedSeverity === 'compliant') return false;
    return isMeaningfulCapGap(gap) || this.canUploadEvidence;
  }

  isUploadingGap(actionIndex: number): boolean {
    return this.evidenceUploading && this.evidenceUploadingActionIndex === actionIndex;
  }

  isRerunningGap(actionIndex: number): boolean {
    return this.evidenceRerunning && this.evidenceRerunningActionIndex === actionIndex;
  }

  onGapFilesSelected(actionIndex: number, event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.uploadGapEvidence.emit({ actionIndex, files: input.files });
    input.value = '';
  }

  get hasRegulationSource(): boolean {
    return Boolean(this.regulationDocId && (this.regulationPageLabel || this.regulationPage != null));
  }

  get hasPolicySource(): boolean {
    return Boolean(
      this.policyRefs.length ||
        this.policyDocId ||
        this.policyDocCatalog.length ||
        this.policyPage ||
        this.policySection,
    );
  }

  get displayGapCount(): number {
    if (this.resolvedSeverity === 'compliant') return 0;
    return this.capGaps.length;
  }

  regSourceTooltip(): string {
    const page = this.regulationPageLabel ?? 'start of document';
    return `Open regulation PDF at ${page}`;
  }

  policySourceTooltip(page?: string | null, docLabel?: string): string {
    const p = page ?? this.policyPage;
    const label = docLabel ? `${docLabel} — ` : '';
    return p ? `${label}Open policy PDF at p. ${p}` : `${label}Open policy PDF`.trim();
  }

  evidenceTooltip(fileName: string): string {
    return `Open uploaded evidence: ${fileName}`;
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

  actionReviewsForGap(index: number): ActionItemReviewEntry[] {
    return reviewsForAction(this.savedActionItemReviews, index);
  }

  actionReviewDraft(index: number): ActionItemReviewDraft {
    return this.newReviewDrafts[index] ?? emptyActionItemReviewDraft();
  }

  setActionReviewStatus(index: number, status: ActionItemReviewStatus): void {
    const draft = this.actionReviewDraft(index);
    this.newReviewDrafts = { ...this.newReviewDrafts, [index]: { ...draft, status } };
  }

  setActionReviewComment(index: number, comment: string): void {
    const draft = this.actionReviewDraft(index);
    this.newReviewDrafts = { ...this.newReviewDrafts, [index]: { ...draft, comment } };
  }

  setActionReviewResponsibility(index: number, responsibility: string): void {
    const draft = this.actionReviewDraft(index);
    this.newReviewDrafts = { ...this.newReviewDrafts, [index]: { ...draft, responsibility } };
  }

  setActionReviewDueDate(index: number, dueDate: string): void {
    const draft = this.actionReviewDraft(index);
    this.newReviewDrafts = { ...this.newReviewDrafts, [index]: { ...draft, dueDate } };
  }

  setActionReviewPriority(index: number, priority: GapPriority | ''): void {
    const draft = this.actionReviewDraft(index);
    this.newReviewDrafts = { ...this.newReviewDrafts, [index]: { ...draft, priority } };
  }

  reviewPriorityFor(index: number): GapPriority | '' {
    return normalizeGapPriority(this.actionReviewDraft(index).priority);
  }

  savedReviewPriority(rev: ActionItemReviewEntry): GapPriority | '' {
    return normalizeGapPriority(rev.priority ?? '');
  }

  canSaveActionReview(index: number): boolean {
    return !!this.actionReviewDraft(index).status && this.savingActionReviewIndex !== index;
  }

  onSaveActionReview(index: number): void {
    const draft = this.actionReviewDraft(index);
    if (!draft.status) return;
    this.saveActionItemReview.emit({
      actionIndex: index,
      status: draft.status,
      comment: draft.comment,
      responsibility: draft.responsibility,
      dueDate: draft.dueDate,
      priority: draft.priority,
    });
  }

  clearReviewDraft(index: number): void {
    const next = { ...this.newReviewDrafts };
    delete next[index];
    this.newReviewDrafts = next;
    const nextForms = new Set(this.openReviewFormIndexes);
    nextForms.delete(index);
    this.openReviewFormIndexes = nextForms;
    const nextCal = new Set(this.openDueDateCalendarIndexes);
    nextCal.delete(index);
    this.openDueDateCalendarIndexes = nextCal;
  }

  formatReviewDate(iso: string): string {
    return formatDate(iso);
  }

  get showGapEvidenceSection(): boolean {
    return false;
  }

  onEvidenceFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.uploadEvidence.emit(input.files);
    input.value = '';
  }

  actionReviewStatusClass(status: ActionItemReviewStatus | ''): string {
    if (status === 'approve') return 'review-status-approve';
    if (status === 'need_modify') return 'review-status-modify';
    return '';
  }

  formatDate = formatDate;
}
