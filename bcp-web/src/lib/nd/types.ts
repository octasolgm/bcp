import type { ActionItemReviewEntry } from './action-item-review';

export type Department = {
  id: string;
  name: string;
  description?: string | null;
  isActive: boolean;
  documentCount?: number;
  libraryCount?: number;
  createdAt?: string;
};

export type DualVerifyLlmProviderOption = {
  id: string;
  label: string;
  models: string[];
  defaultModel: string;
  apiKeyConfigured: boolean;
};

export type DualVerifyLlmSettings = {
  provider: string;
  model: string;
  providers: DualVerifyLlmProviderOption[];
  updatedAt?: string | null;
  updatedBy?: string | null;
};

export type ActiveDualVerifyLlm = {
  provider: string;
  model: string;
  providerLabel: string;
};

export type AnalysisPromptSuggestion = {
  id: string;
  promptKey: string;
  comment: string;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
  createdBy?: string | null;
  createdByName?: string | null;
  updatedBy?: string | null;
  updatedByName?: string | null;
  appliedInVersionId?: string | null;
};

export type AnalysisPromptCoverage = {
  suggestionId: string;
  covered: boolean;
};

export type AnalysisPromptGenerateResult = {
  promptText: string;
  coverage: AnalysisPromptCoverage[];
};

export type AnalysisPromptVersion = {
  id: string;
  promptKey: string;
  versionNumber: number;
  label: string;
  promptText: string;
  isCurrent: boolean;
  createdAt: string;
  createdBy?: string | null;
  createdByName?: string | null;
};

export type AnalysisPromptDefinition = {
  key: string;
  label: string;
  workflow: string;
  description: string;
  text: string;
  currentVersionId?: string | null;
  versions: AnalysisPromptVersion[];
  suggestions: AnalysisPromptSuggestion[];
};

export type AnalysisPromptsCatalog = {
  workflows: { workflow: string; prompts: AnalysisPromptDefinition[] }[];
  prompts: AnalysisPromptDefinition[];
};

export type RegulationDocument = {
  id: string;
  source?: 'legacy' | 'nd' | 'manual';
  name: string;
  departmentId?: string | null;
  departmentName?: string | null;
  extractionStatus: string;
  extractionProgressLabel?: string | null;
  extractionProgressPct?: number | null;
  extractionParseChunkCompleted?: number | null;
  pointCount: number;
  extractedAt?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  storedDocumentId?: string | null;
  legacyHref?: string | null;
  isManual?: boolean;
  originalFileName?: string | null;
  uploadedBy?: string | null;
  uploadedByName?: string | null;
  extractedBy?: string | null;
  extractedByName?: string | null;
  isHidden?: boolean;
  hiddenAt?: string | null;
  convertedFromWord?: boolean;
  sourceOriginalFileName?: string | null;
  landingAiFileName?: string | null;
};

export type RegulationPoint = {
  id: string;
  pointNumber: string;
  pointTitle?: string | null;
  pointContent: string;
  pageReference?: string | null;
  pdfPage?: number | null;
  isIntroductionPoint?: boolean;
  isAnnexPoint?: boolean;
};

export type InternalDocument = {
  id: string;
  source?: 'legacy' | 'nd';
  title: string;
  originalFileName: string;
  version?: number;
  uploaded: string;
  uploadedAt?: string;
  sizeBytes?: number;
  department?: string;
  parseStatus?: 'pending' | 'processing' | 'parsed' | 'failed' | string;
  parsedAt?: string | null;
  parseError?: string | null;
  uploadedBy?: string | null;
  uploadedByName?: string | null;
  parsedBy?: string | null;
  parsedByName?: string | null;
  isHidden?: boolean;
  hiddenAt?: string | null;
  convertedFromWord?: boolean;
  sourceOriginalFileName?: string | null;
  landingAiFileName?: string | null;
  sectionExtractStatus?: 'pending' | 'processing' | 'extracted' | 'failed' | string;
  sectionCount?: number | null;
  sectionExtractedAt?: string | null;
  sectionExtractError?: string | null;
  sectionExtractProgressLabel?: string | null;
  sectionExtractProgressPct?: number | null;
  sectionExtractedByName?: string | null;
};

export type InternalDocumentSection = {
  id: string;
  sectionRef: string;
  sectionText: string;
  sourcePage?: number | null;
  displayOrder?: number;
};

export type LibrarySummary = {
  id: string;
  name: string;
  description?: string | null;
  departmentId?: string | null;
  pointCount: number;
  documentCount: number;
  createdBy?: string | null;
  createdAt: string;
};

export type LibraryPointInput = {
  regulationPointId: string;
  regulationDocumentId: string;
  displayOrder: number;
  pointSnapshot?: Record<string, unknown>;
};

export type AnalysisRunSummary = {
  id: string;
  source?: 'nd_analysis' | 'legacy_analysis' | 'legacy_dual_verify';
  name: string;
  status: string;
  statusBeforeDelete?: string | null;
  deletedAt?: string | null;
  totalPointsCount: number;
  processedPointsCount: number;
  dualVerifyFailedCount?: number;
  departmentId?: string | null;
  createdBy?: string | null;
  createdAt: string;
  updatedAt?: string;
  submittedToCheckerAt?: string | null;
  legacySessionId?: string | null;
  legacyHref?: string | null;
  makerName?: string;
  compliant?: number;
  partial?: number;
  nonCompliant?: number;
  /** Gap risk bands from CAP priority (0–33 / 34–66 / 67–100). */
  criticalGaps?: number;
  mediumGaps?: number;
  lowGaps?: number;
  submittedAt?: string;
  workflowHolder?: string;
  totalGaps?: number;
  reviewedGaps?: number;
  totalReviews?: number;
  workflowEngine?: string | null;
  regulPipelinePhase?: string | null;
  regulClauseTotal?: number;
  regulClauseCompleted?: number;
  regulClauseFailed?: number;
  regulReverseSectionFailed?: number;
  regulLlmProvider?: string | null;
  regulLlmModel?: string | null;
  runningPoints?: number;
  isActive?: boolean;
};

export type AnalysisPoint = {
  id: string;
  regulationPointId?: string | null;
  pointSnapshot: string;
  /** Regul forward judgment status (API primary name when workflowEngine=regul_pipeline). */
  regulForwardStatus?: string | null;
  regulForwardResult?: string | null;
  regulForwardError?: string | null;
  landingAiStatus: string;
  landingAiResult?: string | null;
  landingAiError?: string | null;
  googleAiStatus: string;
  googleAiResult?: string | null;
  googleAiError?: string | null;
  dualVerifyStatus: string;
  finalStatus?: string | null;
  finalActionPlan?: string | null;
  originalAiActionPlan?: string | null;
};

export type PointGapAttachment = {
  id: string;
  analysisPointId: string;
  actionIndex?: number | null;
  storedDocumentId: string;
  fileName: string;
  parseStatus?: string | null;
  sizeBytes?: number | null;
  createdAt: string;
};

export type ResultsData = {
  run: {
    id: string;
    name: string;
    status: string;
    totalPointsCount: number;
    processedPointsCount: number;
    dualVerifyFailedCount: number;
    createdByName?: string | null;
    createdAt: string;
  };
  points: AnalysisPoint[];
  pointAttachments?: PointGapAttachment[];
  reviews: { id: string; reviewerRole: string; action: string; overallComment?: string; createdAt: string }[];
  comments: { id: string; analysisPointId: string; comment: string; createdAt: string }[];
  actionItemReviews?: ActionItemReviewEntry[];
};

export type ActionPlanHistoryEntry = {
  id: string;
  versionNumber: number;
  actionPlanContent: string;
  changeType: string;
  revertedToVersion?: number | null;
  isCurrent: boolean;
  createdAt: string;
  changedByName?: string | null;
};

export type PointSnapshot = {
  pointNumber?: string;
  pointTitle?: string;
  pointContent?: string;
  pageReference?: string;
  pdfPage?: number | null;
  regulationDocumentId?: string;
  regulationPointId?: string;
};
