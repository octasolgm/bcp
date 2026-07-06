export type BcpwebSeverity = 'critical' | 'high' | 'medium' | 'low' | 'compliant';

export interface BcpwebBranch {
  id: string;
  name: string;
  code: string;
}

export interface BcpwebRegulation {
  id: string;
  title: string;
  subtitle?: string;
  issuingBody: string;
  type: string;
  version: string;
  lastUpdated: string;
  status: 'Active' | 'Updated';
  clauseCount: number;
  category: string;
}

export interface BcpwebDocument {
  id: string;
  title: string;
  category: string;
  format: 'PDF' | 'DOC' | 'XLS';
  pageCount: number;
  uploadedAt: string;
  version: string;
  status: string;
  statusTone: 'red' | 'orange' | 'blue' | 'green' | 'yellow';
  sessionId?: string;
}

export interface BcpwebComplianceItem {
  id: string;
  sessionId: string;
  serialNo: number;
  clauseNo: string;
  sectionRef: string;
  title: string;
  severity: BcpwebSeverity;
  regulatoryText: string;
  regulatoryPdfPage: number;
  policyText: string;
  policyPdfPage: number;
  gapsIdentified: string;
  managementResponse: string;
  designEffectiveness: string;
  operatingEffectiveness: string;
  overallEffectiveness: string;
  documentReference: string;
  evidenceImplementation: string;
  evidenceReference: string;
  responsibleDepartment: string;
  complianceStatus: string;
  targetDate: string;
  conclusion: string;
  observation: string;
  actionPlan: string;
  assignedTo: string;
  signedOff: boolean;
  signedOffAt: string | null;
}

export interface BcpwebAnalysisSession {
  id: string;
  branchId: string;
  regulationId: string;
  regulationTitle: string;
  internalDocName: string;
  regulationDocName: string;
  status: 'pending' | 'processing' | 'completed' | 'failed';
  progressPct: number;
  progressStep: string;
  briefing: string;
  totalItems: number;
  criticalCount: number;
  highCount: number;
  mediumCount: number;
  lowCount: number;
  compliantCount: number;
  analysisDate: string;
  createdAt: string;
  completedAt: string | null;
}

export interface BcpwebDashboardMetrics {
  criticalGaps: number;
  highRisk: number;
  totalFindings: number;
  compliantItems: number;
  lastAnalysisDate: string;
  riskBreakdown: { name: string; value: number; color: string }[];
  remediationItems: {
    item: string;
    severity: BcpwebSeverity;
    target: string;
    status: string;
  }[];
  recentAnalyses: {
    id: string;
    title: string;
    date: string;
    findings: number;
    critical: number;
    high: number;
  }[];
}

export interface BcpwebUpdateItemDto {
  gapsIdentified?: string;
  managementResponse?: string;
  designEffectiveness?: string;
  operatingEffectiveness?: string;
  overallEffectiveness?: string;
  documentReference?: string;
  evidenceImplementation?: string;
  evidenceReference?: string;
  responsibleDepartment?: string;
  complianceStatus?: string;
  targetDate?: string;
  conclusion?: string;
  observation?: string;
  actionPlan?: string;
  assignedTo?: string;
}
