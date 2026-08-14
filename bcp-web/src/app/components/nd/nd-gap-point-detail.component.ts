import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  SimpleChanges,
  inject,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  hasDisplayableFulfilledClauses,
  parseBulletLines,
  parseCapGaps,
  parseReferenceCitation,
  parseReferenceComplianceBlock,
  resolvePolicyExtractText,
  resolvePolicyRefAndExtract,
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
import { meaningfulCapGaps, isMeaningfulCapGap, isWeakCorrectivePlan, buildFallbackCapGaps, resolveAiCorrectiveActionForPoint, isVerificationMetaCapText, parseRegulElementCapGaps, looksLikeRegulElementAssessment } from '../../../lib/nd/cap-gap-count';
import { agreementBadgeClass, type AgreementStatus, type DualVerifyAgreement } from '../../../lib/landing-ai/dual-verify-merge';
import { ReferenceComplianceCardComponent } from '../reference-compliance-card/reference-compliance-card.component';
import type { ActionPlanHistoryEntry, AnalysisPoint, Department, PointGapAttachment, PointSnapshot } from '../../../lib/nd/types';
import { resolveSnapshotDisplayNumber, regulatoryRequirementText, isUuidLike } from '../../../lib/nd/utils';
import { NdApiService } from '../../services/nd/nd-api.service';
import {
  complianceSeverityLabel,
  resolveAnalysisPointSeverity,
  resolveDisplayConfidence,
  parseConfidencePercent,
  type ComplianceSeverity,
} from '../../../lib/nd/point-compliance-status';
import {
  actionReviewStatusLabel,
  POINT_REVIEW_ACTION_INDEX,
  reviewsForAction,
  reviewsForPointLevel,
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
import { NdItemReviewSectionComponent, type ItemReviewSaveEvent } from './nd-item-review-section.component';
import { NdActionPlansSectionComponent } from './nd-action-plans-section.component';
import { actionPlansForGap, type ActionPlanEntry } from '../../../lib/nd/action-plan';
import {
  NdTempPointReviewCommentsComponent,
} from './nd-temp-point-review-comments.component';
import type { TempPointReviewComment, TempReviewCommentsChangeEvent } from '../../../lib/nd/temp-point-review-comment';

@Component({
  selector: 'app-nd-gap-point-detail',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ReferenceComplianceCardComponent,
    NdItemReviewSectionComponent,
    NdActionPlansSectionComponent,
    NdTempPointReviewCommentsComponent,
  ],
  templateUrl: './nd-gap-point-detail.component.html',
  styleUrl: './nd-gap-point-detail.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NdGapPointDetailComponent implements OnChanges {
  private readonly ndApi = inject(NdApiService);
  private readonly cdr = inject(ChangeDetectorRef);
  private lastPointId: string | null = null;
  private contentKey = '';
  private collapsedActionsInit = false;
  private departmentsLoaded = false;

  collapsedActionIndexes = new Set<number>();
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
  /** Resolved clause number (e.g. 2.2) — overrides snapshot UUID labels. */
  @Input() displayClause: string | null = null;
  @Input() phaseOutputDefaultOpen = false;
  /** Phase 2 dual-verify rerun in flight for this point. */
  @Input() phase2Running = false;
  /** Phase 1 Landing AI rerun in flight (full point rerun). */
  @Input() phase1Running = false;
  /** Checker/reviewer: per-action status + comment controls. */
  @Input() reviewMode = false;
  /** Show review history / add-review area for checker, reviewer, super_admin. */
  @Input() showReviewPanel = false;
  @Input() reviewDisabledHint = '';
  /** In-progress drafts for adding a new review (review mode). */
  @Input() savingActionReviewIndex: number | null = null;
  @Input() savingReviewId: string | null = null;
  /** All saved reviews for this point (newest first per action in template). */
  @Input() savedActionItemReviews: ActionItemReviewEntry[] = [];
  @Input() runId: string | null = null;
  @Input() gapAttachments: PointGapAttachment[] = [];
  @Input() canUploadEvidence = false;
  @Input() evidenceUploading = false;
  @Input() evidenceRerunning = false;
  @Input() evidenceUploadingActionIndex: number | null = null;
  @Input() evidenceRerunningActionIndex: number | null = null;
  /** Pin point-level review to bottom with scrollable detail above (list / review workspace). */
  @Input() dockPointReview = false;
  /** Regul workflow V3 — forward/reverse labels, no V8 dual-verify pass 2 block. */
  @Input() isRegulWorkflow = false;
  /** Temporary manual review notes for this point. */
  @Input() tempReviewComments: TempPointReviewComment[] = [];
  @Input() canEditTempReviewComments = true;
  /** Corrective action plans for this gap (loaded with the run results). */
  @Input() actionPlans: ActionPlanEntry[] = [];
  @Input() canEditActionPlans = false;
  @Input() canReviewActionPlans = false;

  @Output() startEdit = new EventEmitter<void>();
  @Output() cancelEdit = new EventEmitter<void>();
  @Output() save = new EventEmitter<string>();
  @Output() openHistory = new EventEmitter<void>();
  @Output() closeHistory = new EventEmitter<void>();
  @Output() restoreVersion = new EventEmitter<ActionPlanHistoryEntry>();
  @Output() openPdf = new EventEmitter<{ docId: string; page?: string | null }>();
  @Output() saveActionItemReview = new EventEmitter<ItemReviewSaveEvent>();
  @Output() deleteActionItemReview = new EventEmitter<string>();
  @Output() reorderActionItemReview = new EventEmitter<{
    reviewId: string;
    actionIndex: number;
    direction: 'up' | 'down';
  }>();
  @Output() uploadEvidence = new EventEmitter<FileList>();
  @Output() uploadGapEvidence = new EventEmitter<{ actionIndex: number; files: FileList }>();
  @Output() deleteEvidence = new EventEmitter<string>();
  @Output() deleteGapEvidence = new EventEmitter<{ actionIndex: number; attachmentId: string }>();
  @Output() rerunWithEvidence = new EventEmitter<'full' | 'dual'>();
  @Output() rerunGapEvidence = new EventEmitter<{ actionIndex: number; mode: 'full' | 'dual' }>();
  @Output() tempReviewCommentsChanged = new EventEmitter<TempReviewCommentsChangeEvent>();
  @Output() actionPlansChanged = new EventEmitter<void>();
  @Output() viewActionPlanReviews = new EventEmitter<ActionPlanEntry>();

  readonly actionReviewStatusLabel = actionReviewStatusLabel;
  readonly pointReviewActionIndex = POINT_REVIEW_ACTION_INDEX;
  readonly gapPriorityOptions = GAP_PRIORITY_OPTIONS;
  readonly gapPriorityLabel = gapPriorityLabel;
  readonly gapPriorityClass = gapPriorityClass;

  pointHeading = '';
  regulatoryText = '';
  policyExtract = '';
  documentReference = '';
  documentRefLines: string[] = [];
  documentRefDocId: string | null = null;
  policyPage: string | null = null;
  policySection: string | null = null;
  policyRefLabel = '';
  policyRefs: PolicyRefProof[] = [];
  regulationPage: number | null = null;
  regulationPageLabel: string | null = null;
  landingMessage = '';
  llmMessage = '';
  googleAiError = '';
  pass2ErrorTitle = 'Phase 2 failed';
  pass2ErrorMessage = '';
  pass2ErrorFull = '';
  pass1ErrorTitle = 'Phase 1 failed';
  pass1ErrorMessage = '';
  pass1ErrorFull = '';
  agreement?: DualVerifyAgreement;
  primaryBlock!: ReferenceComplianceBlock;
  showFulfilled = false;
  fulfilledLines: string[] = [];
  gapAnalysisText = '';
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
  resolvedSeverity: ComplianceSeverity | null = null;
  capSourceLabel = 'Compliance AI draft';

  get pointLevelReviews(): ActionItemReviewEntry[] {
    return reviewsForPointLevel(this.savedActionItemReviews);
  }

  get showTempReviewComments(): boolean {
    return Boolean(this.runId?.trim());
  }

  ngOnChanges(changes: SimpleChanges): void {
    const nextKey = [
      this.point?.id ?? '',
      this.point?.pointSnapshot ?? '',
      this.point?.finalActionPlan ?? '',
      this.point?.originalAiActionPlan ?? '',
      this.point?.landingAiResult ?? '',
      this.point?.landingAiError ?? '',
      this.point?.googleAiResult ?? '',
      this.point?.googleAiError ?? '',
    ].join('\u001f');

    if (nextKey !== this.contentKey) {
      this.contentKey = nextKey;
      queueMicrotask(() => this.rebuildContent());
    }

    if (this.showReviewPanel && !this.departmentsLoaded && (changes['showReviewPanel'] || changes['point'])) {
      void this.loadDepartments();
    }
  }

  private rebuildContent(): void {
    if (this.point?.id !== this.lastPointId) {
      this.lastPointId = this.point?.id ?? null;
      this.collapsedActionsInit = false;
      this.collapsedActionIndexes = new Set();
    }

    const snap = this.snapshot;
    const fromInput =
      this.displayClause?.trim() && !isUuidLike(this.displayClause) ? this.displayClause.trim() : '';
    const fromSnap = resolveSnapshotDisplayNumber(snap ?? {}, this.point.regulationPointId);
    const displayNum = fromInput || (fromSnap && !isUuidLike(fromSnap) ? fromSnap : '');
    let title = snap?.pointTitle?.trim() || '';
    if (title && isUuidLike(title.split(/\s+/)[0]?.replace(/^§/, ''))) {
      title = title.replace(/^§?\s*[0-9a-f-]{36}\s*[—–\-]\s*/i, '').trim();
    }
    this.pointHeading = displayNum
      ? title
        ? `§${displayNum} — ${title}`
        : `§${displayNum}`
      : title || 'Regulation point';

    this.regulatoryText = regulatoryRequirementText(snap, {
      title: snap?.pointTitle ?? undefined,
      text: snap?.pointContent ?? '',
    }) || '—';

    this.regulationPage = resolveRegulationPdfPage(snap?.pageReference, snap?.pdfPage ?? null);
    this.regulationPageLabel = formatPointPageRef(snap?.pageReference, this.regulationPage);

    this.landingMessage = this.extractMessage(this.point.landingAiResult);
    this.llmMessage = this.extractMessage(this.point.googleAiResult);
    this.googleAiError = this.point.googleAiError?.trim() ?? '';
    const pass2Err = this.formatPhaseError(this.googleAiError, 'Phase 2 dual verify failed');
    this.pass2ErrorTitle = pass2Err.title;
    this.pass2ErrorMessage = pass2Err.message;
    this.pass2ErrorFull = pass2Err.full;
    const landingErrRaw = this.point.landingAiError?.trim() ?? '';
    const pass1Err = this.formatPhaseError(landingErrRaw, 'Phase 1 (Landing AI) failed');
    this.pass1ErrorTitle = pass1Err.title;
    this.pass1ErrorMessage = pass1Err.message;
    this.pass1ErrorFull = pass1Err.full;
    this.agreement = this.extractAgreement(this.point.googleAiResult);

    const primaryMsg = this.isRegulWorkflow
      ? (this.landingMessage || this.llmMessage).trim()
      : (this.llmMessage || this.landingMessage).trim();
    this.primaryBlock = parseReferenceComplianceBlock(primaryMsg);
    this.showFulfilled = hasDisplayableFulfilledClauses(this.primaryBlock.fulfilledClauses);
    this.fulfilledLines = parseBulletLines(this.primaryBlock.fulfilledClauses ?? '');
    const gapRaw = (this.primaryBlock.gapAnalysis ?? '').trim();
    this.gapAnalysisText =
      gapRaw && !/^n\/a$/i.test(gapRaw) && gapRaw !== '—' ? gapRaw : '';
    this.responsibility =
      this.primaryBlock.responsibility && this.primaryBlock.responsibility !== 'N/A'
        ? this.primaryBlock.responsibility
        : '';

    // Policy extract is the full Output/Response body (same as Excel export), not short citation quotes.
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

    let bestExtract = '';
    for (const msg of [this.landingMessage, this.llmMessage]) {
      if (!msg?.trim()) continue;
      const structured = parseReferenceComplianceBlock(msg);
      const extract = resolvePolicyExtractText(structured);
      if (extract.length > bestExtract.length) {
        bestExtract = extract;
        const cite = parseReferenceCitation(extract);
        if (!this.policyPage) {
          this.policyPage = cite.page;
          this.policySection = cite.section;
        }
      }
    }
    this.policyExtract = bestExtract;

    const { documentReference: resolvedDocRef } = resolvePolicyRefAndExtract(
      parseReferenceComplianceBlock(this.landingMessage),
      parseReferenceComplianceBlock(this.llmMessage),
    );
    if (resolvedDocRef) {
      this.documentReference = resolvedDocRef;
      this.documentRefLines = resolvedDocRef
        .split(/[;\n]+/)
        .map((l) => l.trim())
        .filter(Boolean);
    } else {
      this.documentRefLines = [];
    }

    const resolvedDocId =
      resolvePolicyDocId(resolvedDocRef || this.primaryBlock.documentReference, catalog) ??
      resolvePolicyDocId(this.primaryBlock.referencePdf, catalog) ??
      this.policyRefs.find((r) => r.docId)?.docId ??
      this.policyDocId;
    this.documentRefDocId = resolvedDocId ?? null;

    if (!this.policyExtract) {
      const notStarted =
        !this.landingMessage?.trim() &&
        !this.llmMessage?.trim() &&
        (this.point.landingAiStatus === 'pending' || !this.point.landingAiStatus) &&
        (this.point.dualVerifyStatus === 'pending' || !this.point.dualVerifyStatus);
      this.policyExtract = notStarted
        ? 'Analysis not started yet — run forward/reverse to extract policy text.'
        : 'No corresponding policy extract found.';
    }

    const refPdf =
      resolvedDocRef ||
      this.primaryBlock.documentReference?.trim() ||
      this.primaryBlock.referencePdf?.trim() ||
      '';
    if (refPdf && refPdf.toLowerCase() !== 'internal policy manual' && refPdf !== 'N/A' && refPdf !== '—') {
      if (!this.documentReference) this.documentReference = refPdf;
      if (!this.policyRefLabel) this.policyRefLabel = refPdf;
      if (!this.policyPage) {
        const ppMatch = refPdf.match(/\bpp\.?\s*(\d+(?:\s*[-–]\s*\d+)?)/i);
        if (ppMatch?.[1]) this.policyPage = ppMatch[1].trim();
      }
      const sectionParen = refPdf.match(/\(([^)]+)\)\s*$/);
      if (!this.policySection && sectionParen?.[1]) {
        this.policySection = sectionParen[1].trim();
      }
    }

    const refParts: string[] = [];
    if (catalog.length > 1 && resolvedDocId) {
      refParts.push(docLabelForId(resolvedDocId, catalog));
    }
    if (this.policyPage) refParts.push(`Page ${this.policyPage}`);
    if (this.policySection) refParts.push(`Section ${this.policySection}`);
    this.policyRefLabel = refParts.join(', ');

    this.originalPlan = this.point.originalAiActionPlan?.trim() ?? '';
    this.currentPlan = this.point.finalActionPlan?.trim() ?? this.originalPlan;
    const aiCap = resolveAiCorrectiveActionForPoint(this.point);
    let capSource = this.currentPlan || this.originalPlan || aiCap;
    if (looksLikeRegulElementAssessment(capSource)) capSource = '';
    const effectiveCap =
      capSource && !isVerificationMetaCapText(capSource) && !isWeakCorrectivePlan(capSource)
        ? capSource
        : '';
    if (!this.currentPlan && effectiveCap) this.currentPlan = effectiveCap;
    if (!effectiveCap && isWeakCorrectivePlan(this.currentPlan)) {
      this.currentPlan = '';
    }
    this.resolvedSeverity = resolveAnalysisPointSeverity(this.point);
    this.capGaps = effectiveCap ? meaningfulCapGaps(effectiveCap) : [];
    // Old Landing placeholder CAP ("Re-run comparison…") has no real gap/action —
    // synthesize from the regulatory requirement so the panel is usable.
    // Never invent CAP for queued / unscored points.
    if (
      !this.isRegulWorkflow &&
      this.resolvedSeverity &&
      this.resolvedSeverity !== 'compliant' &&
      (this.capGaps.length === 0 || isWeakCorrectivePlan(effectiveCap))
    ) {
      this.capGaps = buildFallbackCapGaps(this.regulatoryText, this.resolvedSeverity);
      if (!this.currentPlan || isWeakCorrectivePlan(this.currentPlan)) {
        const g = this.capGaps[0];
        this.currentPlan = `Gap(s):\n(1) Missing: ${g.missing}. Fix: ${g.fix}. Priority: ${g.priority || 'Medium'}.`;
      }
    }

    const userEditedPlan =
      Boolean(this.point.finalActionPlan?.trim()) &&
      this.point.finalActionPlan!.trim() !== (this.point.originalAiActionPlan?.trim() ?? '') &&
      !looksLikeRegulElementAssessment(this.point.finalActionPlan!);

    if (
      this.isRegulWorkflow &&
      gapRaw &&
      this.resolvedSeverity &&
      this.resolvedSeverity !== 'compliant' &&
      !userEditedPlan
    ) {
      const fixFromCap = this.primaryBlock.correctiveAction?.trim() ?? '';
      const fix =
        fixFromCap &&
        !isWeakCorrectivePlan(fixFromCap) &&
        fixFromCap !== '—' &&
        fixFromCap !== 'N/A'
          ? fixFromCap
          : '';
      const elementGaps = parseRegulElementCapGaps(gapRaw);
      if (elementGaps.length > 0) {
        this.capGaps = elementGaps.map((g, i) => ({
          ...g,
          index: i + 1,
          fix: g.fix || fix,
        }));
      } else {
        this.capGaps = [
          {
            index: 1,
            missing: this.gapAnalysisText,
            fix,
            priority: 'Medium',
          },
        ];
      }
    }

    this.capSourceLabel = userEditedPlan ? 'Edited' : 'Compliance AI draft';

    this.showCapSection =
      this.resolvedSeverity === 'partial_compliant' ||
      this.resolvedSeverity === 'non_compliant' ||
      (this.capGaps.length > 0 && this.resolvedSeverity !== 'compliant') ||
      (userEditedPlan && this.capGaps.length > 0);

    if (!this.editing) {
      this.resetEditState();
    }

    this.initActionCollapseState();
    this.cdr.markForCheck();
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
    this.cdr.markForCheck();
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

  reviewCountForGap(index: number): number {
    return this.actionReviewsForGap(index).length;
  }

  /** Actions the user has attached to one CAP gap. */
  actionPlansForGapIndex(index: number): ActionPlanEntry[] {
    return actionPlansForGap(this.actionPlans, index);
  }

  actionCountForGap(index: number): number {
    return this.actionPlansForGapIndex(index).length;
  }

  onItemReviewSave(event: ItemReviewSaveEvent): void {
    this.saveActionItemReview.emit(event);
  }

  get displayComplianceStatus(): string {
    return this.resolvedSeverity ? complianceSeverityLabel(this.resolvedSeverity) : 'Pending';
  }

  get displayConfidence(): string {
    const fromBlock = this.primaryBlock?.confidence?.trim();
    if (fromBlock) {
      const pct = parseConfidencePercent(fromBlock);
      if (pct != null) return `${pct}%`;
    }
    const resolved = resolveDisplayConfidence(this.point);
    const pct = parseConfidencePercent(resolved);
    if (pct != null) return `${pct}%`;
    return resolved;
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

  onViewDocumentReference(): void {
    if (!this.documentRefDocId) return;
    const line = this.documentRefLines[0] ?? this.documentReference;
    this.onViewDocumentReferenceLine(line);
  }

  onViewDocumentReferenceLine(line: string): void {
    if (!this.documentRefDocId) return;
    const pageMatch = line.match(/(?:p\.?|page)\s*(\d+)/i);
    const page = pageMatch?.[1] ?? this.policyPage;
    this.onViewPolicyPage(page, this.documentRefDocId);
  }

  onViewPolicyPage(page?: string | null, docId?: string | null): void {
    const id = docId ?? this.policyDocId;
    if (id) this.openPdf.emit({ docId: id, page: page ?? undefined });
  }

  onViewGapEvidence(storedDocumentId: string): void {
    this.openPdf.emit({ docId: storedDocumentId });
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

  /** User-facing phase error (strip HTTP wrapper + parse JSON when present). */
  private formatPhaseError(raw: string, title: string): { title: string; message: string; full: string } {
    const full = raw.trim();
    if (!full) return { title, message: '', full: '' };

    let rest = full.replace(/^(Anthropic API error|Landing AI[^:]*)\s*\(\w+\):\s*/i, '').trim();
    const jsonStart = rest.indexOf('{');
    if (jsonStart >= 0) {
      try {
        const parsed = JSON.parse(rest.slice(jsonStart)) as {
          error?: { message?: string };
          message?: string;
        };
        const inner = parsed.error?.message ?? parsed.message;
        if (typeof inner === 'string' && inner.trim()) {
          return { title, message: inner.trim(), full };
        }
      } catch {
        /* use rest below */
      }
    }

    return { title, message: rest || full, full };
  }

  formatDate = formatDate;
}
