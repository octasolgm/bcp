import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom, timeout, catchError, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { getNdAccessToken } from './nd-supabase-client';

const API_TIMEOUT_MS = 25_000;
/** Status poll for large runs (~140 points) — allow slow DB without canceling mid-response. */
const ANALYSIS_STATUS_TIMEOUT_MS = 90_000;
/** Full run detail (points + snapshots) for resume — larger than status, still no PDF enrichment. */
const ANALYSIS_RUN_DETAIL_TIMEOUT_MS = 90_000;
/** Login / profile — allow slow first hit when DB pool or Supabase Auth API is busy. */
const AUTH_API_TIMEOUT_MS = 60_000;
/** Rerun should return quickly after queueing; allow headroom if API is under load. */
const RERUN_API_TIMEOUT_MS = 60_000;
/** Library create/update can persist many regulation points in one request. */
const LIBRARY_WRITE_TIMEOUT_MS = 120_000;

function friendlyNdApiError(raw: string, status?: number): string {
  if (
    raw.includes('EMAXCONNSESSION') ||
    raw.includes('max clients reached in session mode')
  ) {
    return (
      'Database connection limit reached (Supabase session pool is full). ' +
      'Stop extra API instances and browser tabs, wait ~1 minute, restart the API once. ' +
      'For local dev, set Supabase:DbPort to 6543 (transaction pooler) in appsettings.Development.json.'
    );
  }
  if (raw.includes('Npgsql.PostgresException') || raw.length > 320) {
    return status === 500
      ? 'Server error — the API could not reach the database. Restart the API and try again.'
      : raw.slice(0, 280);
  }
  return raw;
}

export type NdApiResult<T> = {
  success: boolean;
  data?: T;
  message?: string;
  status?: number;
  totalMatches?: number;
  requiresConversion?: boolean;
  originalFileName?: string;
  code?: string;
};

export type NdUserProfile = {
  id: string;
  fullName: string;
  role: 'super_admin' | 'maker' | 'checker' | 'reviewer';
  departmentId?: string | null;
  departmentName?: string | null;
  isActive: boolean;
  createdAt?: string;
};

export type NdRunReviewBody = {
  overallComment?: string;
  reviewStatus?: string;
  priority?: number;
  responsibility?: string;
  dueDate?: string;
  pointComments?: { analysisPointId: string; comment: string }[];
  actionItemReviews?: {
    analysisPointId: string;
    actionIndex: number;
    status: string;
    comment?: string;
    responsibility?: string;
    dueDate?: string;
    priority?: string;
  }[];
};

export type NdWorkspaceNavCounts = {
  analysisRunsAll: number;
  analysisRunsCorrection: number;
  analysisRunsInProgress: number;
  internalDocuments: number;
  regulationDocuments: number;
  libraries: number;
  internalDocumentsDeleted: number;
  regulationDocumentsDeleted: number;
  adminUsers: number;
  adminDepartments: number;
  checkerQueue: number;
  reviewerQueue: number;
};

@Injectable({ providedIn: 'root' })
export class NdApiService {
  private readonly http = inject(HttpClient);

  private baseUrl(): string {
    const url = environment.ndApiUrl || environment.apiUrl;
    if (!url) throw new Error('ND API URL is not configured');
    return url.replace(/\/$/, '');
  }

  private async headers(json = true): Promise<HttpHeaders> {
    const token = await getNdAccessToken();
    let h = new HttpHeaders();
    if (token) h = h.set('Authorization', `Bearer ${token}`);
    if (json) h = h.set('Content-Type', 'application/json');
    return h;
  }

  private async request<T>(
    method: string,
    path: string,
    body?: unknown,
    json = true,
    timeoutMs = API_TIMEOUT_MS,
  ): Promise<NdApiResult<T>> {
    const url = `${this.baseUrl()}${path}`;
    const options = { headers: await this.headers(json), body };
    try {
      const obs =
        method === 'GET'
          ? this.http.get<NdApiResult<T>>(url, { headers: options.headers })
          : method === 'POST'
            ? this.http.post<NdApiResult<T>>(url, body, { headers: options.headers })
            : method === 'PUT'
              ? this.http.put<NdApiResult<T>>(url, body, { headers: options.headers })
              : this.http.delete<NdApiResult<T>>(url, { headers: options.headers });
      return await firstValueFrom(
        obs.pipe(
          timeout(timeoutMs),
          catchError((err: unknown) => {
            if (err && typeof err === 'object' && 'name' in err && err.name === 'TimeoutError') {
              return throwError(
                () =>
                  new Error(
                    'Request timed out — is bcp-api running on http://localhost:5100? ' +
                      'Use one API instance, Supabase DbPort 6543, and set Supabase:JwtSecret locally for fast login.',
                  ),
              );
            }
            return throwError(() => err);
          }),
        ),
      );
    } catch (err: unknown) {
      if (err instanceof HttpErrorResponse) {
        const body = err.error as { message?: string } | string | null;
        const raw =
          typeof body === 'string'
            ? body
            : body?.message ?? err.message ?? `Request failed (${err.status})`;
        const message = friendlyNdApiError(raw, err.status);
        return { success: false, message, status: err.status };
      }
      const e = err as { error?: { message?: string }; message?: string };
      return { success: false, message: e.error?.message ?? e.message ?? 'Request failed' };
    }
  }

  getProfile() {
    return this.request<NdUserProfile>('GET', '/nd/auth/me', undefined, true, AUTH_API_TIMEOUT_MS);
  }

  upsertProfile(body: { fullName?: string; role?: string; departmentId?: string }) {
    return this.request<NdUserProfile>('POST', '/nd/auth/profile', body);
  }

  forgotPassword(email: string, redirectTo?: string) {
    return this.request<{ resetLink?: string; tokenHash?: string }>('POST', '/nd/auth/forgot-password', {
      email,
      redirectTo,
    });
  }

  setUserPassword(userId: string, password: string) {
    return this.request<unknown>('POST', `/nd/users/${userId}/set-password`, { password });
  }

  getDepartments() {
    return this.request<unknown[]>('GET', '/nd/departments');
  }

  createDepartment(body: { name: string; description?: string }) {
    return this.request<unknown>('POST', '/nd/departments', body);
  }

  updateDepartment(id: string, body: { name: string; description?: string; isActive?: boolean }) {
    return this.request<unknown>('PUT', `/nd/departments/${id}`, body);
  }

  deleteDepartment(id: string) {
    return this.request<unknown>('DELETE', `/nd/departments/${id}`);
  }

  getUsers() {
    return this.request<unknown[]>('GET', '/nd/users');
  }

  getDualVerifyLlmSettings() {
    return this.request<import('../../../lib/nd/types').DualVerifyLlmSettings>(
      'GET',
      '/nd/admin/settings/dual-verify-llm',
    );
  }

  updateDualVerifyLlmSettings(body: { provider: string; model: string }) {
    return this.request<import('../../../lib/nd/types').DualVerifyLlmSettings>(
      'PUT',
      '/nd/admin/settings/dual-verify-llm',
      body,
    );
  }

  getActiveDualVerifyLlm() {
    return this.request<import('../../../lib/nd/types').ActiveDualVerifyLlm>(
      'GET',
      '/nd/settings/dual-verify-llm',
    );
  }

  updateUser(id: string, body: Record<string, unknown>) {
    return this.request<unknown>('PUT', `/nd/users/${id}`, body);
  }

  deactivateUser(id: string) {
    return this.request<unknown>('POST', `/nd/users/${id}/deactivate`);
  }

  activateUser(id: string) {
    return this.request<unknown>('POST', `/nd/users/${id}/activate`);
  }

  deleteUser(id: string) {
    return this.request<unknown>('DELETE', `/nd/users/${id}`);
  }

  inviteUser(body: { fullName: string; email: string; role: string; password?: string }) {
    return this.request<unknown>('POST', '/nd/users/invite', body);
  }

  getRegulationDocuments(params?: { departmentId?: string; status?: string; hiddenOnly?: boolean }) {
    const q = new URLSearchParams();
    if (params?.departmentId) q.set('departmentId', params.departmentId);
    if (params?.status) q.set('status', params.status);
    if (params?.hiddenOnly) q.set('hiddenOnly', 'true');
    const suffix = q.toString() ? `?${q}` : '';
    return this.request<unknown[]>('GET', `/nd/regulation-documents${suffix}`);
  }

  getRegulationDocument(id: string) {
    return this.request<unknown>('GET', `/nd/regulation-documents/${id}`);
  }

  getRegulationDocumentFileUrl(id: string) {
    return this.request<{ url: string; fileName?: string; expiresIn?: number }>(
      'GET',
      `/nd/regulation-documents/${id}/file-url`,
    );
  }

  updateRegulationDocument(id: string, body: { departmentId?: string | null }) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${id}/department`, body);
  }

  hideRegulationDocument(id: string) {
    return this.request<unknown>('DELETE', `/nd/regulation-documents/${id}`);
  }

  restoreRegulationDocument(id: string) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${id}/restore`);
  }

  async uploadRegulationDocument(file: File, departmentId?: string) {
    const form = new FormData();
    form.append('file', file);
    if (departmentId) form.append('departmentId', departmentId);
    return this.postMultipart<unknown>('/nd/regulation-documents/upload', form);
  }

  extractRegulationDocument(docId: string) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${docId}/extract`);
  }

  stopRegulationExtract(docId: string) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${docId}/extract/stop`);
  }

  refreshRegulationPageReferences(docId: string) {
    return this.request<{ pointsUpdated: number }>(
      'POST',
      `/nd/regulation-documents/${docId}/refresh-page-references`,
    );
  }

  repairRegulationPoints(docId: string) {
    return this.request<{
      regulationDocumentId: string;
      pointCount: number;
      repair: {
        beforeCount: number;
        afterCount: number;
        softDeleted: number;
        duplicateGroups: number;
        junkRemoved: number;
        pagesRefreshed: number;
      };
    }>('POST', `/nd/regulation-documents/${docId}/points/repair`);
  }

  getDocumentPoints(docId: string) {
    return this.request<unknown[]>('GET', `/nd/regulation-documents/${docId}/points`);
  }

  searchRegulationPoints(query: string, limit = 80) {
    const q = encodeURIComponent(query.trim());
    return this.request<unknown[]>(
      'GET',
      `/nd/regulation-documents/points/search?q=${q}&limit=${limit}`,
    );
  }

  createManualRegulationPoint(
    docId: string,
    body: {
      parentPointNumber?: string | null;
      pointNumber?: string;
      pointTitle?: string | null;
      pointContent: string;
      pageReference?: string | null;
    },
  ) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${docId}/manual-points`, body);
  }

  updateManualRegulationPoint(
    docId: string,
    pointId: string,
    body: {
      pointNumber?: string;
      pointTitle?: string | null;
      pointContent?: string;
      pageReference?: string | null;
    },
  ) {
    return this.request<unknown>(
      'PUT',
      `/nd/regulation-documents/${docId}/manual-points/${pointId}`,
      body,
    );
  }

  deleteManualRegulationPoint(docId: string, pointId: string) {
    return this.request<unknown>(
      'DELETE',
      `/nd/regulation-documents/${docId}/manual-points/${pointId}`,
    );
  }

  getInternalDocuments(hiddenOnly = false) {
    const suffix = hiddenOnly ? '?hiddenOnly=true' : '';
    return this.request<unknown[]>('GET', `/nd/internal-documents${suffix}`);
  }

  getInternalDocumentFileUrl(id: string) {
    return this.request<{ url: string; fileName?: string; expiresIn?: number }>(
      'GET',
      `/nd/internal-documents/${id}/file-url`,
    );
  }

  hideInternalDocument(id: string) {
    return this.request<unknown>('DELETE', `/nd/internal-documents/${id}`);
  }

  restoreInternalDocument(id: string) {
    return this.request<unknown>('POST', `/nd/internal-documents/${id}/restore`);
  }

  getInternalDocumentAnalysisRuns(docId: string) {
    return this.request<unknown[]>('GET', `/nd/internal-documents/${docId}/analysis-runs`);
  }

  parseInternalDocument(docId: string) {
    return this.request<{ parseStatus?: string; parsedAt?: string }>(
      'POST',
      `/nd/internal-documents/${docId}/parse`,
    );
  }

  async uploadInternalDocument(file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.postMultipart<unknown>('/nd/internal-documents/upload', form);
  }

  private async postMultipart<T>(path: string, form: FormData): Promise<NdApiResult<T>> {
    const token = await getNdAccessToken();
    const url = `${this.baseUrl()}${path}`;
    try {
      return await firstValueFrom(
        this.http.post<NdApiResult<T>>(url, form, {
          headers: new HttpHeaders({ Authorization: `Bearer ${token ?? ''}` }),
        }).pipe(timeout(API_TIMEOUT_MS)),
      );
    } catch (err) {
      if (err instanceof HttpErrorResponse && err.status === 409) {
        const body = err.error as Record<string, unknown> | null;
        if (body?.['requiresConversion']) {
          return {
            success: false,
            requiresConversion: true,
            message: String(body['message'] ?? 'Convert to PDF and continue?'),
            originalFileName: body['originalFileName'] as string | undefined,
            code: body['code'] as string | undefined,
          };
        }
      }
      throw err;
    }
  }

  getLibraries(departmentId?: string) {
    const q = departmentId ? `?departmentId=${departmentId}` : '';
    return this.request<unknown[]>('GET', `/nd/libraries${q}`);
  }

  getLibrary(id: string) {
    return this.request<unknown>('GET', `/nd/libraries/${id}`);
  }

  createLibrary(body: unknown) {
    return this.request<{ id: string }>('POST', '/nd/libraries', body, true, LIBRARY_WRITE_TIMEOUT_MS);
  }

  updateLibrary(id: string, body: unknown) {
    return this.request<unknown>('PUT', `/nd/libraries/${id}`, body, true, LIBRARY_WRITE_TIMEOUT_MS);
  }

  deleteLibrary(id: string) {
    return this.request<unknown>('DELETE', `/nd/libraries/${id}`);
  }

  getWorkspaceNavCounts() {
    return this.request<NdWorkspaceNavCounts>('GET', '/nd/workspace/nav-counts');
  }

  getAnalysisRuns(params?: {
    status?: string;
    mineOnly?: boolean;
    deletedOnly?: boolean;
    ndOnly?: boolean;
    /** Skip heavy gap/review aggregation (nav badges, dashboard list). */
    summaryOnly?: boolean;
  }) {
    const q = new URLSearchParams();
    if (params?.status) q.set('status', params.status);
    if (params?.mineOnly) q.set('mineOnly', 'true');
    if (params?.deletedOnly) q.set('deletedOnly', 'true');
    if (params?.ndOnly) q.set('ndOnly', 'true');
    if (params?.summaryOnly) q.set('summaryOnly', 'true');
    const suffix = q.toString() ? `?${q}` : '';
    return this.request<unknown[]>('GET', `/nd/analysis-runs${suffix}`);
  }

  createAnalysisRun(body: unknown) {
    return this.request<{ id: string }>('POST', '/nd/analysis-runs', body);
  }

  createDemoAnalysisFromSeed() {
    return this.request<{ id: string; pointCount: number; status: string }>(
      'POST',
      '/nd/analysis-runs/demo-from-seed',
    );
  }

  saveDemoAnalysisRun(body: {
    name?: string;
    selectedPointsSnapshot: unknown[];
    selectedInternalDocIds: string[];
    selectedRegulationDocIds: string[];
    points: Array<{
      pointId: string;
      title?: string | null;
      text?: string | null;
      landingMessage?: string | null;
      llmMessage?: string | null;
      agreementJson?: unknown;
    }>;
  }) {
    return this.request<{ id: string; pointCount: number; status: string; name?: string }>(
      'POST',
      '/nd/analysis-runs/demo-save',
      body,
    );
  }

  getAnalysisRun(id: string) {
    return this.request<unknown>(
      'GET',
      `/nd/analysis-runs/${id}`,
      undefined,
      true,
      ANALYSIS_RUN_DETAIL_TIMEOUT_MS,
    );
  }

  getAnalysisRunStatus(id: string, opts?: { resume?: boolean }) {
    const q = opts?.resume ? '?resume=true' : '';
    return this.request<unknown>(
      'GET',
      `/nd/analysis-runs/${id}/status${q}`,
      undefined,
      true,
      ANALYSIS_STATUS_TIMEOUT_MS,
    );
  }

  getAnalysisRunHistory(id: string) {
    return this.request<unknown>('GET', `/nd/analysis-runs/${id}/history`);
  }

  stopAnalysisRun(id: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${id}/stop`);
  }

  startAnalysisRun(id: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${id}/start`);
  }

  rerunPoint(runId: string, pointId: string, opts?: { evidenceOnly?: boolean; actionIndex?: number }) {
    const params = new URLSearchParams();
    if (opts?.evidenceOnly) params.set('evidenceOnly', 'true');
    if (opts?.actionIndex != null) params.set('actionIndex', String(opts.actionIndex));
    const q = params.toString();
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${runId}/rerun-point/${pointId}${q ? `?${q}` : ''}`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  rerunDualVerify(runId: string, pointId: string, opts?: { evidenceOnly?: boolean; actionIndex?: number }) {
    const params = new URLSearchParams();
    if (opts?.evidenceOnly) params.set('evidenceOnly', 'true');
    if (opts?.actionIndex != null) params.set('actionIndex', String(opts.actionIndex));
    const q = params.toString();
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${runId}/rerun-dual-verify/${pointId}${q ? `?${q}` : ''}`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  rerunAllFailedDualVerify(runId: string) {
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${runId}/rerun-dual-verify/all`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  submitForReview(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/submit-for-review`);
  }

  resubmitForReview(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/resubmit-for-review`);
  }

  softDeleteAnalysisRun(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/soft-delete`);
  }

  restoreAnalysisRun(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/restore`);
  }

  getResults(runId: string) {
    return this.request<unknown>('GET', `/nd/results/${runId}`, undefined, true, ANALYSIS_RUN_DETAIL_TIMEOUT_MS);
  }

  saveActionItemReview(
    runId: string,
    body: {
      analysisPointId: string;
      actionIndex: number;
      status: string;
      comment?: string;
      responsibility?: string;
      dueDate?: string;
      priority?: string;
    },
  ) {
    return this.request<unknown>('POST', `/nd/results/${runId}/action-item-reviews`, body);
  }

  updateActionItemReview(
    runId: string,
    reviewId: string,
    body: {
      status: string;
      comment?: string;
      responsibility?: string;
      dueDate?: string;
      priority?: string;
    },
  ) {
    return this.request<unknown>('PUT', `/nd/results/${runId}/action-item-reviews/${reviewId}`, body);
  }

  reorderActionItemReview(runId: string, reviewId: string, direction: 'up' | 'down') {
    return this.request<unknown>('POST', `/nd/results/${runId}/action-item-reviews/${reviewId}/reorder`, {
      direction,
    });
  }

  deleteActionItemReview(runId: string, reviewId: string) {
    return this.request<unknown>('DELETE', `/nd/results/${runId}/action-item-reviews/${reviewId}`);
  }

  updateActionPlan(runId: string, pointId: string, content: string, revertToVersion?: number) {
    return this.request<unknown>('PUT', `/nd/results/${runId}/action-plan/${pointId}`, {
      content,
      revertToVersion,
    });
  }

  getActionPlanHistory(runId: string, pointId: string) {
    return this.request<unknown[]>('GET', `/nd/results/${runId}/action-plan-history/${pointId}`);
  }

  uploadPointGapAttachments(runId: string, pointId: string, files: File[], actionIndex?: number) {
    return this.uploadMultipart(`/nd/results/${runId}/points/${pointId}/attachments`, files, actionIndex);
  }

  deletePointGapAttachment(runId: string, pointId: string, attachmentId: string) {
    return this.request<unknown>(
      'DELETE',
      `/nd/results/${runId}/points/${pointId}/attachments/${attachmentId}`,
    );
  }

  private async uploadMultipart<T>(path: string, files: File[], actionIndex?: number): Promise<NdApiResult<T>> {
    const url = `${this.baseUrl()}${path}`;
    const form = new FormData();
    for (const file of files) form.append('files', file, file.name);
    if (actionIndex != null) form.append('actionIndex', String(actionIndex));
    const token = await getNdAccessToken();
    let h = new HttpHeaders();
    if (token) h = h.set('Authorization', `Bearer ${token}`);
    try {
      return await firstValueFrom(this.http.post<NdApiResult<T>>(url, form, { headers: h }));
    } catch (err: unknown) {
      if (err instanceof HttpErrorResponse) {
        const body = err.error as { message?: string } | string | null;
        const raw =
          typeof body === 'string'
            ? body
            : body?.message ?? err.message ?? `Request failed (${err.status})`;
        const message = friendlyNdApiError(raw, err.status);
        return { success: false, message, status: err.status };
      }
      const e = err as { message?: string };
      return { success: false, message: e.message ?? 'Upload failed' };
    }
  }

  getCheckerQueue() {
    return this.request<unknown[]>('GET', '/nd/checker/queue');
  }

  getCheckerHistory() {
    return this.request<unknown[]>('GET', '/nd/checker/history');
  }

  approveAnalysis(runId: string, body: NdRunReviewBody) {
    return this.request<unknown>('POST', `/nd/checker/review/${runId}/approve`, body);
  }

  pullBackAnalysis(runId: string, body: NdRunReviewBody) {
    return this.request<unknown>('POST', `/nd/checker/review/${runId}/pull-back`, body);
  }

  getReviewerQueue() {
    return this.request<unknown[]>('GET', '/nd/reviewer/queue');
  }

  getReviewerHistory() {
    return this.request<unknown[]>('GET', '/nd/reviewer/history');
  }

  finalizeAnalysis(runId: string, body: NdRunReviewBody) {
    return this.request<unknown>('POST', `/nd/reviewer/review/${runId}/finalize`, body);
  }

  pullBackToChecker(runId: string, body: NdRunReviewBody) {
    return this.request<unknown>('POST', `/nd/reviewer/review/${runId}/pull-back`, body);
  }

  pullBackToMaker(runId: string, body: NdRunReviewBody) {
    return this.request<unknown>('POST', `/nd/reviewer/review/${runId}/pull-back-to-maker`, body);
  }
}
