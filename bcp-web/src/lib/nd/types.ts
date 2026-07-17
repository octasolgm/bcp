export type Department = {
  id: string;
  name: string;
  description?: string | null;
  isActive: boolean;
  documentCount?: number;
  libraryCount?: number;
  createdAt?: string;
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
  storedDocumentId?: string | null;
  legacyHref?: string | null;
  isManual?: boolean;
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
  reviews: { id: string; reviewerRole: string; action: string; overallComment?: string; createdAt: string }[];
  comments: { id: string; analysisPointId: string; comment: string; createdAt: string }[];
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
