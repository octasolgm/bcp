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
import {
  actionPlansForGap,
  actionPlansForPoint,
  actionPlanPriorityFromScore,
  actionPlanPriorityLabel,
  actionPlanScoreLabel,
  clampActionPlanScore,
  normalizeActionPlanStatus,
  ACTION_PLAN_PRIORITY_OPTIONS,
  ACTION_PLAN_PRIORITY_SCALE,
  ACTION_PLAN_STATUS_OPTIONS,
  DEFAULT_ACTION_PLAN_PRIORITY_SCORE,
  type ActionPlanEntry,
  type ActionPlanPriority,
} from '../../../lib/nd/action-plan';
import { normalizeGapRisk } from '../../../lib/nd/doc-analysis-ready';
import {
  canResolveGapByHand,
  deriveGapStatus,
  gapRiskScore,
  gapRiskTargetHint,
  gapStateKey,
  gapStatusLabel,
  type GapState,
  type GapStatus,
} from '../../../lib/nd/gap-state';
import { NdAuthService } from '../../services/nd/nd-auth.service';
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
  private readonly auth = inject(NdAuthService);
  private readonly cdr = inject(ChangeDetectorRef);
  private loadedActionPlans: ActionPlanEntry[] = [];
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
  @Input() evidenceDeletingAttachmentId: string | null = null;
  /** Pin point-level review to bottom with scrollable detail above (list / review workspace). */
  @Input() dockPointReview = false;
  /** Regul workflow V3 — forward/reverse labels, no V8 dual-verify pass 2 block. */
  @Input() isRegulWorkflow = false;
  /** Temporary manual review notes for this point. */
  @Input() tempReviewComments: TempPointReviewComment[] = [];
  @Input() canEditTempReviewComments = true;
  /** Corrective action plans for this gap (loaded with the run results). */
  /** Deep link from My actions: CAP gap index to keep open, and the action to reveal. */
  @Input() focusGapIndex: number | null = null;
  @Input() focusPlanId: string | null = null;
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
  pendingRemoveId: string | null = null;
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

    if ((changes['runId'] || changes['point'] || changes['actionPlans']) && this.runId && this.point?.id) {
      if (this.actionPlans.length > 0) {
        this.loadedActionPlans = this.actionPlans;
      } else {
        void this.loadActionPlans();
      }
    }

    if (this.pendingRemoveId && !this.gapAttachments.some((a) => a.id === this.pendingRemoveId)) {
      this.pendingRemoveId = null;
    }
  }

  /** Every role may add or edit action plans when viewing a saved run. */
  get effectiveCanEditActionPlans(): boolean {
    return Boolean(this.runId);
  }

  get effectiveCanReviewActionPlans(): boolean {
    const role = this.auth.getRole();
    return role === 'super_admin' || role === 'checker' || role === 'reviewer';
  }

  private get resolvedActionPlans(): ActionPlanEntry[] {
    return this.actionPlans.length > 0 ? this.actionPlans : this.loadedActionPlans;
  }

  private async loadActionPlans(): Promise<void> {
    if (!this.runId || !this.point?.id) return;
    const res = await this.ndApi.getActionPlans(this.runId);
    if (res.success && res.data) {
      this.loadedActionPlans = actionPlansForPoint(res.data, this.point.id);
      this.cdr.markForCheck();
    }
  }

  async onActionPlansChanged(): Promise<void> {
    if (this.actionPlans.length === 0) {
      await this.loadActionPlans();
    }
    this.actionPlansChanged.emit();
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

  /** Only hand the deep-link action id to the gap that actually owns it. */
  focusPlanIdForGap(index: number): string | null {
    if (!this.focusPlanId) return null;
    if (this.focusGapIndex != null && this.focusGapIndex !== index) return null;
    return this.actionPlansForGapIndex(index).some((p) => p.id === this.focusPlanId)
      ? this.focusPlanId
      : null;
  }

  reviewCountForGap(index: number): number {
    return this.actionReviewsForGap(index).length;
  }

  /** Actions the user has attached to one CAP gap. */
  actionPlansForGapIndex(index: number): ActionPlanEntry[] {
    return actionPlansForGap(this.resolvedActionPlans, index);
  }

  actionCountForGap(index: number): number {
    return this.actionPlansForGapIndex(index).length;
  }

  // ------------------------------------------------------------- gap state

  readonly gapRiskOptions = ACTION_PLAN_PRIORITY_OPTIONS;
  readonly gapStatusOptions = ACTION_PLAN_STATUS_OPTIONS;
  readonly gapPriorityScale = ACTION_PLAN_PRIORITY_SCALE;
  readonly gapStatusLabel = gapStatusLabel;

  /** Gap index currently open in the header edit panel. */
  editingGapStateIndex: number | null = null;
  gapEditDraft = { missing: '', riskScore: DEFAULT_ACTION_PLAN_PRIORITY_SCORE, status: 'pending' as GapStatus };
  savingGapState = false;
  /** Set when a resolve was refused because the gap still has open actions. */
  gapResolveBlockedIndex: number | null = null;

  /** Saved gap rows for this run, keyed `${pointId}:${gapIndex}`. */
  @Input() gapStates = new Map<string, GapState>();
  @Output() gapStateChanged = new EventEmitter<void>();

  gapStateFor(index: number): GapState | null {
    if (!this.point?.id) return null;
    return this.gapStates.get(gapStateKey(this.point.id, index)) ?? null;
  }

  /** Risk shown on the card: the saved value when there is one, else the AI's. */
  gapRisk(gap: CapGap): ActionPlanPriority {
    return this.gapStateFor(gap.index)?.risk ?? normalizeGapRisk(gap.priority);
  }

  gapRiskScore(gap: CapGap): number {
    return gapRiskScore(this.gapStateFor(gap.index), gap.priority);
  }

  gapRiskLabel(gap: CapGap): string {
    return actionPlanPriorityLabel(this.gapRisk(gap));
  }

  gapRiskClass(gap: CapGap): string {
    return `gap-risk-${this.gapRisk(gap)}`;
  }

  /** Resolved once every action on the gap is resolved. */
  gapStatus(gap: CapGap): GapStatus {
    return deriveGapStatus(this.actionPlansForGapIndex(gap.index), this.gapStateFor(gap.index));
  }

  gapTargetHint(gap: CapGap): string {
    return gapRiskTargetHint(this.gapRisk(gap));
  }

  private openActionCount(index: number): number {
    return this.actionPlansForGapIndex(index).filter(
      (p) => normalizeActionPlanStatus(p.status) !== 'resolved',
    ).length;
  }

  isEditingGapState(index: number): boolean {
    return this.editingGapStateIndex === index;
  }

  startEditGapState(gap: CapGap): void {
    this.editingGapStateIndex = gap.index;
    this.gapEditDraft = {
      missing: gap.missing ?? '',
      riskScore: this.gapRiskScore(gap),
      status: this.gapStatus(gap),
    };
    this.gapResolveBlockedIndex = null;
    this.cdr.markForCheck();
  }

  cancelEditGapState(): void {
    this.editingGapStateIndex = null;
    this.gapResolveBlockedIndex = null;
    this.cdr.markForCheck();
  }

  gapEditRiskLabel(): string {
    return actionPlanScoreLabel(this.gapEditDraft.riskScore);
  }

  gapEditTargetHint(): string {
    return gapRiskTargetHint(actionPlanPriorityFromScore(this.gapEditDraft.riskScore));
  }

  onGapEditScore(value: string | number): void {
    this.gapEditDraft.riskScore = clampActionPlanScore(Number(value));
  }

  /** Dismisses the "resolve the actions first" notice. */
  dismissGapResolveBlocked(): void {
    this.gapResolveBlockedIndex = null;
    this.cdr.markForCheck();
  }

  gapResolveBlockedMessage(index: number): string {
    const open = this.openActionCount(index);
    return open === 1
      ? 'This gap still has 1 action open. Resolve it before closing the gap.'
      : `This gap still has ${open} actions open. Resolve them before closing the gap.`;
  }

  /**
   * Saves the gap header edit: the gap text goes back into the clause's CAP blob, while
   * risk and resolve are stored per gap so the clause's other gaps are left alone.
   */
  async saveGapState(gap: CapGap): Promise<void> {
    if (!this.runId || !this.point?.id || this.savingGapState) return;

    const wantsResolved = this.gapEditDraft.status === 'resolved';
    if (wantsResolved && !canResolveGapByHand(this.actionPlansForGapIndex(gap.index))) {
      this.gapResolveBlockedIndex = gap.index;
      this.cdr.markForCheck();
      return;
    }

    this.savingGapState = true;
    this.gapResolveBlockedIndex = null;
    this.cdr.markForCheck();

    const missing = this.gapEditDraft.missing.trim();
    const textChanged = missing.length > 0 && missing !== (gap.missing ?? '').trim();
    if (textChanged) {
      gap.missing = missing;
      this.save.emit(serializeCapGaps(this.capGaps));
    }

    const res = await this.ndApi.updateRunGap(this.runId, this.point.id, gap.index, {
      riskScore: this.gapEditDraft.riskScore,
      status: this.gapEditDraft.status,
    });

    this.savingGapState = false;
    if (res.success) {
      this.editingGapStateIndex = null;
      this.gapStateChanged.emit();
    } else if ((res as { code?: string }).code === 'actions_pending') {
      this.gapResolveBlockedIndex = gap.index;
    }
    this.cdr.markForCheck();
  }

  // --------------------------------------------------- clause status override

  readonly clauseStatusOptions = [
    { value: 'compliant', label: 'Compliant' },
    { value: 'partial_compliant', label: 'Partial' },
    { value: 'non_compliant', label: 'Non-compliant' },
  ];

  savingClauseStatus = false;

  get canOverrideClauseStatus(): boolean {
    return !!this.runId && !!this.point?.id;
  }

  /** Blank means the clause is still following the analysis and the auto rule. */
  get clauseStatusValue(): string {
    return this.point?.finalStatusSource === 'manual' ? (this.point.finalStatus ?? '') : '';
  }

  get clauseStatusSourceNote(): string {
    if (this.point?.finalStatusSource === 'manual') return 'Set manually';
    if (this.point?.finalStatusSource === 'auto') return 'Auto — all actions resolved';
    return '';
  }

  get clauseStatusHint(): string {
    return 'Compliance verdict for this clause. Leave on Auto to follow the analysis and flip to compliant once every action is resolved.';
  }

  async setClauseStatus(value: string): Promise<void> {
    if (!this.runId || !this.point?.id || this.savingClauseStatus) return;

    this.savingClauseStatus = true;
    this.cdr.markForCheck();

    const res = await this.ndApi.updateClauseStatus(this.runId, this.point.id, value || null);
    this.savingClauseStatus = false;

    if (res.success && res.data) {
      this.point.finalStatus = res.data.finalStatus;
      this.point.finalStatusSource = res.data.finalStatusSource;
      this.point.aiFinalStatus = res.data.aiFinalStatus;
      this.rebuildContent();
      this.actionPlansChanged.emit();
    }
    this.cdr.markForCheck();
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
    this.draftGap = { index: next, missing: '', fix: '', priority: 'medium' };
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
    this.draftGap = gap
      ? { ...gap, missing: gap.missing }
      : { index, missing: '', fix: '', priority: '' };
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
    if (!this.draftGap.missing.trim()) return;

    let gaps: CapGap[];
    if (this.addingNewAction) {
      gaps = [
        ...this.capGaps,
        { ...this.draftGap, missing: this.draftGap.missing.trim(), fix: '', priority: '' },
      ];
    } else if (this.editingGapIndex != null) {
      gaps = this.capGaps.map((g) =>
        g.index === this.editingGapIndex
          ? { ...g, missing: this.draftGap.missing.trim() }
          : g,
      );
    } else {
      return;
    }

    gaps = gaps
      .filter((g) => g.missing.trim())
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
    return this.evidenceDeletingAttachmentId === id;
  }

  isRemovingAny(): boolean {
    return Boolean(this.evidenceDeletingAttachmentId);
  }

  askRemove(id: string): void {
    this.pendingRemoveId = id;
    this.cdr.markForCheck();
  }

  cancelRemove(): void {
    this.pendingRemoveId = null;
    this.cdr.markForCheck();
  }

  confirmRemoveGap(actionIndex: number, attachmentId: string): void {
    this.pendingRemoveId = null;
    this.deleteGapEvidence.emit({ actionIndex, attachmentId });
    this.cdr.markForCheck();
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

function formatAttachmentSize(bytes?: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes <= 0) return '';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
