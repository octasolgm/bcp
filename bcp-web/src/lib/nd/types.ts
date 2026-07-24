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

export type RegulationDocument = {
  id: string;
  source?: 'legacy' | 'nd' | 'manual';
  name: string;
  departmentId?: string | null;
  departmentName?: string | null;
  extractionStatus: string;
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
  submittedToCheckerAt?: string | null;
  legacySessionId?: string | null;
  legacyHref?: string | null;
  makerName?: string;
  compliant?: number;
  partial?: number;
  nonCompliant?: number;
  submittedAt?: string;
  workflowHolder?: string;
  totalGaps?: number;
  reviewedGaps?: number;
  totalReviews?: number;
};

export type AnalysisPoint = {
  id: string;
  regulationPointId?: string | null;
  pointSnapshot: string;
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
  regulationDocumentId?: string;
  regulationPointId?: string;
};
