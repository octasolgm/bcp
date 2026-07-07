import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
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
    runningStage?: string;
  }>;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  /** Reguliq .NET API — all primary calls go here (:5100 local, Azure in prod) */
  private base = environment.apiUrl;
  /** Optional NestJS — legacy Kafka sessions only; empty = no :4000 requests */
  private nest = environment.nestjsApiUrl?.trim() ?? '';

  constructor(private http: HttpClient) {}

  /** True when nestjsApiUrl is set (e.g. local Nest for old sessions) */
  get nestJsEnabled(): boolean {
    return this.nest.length > 0;
  }

  getDashboard() {
    return this.http.get<ApiResponse<DashboardMetrics>>(`${this.base}/bcpweb/dashboard`);
  }

  getDualVerifyHealth() {
    return this.http.get<ApiResponse<DualVerifyHealth>>(`${this.base}/dual-verify-kafka/health`);
  }

  listDualVerifySessions() {
    return this.http.get<ApiResponse<DualVerifySessionSummary[]>>(`${this.base}/dual-verify-kafka/sessions`);
  }

  /** Kafka sessions from Nest (optional — skipped when nestjsApiUrl is empty) */
  listNestDualVerifySessions(): Observable<ApiResponse<DualVerifySessionSummary[]>> {
    if (!this.nestJsEnabled) {
      return of({ success: true, data: [] });
    }
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
    return this.http.get<{
      points: GovPoint[];
      pointCount?: number;
      source?: string;
      message?: string;
    }>(`${this.base}/landing-ai/stored-points?docId=gov-tfs-guidelines`);
  }

  loadGovPointsFromDb(docId = 'gov-tfs-guidelines') {
    return this.http.post<{
      success: boolean;
      source: string;
      pointCount: number;
      message?: string;
    }>(`${this.base}/landing-ai/gov-points/load-from-db?docId=${docId}`, {});
  }

  extractGovPoints(form: FormData) {
    return this.http.post<{
      success: boolean;
      cached?: boolean;
      pointCount: number;
      points: GovPoint[];
      creditUsage?: number;
      source?: string;
      message?: string;
    }>(`${this.base}/landing-ai/extract-gov-points`, form);
  }

  seedBuiltin() {
    return this.http.post(`${this.base}/landing-ai/seed/builtin`, {});
  }

  getNestJob(sessionId: string): Observable<ApiResponse<SessionProgress>> {
    if (!this.nestJsEnabled) {
      return of({ success: false, data: {} as SessionProgress });
    }
    return this.http.get<ApiResponse<SessionProgress>>(`${this.nest}/dual-verify-kafka/jobs/${sessionId}`);
  }

  getJob(sessionId: string) {
    return this.http.get<ApiResponse<SessionProgress>>(`${this.base}/dual-verify-kafka/jobs/${sessionId}`);
  }

  startJob(form: FormData) {
    return this.http.post<ApiResponse<{ id: string }>>(`${this.base}/dual-verify-kafka/jobs`, form);
  }

  retryFailed(sessionId: string, internalFile?: File | null) {
    if (internalFile) {
      const form = new FormData();
      form.append('internalFile', internalFile);
      return this.http.post(
        `${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-failed`,
        form,
      );
    }
    return this.http.post(`${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-failed`, {});
  }

  cancelSession(sessionId: string) {
    return this.http.post<ApiResponse<{ cancelled: boolean }>>(
      `${this.base}/dual-verify-kafka/jobs/${sessionId}/cancel`,
      {},
    );
  }

  deleteDualVerifySession(sessionId: string) {
    return this.http.delete<ApiResponse<{ deleted: boolean }>>(
      `${this.base}/dual-verify-kafka/jobs/${sessionId}`,
    );
  }

  deleteComplianceSession(id: string) {
    return this.http.delete<{ success: boolean; deleted?: boolean; message?: string }>(
      `${this.base}/landing-ai/compliance-sessions/${id}`,
    );
  }
}
