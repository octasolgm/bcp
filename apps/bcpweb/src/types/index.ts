/** BCP Web (Reguliq UI) shared types */

export type BcpwebSeverity = 'critical' | 'high' | 'medium' | 'low' | 'compliant';

export type BcpwebEffectiveness = 'Compliant' | 'Partial' | 'Non-Compliant' | 'N/A' | '';

export type BcpwebComplianceStatus =
  | 'Compliant'
  | 'Gap Identified'
  | 'In Progress'
  | 'Closed'
  | '';

export interface BcpwebBranch {
  id: string;
  name: string;
  code: string;
}

export interface BcpwebRegulation {
  id: string;
  title: string;
  issuingBody: string;
  type: string;
  version: string;
  lastUpdated: string;
  status: 'Active' | 'Updated';
  clauseCount: number;
  category: string;
  subtitle?: string;
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
  designEffectiveness: BcpwebEffectiveness;
  operatingEffectiveness: BcpwebEffectiveness;
  overallEffectiveness: BcpwebEffectiveness;
  documentReference: string;
  evidenceImplementation: string;
  evidenceReference: string;
  responsibleDepartment: string;
  complianceStatus: BcpwebComplianceStatus;
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

export interface BcpwebApiResponse<T> {
  success: boolean;
  data: T;
  message?: string;
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

export const EFFECTIVENESS_OPTIONS = ['Compliant', 'Partial', 'Non-Compliant', 'N/A'] as const;

export const DEPARTMENT_OPTIONS = [
  'Compliance',
  'Business / Compliance',
  'Risk Management',
  'Internal Audit',
  'Legal',
  'Technology',
] as const;

export const COMPLIANCE_STATUS_OPTIONS = [
  'Compliant',
  'Gap Identified',
  'In Progress',
  'Closed',
] as const;

export const ASSIGNEE_OPTIONS = [
  'Head, Compliance (Compliance)',
  'Senior Officer, Policies & Procedures (Compliance)',
  'Officer, AML and Compliance (Compliance)',
  'Head, UAE/DIFC Branch (Management)',
  'Risk Management Representative (Risk)',
  'Legal & Counseling Representative (Legal)',
  'Internal Audit (Audit)',
  'Sharia Representative (Sharia)',
] as const;

export function severityLabel(severity: BcpwebSeverity): string {
  const map: Record<BcpwebSeverity, string> = {
    critical: 'Critical',
    high: 'High',
    medium: 'Medium',
    low: 'Low',
    compliant: 'Compliant',
  };
  return map[severity];
}

export function severityColor(severity: BcpwebSeverity): string {
  const map: Record<BcpwebSeverity, string> = {
    critical: 'bg-red-500/20 text-red-400 border-red-500/30',
    high: 'bg-orange-500/20 text-orange-400 border-orange-500/30',
    medium: 'bg-yellow-500/20 text-yellow-400 border-yellow-500/30',
    low: 'bg-green-500/20 text-green-400 border-green-500/30',
    compliant: 'bg-blue-500/20 text-blue-400 border-blue-500/30',
  };
  return map[severity];
}
