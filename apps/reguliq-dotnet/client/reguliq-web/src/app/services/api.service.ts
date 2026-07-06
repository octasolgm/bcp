import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface ApiResponse<T> {
  success: boolean;
  data: T;
  message?: string;
}

export type { ComplianceStatusFilter } from '../../lib/dual-verify-workflow';

export interface DashboardMetrics {
  compliant: number;
  partial: number;
  nonCompliant: number;
  totalFindings: number;
  lastAnalysisDate: string;
  complianceBreakdown: { name: string; value: number; color: string }[];
  recentAnalyses: {
    id: string;
    title: string;
    date: string;
    findings: number;
    compliant: number;
    partial: number;
    nonCompliant: number;
  }[];
}

export interface DualVerifyHealth {
  status: string;
  transport: string;
  kafkaConfigured: boolean;
  topics: { jobs: string; retry: string; dlq: string; results: string };
  persistence: {
    dualVerifyTablesReady: boolean;
    complianceSessionsTableReady: boolean;
    fileFallbackReady: boolean;
    fileDataDir: string;
    mode: string;
    hint?: string;
  };
}

export interface DualVerifySessionSummary {
  id: string;
  status: string;
  granularity: string;
  totalPoints: number;
  completedPoints: number;
  failedPoints: number;
  phase2Model: string;
  transport: string;
  updatedAt: string;
  label: string;
}

export interface ComplianceSessionSummary {
  id: string;
  label: string;
  comparedPoints: number;
  granularity?: string;
  source?: string;
  updatedAt?: string;
}

export interface GovPoint {
  point_id: string;
  title?: string;
  text: string;
  section?: string;
}

export interface SessionProgress {
  session: {
    id: string;
    status: string;
    totalPoints: number;
    completedPoints: number;
    failedPoints: number;
    runningPoints: number;
    queuedPoints: number;
    transport: string;
    phase2Model: string;
    granularity?: string;
  };
  points: Array<{
    id: string;
    pointId: string;
    pointTitle?: string;
    status: string;
    landingMessage?: string;
    llmMessage?: string;
    agreementJson?: { status: string; label: string; summary?: string };
    errorMessage?: string;
  }>;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private base = environment.apiUrl;
  /** Optional — load Kafka sessions created via Next.js / Nest */
  private nest = environment.nestjsApiUrl;

  constructor(private http: HttpClient) {}

  getDashboard() {
    return this.http.get<ApiResponse<DashboardMetrics>>(`${this.base}/bcpweb/dashboard`);
  }

  getDualVerifyHealth() {
    return this.http.get<ApiResponse<DualVerifyHealth>>(`${this.base}/dual-verify-kafka/health`);
  }

  listDualVerifySessions() {
    return this.http.get<ApiResponse<DualVerifySessionSummary[]>>(`${this.base}/dual-verify-kafka/sessions`);
  }

  /** Kafka sessions from Nest (runs started via Next.js / Nest) */
  listNestDualVerifySessions() {
    return this.http.get<ApiResponse<DualVerifySessionSummary[]>>(`${this.nest}/dual-verify-kafka/sessions`);
  }

  listComplianceSessions(granularity = 'dual-leaf', limit = 30) {
    return this.http.get<{
      success: boolean;
      sessions: ComplianceSessionSummary[];
      diagnostics?: { hint?: string };
    }>(`${this.base}/landing-ai/compliance-sessions?limit=${limit}&granularity=${granularity}`);
  }

  loadComplianceSession(id: string, granularity = 'dual-leaf') {
    return this.http.get<{ success: boolean; results: unknown[]; message?: string }>(
      `${this.base}/landing-ai/compliance-sessions/${id}?granularity=${granularity}`,
    );
  }

  saveComplianceSession(body: Record<string, unknown>) {
    return this.http.post(`${this.base}/landing-ai/compliance-sessions`, body);
  }

  getGovPoints() {
    return this.http.get<{ points: GovPoint[]; message?: string }>(
      `${this.base}/landing-ai/stored-points?docId=gov-tfs-guidelines`,
    );
  }

  seedBuiltin() {
    return this.http.post(`${this.base}/landing-ai/seed/builtin`, {});
  }

  getNestJob(sessionId: string) {
    return this.http.get<ApiResponse<SessionProgress>>(`${this.nest}/dual-verify-kafka/jobs/${sessionId}`);
  }

  getJob(sessionId: string) {
    return this.http.get<ApiResponse<SessionProgress>>(`${this.base}/dual-verify-kafka/jobs/${sessionId}`);
  }

  startJob(form: FormData) {
    return this.http.post<ApiResponse<{ id: string }>>(`${this.base}/dual-verify-kafka/jobs`, form);
  }

  retryFailed(sessionId: string) {
    return this.http.post(`${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-failed`, {});
  }
}
