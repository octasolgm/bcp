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
  runningPoints?: number;
  phase2Model: string;
  transport: string;
  updatedAt: string;
  label: string;
  regulationFileName?: string | null;
  internalFileName?: string | null;
  govFileName?: string | null;
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
    govFileName?: string | null;
    internalFileName?: string | null;
    govFileHash?: string | null;
    internalFileHash?: string | null;
    regulationDocumentId?: string | null;
    internalDocumentId?: string | null;
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

  listActiveDualVerifySessions() {
    return this.http.get<ApiResponse<DualVerifySessionSummary[]>>(
      `${this.base}/dual-verify-kafka/sessions/active`,
    );
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
      points?: GovPoint[];
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
    return this.http.post<ApiResponse<{ id: string; analysisRunId?: string }>>(
      `${this.base}/dual-verify-kafka/jobs`,
      form,
    );
  }

  /** Analysis runs linked to a stored document (compliance or regulation). */
  listDocumentAnalysisRuns(documentId: string) {
    return this.http.get<{
      success: boolean;
      documentId: string;
      count: number;
      runs: DocumentAnalysisRunDto[];
      message?: string;
    }>(`${this.base}/documents/${documentId}/analysis-runs`);
  }

  deleteDocumentAnalysisRun(documentId: string, runId: string) {
    return this.http.delete<{ success: boolean; deleted?: boolean; message?: string }>(
      `${this.base}/documents/${documentId}/analysis-runs/${runId}`,
    );
  }

  retryFailed(sessionId: string, internalFile?: File | null) {
    if (internalFile) {
      const form = new FormData();
      form.append('internalFile', internalFile);
      return this.http.post<ApiResponse<{ requeued: number }>>(
        `${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-failed`,
        form,
      );
    }
    return this.http.post<ApiResponse<{ requeued: number }>>(
      `${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-failed`,
      {},
    );
  }

  /**
   * Re-run specific points on an existing session, or append not-yet-run points.
   * Form: pointIds (JSON), optional govPointsJson, forceRefresh, internalFile.
   */
  retryPoints(sessionId: string, form: FormData) {
    return this.http.post<ApiResponse<{ requeued: number }>>(
      `${this.base}/dual-verify-kafka/jobs/${sessionId}/retry-points`,
      form,
    );
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

  getDocumentsStorageHealth() {
    return this.http.get<{
      success: boolean;
      storageConfigured: boolean;
      bucket: string;
      hint?: string;
    }>(`${this.base}/documents/health`);
  }

  listStoredDocuments(kind?: string, workspaceId?: string) {
    const qs = new URLSearchParams();
    if (kind) qs.set('kind', kind);
    if (workspaceId) qs.set('workspaceId', workspaceId);
    const q = qs.toString();
    return this.http.get<ApiResponse<StoredDocumentDto[]>>(
      `${this.base}/documents${q ? `?${q}` : ''}`,
    );
  }

  uploadDocument(form: FormData) {
    return this.http.post<
      | ApiResponse<StoredDocumentDto>
      | {
          success: false;
          duplicate: true;
          message: string;
          existing: StoredDocumentDto;
          nextVersion: string;
        }
    >(`${this.base}/documents/upload`, form);
  }

  /** Seed TFS Guidelines.pdf into Storage + link existing extract cache by file hash. */
  seedTfsGuidelines(form?: FormData) {
    return this.http.post<{
      success: boolean;
      message?: string;
      documentId?: string;
      fileHash?: string;
      pointCount?: number;
      uploadedToStorage?: boolean;
      storageConfigured?: boolean;
      storagePath?: string;
      sourcePdfPath?: string;
      document?: StoredDocumentDto;
    }>(`${this.base}/documents/seed-tfs-guidelines`, form ?? new FormData());
  }

  /** Upload regulation to Storage + Landing extract (DB cache by file hash). */
  uploadRegulation(form: FormData) {
    return this.http.post<{
      success: boolean;
      message?: string;
      document?: StoredDocumentDto;
      cached?: boolean;
      fileHash?: string;
      pointCount?: number;
      points?: GovPoint[];
      source?: string;
      duplicate?: boolean;
      existing?: StoredDocumentDto;
      nextVersion?: string;
    }>(`${this.base}/documents/upload-regulation`, form);
  }

  /** Load gov points for a stored regulation (DB cache first, else extract from Storage). */
  loadDocumentPoints(id: string) {
    return this.http.post<{
      success: boolean;
      source?: string;
      cached?: boolean;
      fileHash?: string;
      pointCount?: number;
      document?: StoredDocumentDto;
      points?: GovPoint[];
      message?: string;
    }>(`${this.base}/documents/${id}/load-points`, {});
  }

  getDocumentSignedUrl(id: string) {
    return this.http.get<{ success: boolean; url: string; expiresIn: number; path: string }>(
      `${this.base}/documents/${id}/signed-url`,
    );
  }
}

export interface StoredDocumentDto {
  id: string;
  title: string;
  category: string;
  pages: number;
  uploaded: string;
  version: string;
  status: string;
  gapCount?: number | null;
  filter: string;
  fileType: string;
  docKind: string;
  storagePath: string;
  history: string[];
  originalFileName: string;
  sizeBytes: number;
  fileHash?: string | null;
  pointCount?: number | null;
  activeAnalysisCount?: number;
  /** ND regulation library manual entry (not a stored PDF). */
  isNdManual?: boolean;
  /** Legacy stored document id when this row comes from the ND catalog. */
  ndStoredDocumentId?: string | null;
  extractionStatus?: string | null;
  parseStatus?: string | null;
  sectionExtractStatus?: string | null;
  sectionCount?: number | null;
  analysisRunCount?: number | null;
}

export interface DocumentAnalysisRunDto {
  id: string;
  dualVerifySessionId?: string | null;
  complianceSessionId?: string | null;
  label: string;
  regulationFileName?: string | null;
  internalFileName?: string | null;
  status: string;
  pointCount: number;
  completedPoints: number;
  failedPoints?: number;
  runningPoints?: number;
  isActive?: boolean;
  sessionAvailable?: boolean;
  granularity: string;
  createdAt: string;
  updatedAt: string;
  openUrl: string;
}
