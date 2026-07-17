import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { getNdAccessToken } from './nd-supabase-client';

export type NdApiResult<T> = { success: boolean; data?: T; message?: string; status?: number };

export type NdUserProfile = {
  id: string;
  fullName: string;
  role: 'super_admin' | 'maker' | 'checker' | 'reviewer';
  departmentId?: string | null;
  departmentName?: string | null;
  isActive: boolean;
  createdAt?: string;
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
      return await firstValueFrom(obs);
    } catch (err: unknown) {
      if (err instanceof HttpErrorResponse) {
        const body = err.error as { message?: string } | string | null;
        const message =
          typeof body === 'string'
            ? body
            : body?.message ?? err.message ?? `Request failed (${err.status})`;
        return { success: false, message, status: err.status };
      }
      const e = err as { error?: { message?: string }; message?: string };
      return { success: false, message: e.error?.message ?? e.message ?? 'Request failed' };
    }
  }

  getProfile() {
    return this.request<NdUserProfile>('GET', '/nd/auth/me');
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

  getRegulationDocuments(params?: { departmentId?: string; status?: string }) {
    const q = new URLSearchParams();
    if (params?.departmentId) q.set('departmentId', params.departmentId);
    if (params?.status) q.set('status', params.status);
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

  async uploadRegulationDocument(file: File, departmentId?: string) {
    const form = new FormData();
    form.append('file', file);
    if (departmentId) form.append('departmentId', departmentId);
    const token = await getNdAccessToken();
    const url = `${this.baseUrl()}/nd/regulation-documents/upload`;
    return firstValueFrom(
      this.http.post<NdApiResult<unknown>>(url, form, {
        headers: new HttpHeaders({ Authorization: `Bearer ${token ?? ''}` }),
      }),
    );
  }

  extractRegulationDocument(docId: string) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${docId}/extract`);
  }

  getDocumentPoints(docId: string) {
    return this.request<unknown[]>('GET', `/nd/regulation-documents/${docId}/points`);
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

  getInternalDocuments() {
    return this.request<unknown[]>('GET', '/nd/internal-documents');
  }

  getInternalDocumentAnalysisRuns(docId: string) {
    return this.request<unknown[]>('GET', `/nd/internal-documents/${docId}/analysis-runs`);
  }

  async uploadInternalDocument(file: File) {
    const form = new FormData();
    form.append('file', file);
    const token = await getNdAccessToken();
    const url = `${this.baseUrl()}/nd/internal-documents/upload`;
    return firstValueFrom(
      this.http.post<NdApiResult<unknown>>(url, form, {
        headers: new HttpHeaders({ Authorization: `Bearer ${token ?? ''}` }),
      }),
    );
  }

  getLibraries(departmentId?: string) {
    const q = departmentId ? `?departmentId=${departmentId}` : '';
    return this.request<unknown[]>('GET', `/nd/libraries${q}`);
  }

  getLibrary(id: string) {
    return this.request<unknown>('GET', `/nd/libraries/${id}`);
  }

  createLibrary(body: unknown) {
    return this.request<{ id: string }>('POST', '/nd/libraries', body);
  }

  updateLibrary(id: string, body: unknown) {
    return this.request<unknown>('PUT', `/nd/libraries/${id}`, body);
  }

  deleteLibrary(id: string) {
    return this.request<unknown>('DELETE', `/nd/libraries/${id}`);
  }

  getAnalysisRuns(params?: { status?: string; mineOnly?: boolean }) {
    const q = new URLSearchParams();
    if (params?.status) q.set('status', params.status);
    if (params?.mineOnly) q.set('mineOnly', 'true');
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
    return this.request<unknown>('GET', `/nd/analysis-runs/${id}`);
  }

  getAnalysisRunStatus(id: string) {
    return this.request<unknown>('GET', `/nd/analysis-runs/${id}/status`);
  }

  startAnalysisRun(id: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${id}/start`);
  }

  rerunPoint(runId: string, pointId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/rerun-point/${pointId}`);
  }

  rerunDualVerify(runId: string, pointId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/rerun-dual-verify/${pointId}`);
  }

  rerunAllFailedDualVerify(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/rerun-dual-verify/all`);
  }

  submitForReview(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/submit-for-review`);
  }

  resubmitForReview(runId: string) {
    return this.request<unknown>('POST', `/nd/analysis-runs/${runId}/resubmit-for-review`);
  }

  getResults(runId: string) {
    return this.request<unknown>('GET', `/nd/results/${runId}`);
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

  getCheckerQueue() {
    return this.request<unknown[]>('GET', '/nd/checker/queue');
  }

  getCheckerHistory() {
    return this.request<unknown[]>('GET', '/nd/checker/history');
  }

  approveAnalysis(
    runId: string,
    body: { overallComment?: string; pointComments?: { analysisPointId: string; comment: string }[] },
  ) {
    return this.request<unknown>('POST', `/nd/checker/review/${runId}/approve`, body);
  }

  pullBackAnalysis(
    runId: string,
    body: { overallComment?: string; pointComments?: { analysisPointId: string; comment: string }[] },
  ) {
    return this.request<unknown>('POST', `/nd/checker/review/${runId}/pull-back`, body);
  }

  getReviewerQueue() {
    return this.request<unknown[]>('GET', '/nd/reviewer/queue');
  }

  getReviewerHistory() {
    return this.request<unknown[]>('GET', '/nd/reviewer/history');
  }

  finalizeAnalysis(runId: string, body: { overallComment?: string }) {
    return this.request<unknown>('POST', `/nd/reviewer/review/${runId}/finalize`, body);
  }

  pullBackToChecker(runId: string, body: { overallComment?: string }) {
    return this.request<unknown>('POST', `/nd/reviewer/review/${runId}/pull-back`, body);
  }
}
