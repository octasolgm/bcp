import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom, timeout, catchError, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { getNdAccessToken } from './nd-supabase-client';

/** Default ND API calls — Supabase pooler can exceed 25s under load (regul runs, cold start). */
const API_TIMEOUT_MS = 60_000;
/** Document/library catalog lists — first load after API restart can be slow on Supabase. */
const CATALOG_LIST_TIMEOUT_MS = 60_000;
/** Run list / nav badges — multiple DB aggregations while workers are active. */
const ANALYSIS_RUNS_LIST_TIMEOUT_MS = 90_000;
/** Status poll for large runs (~140 points) — allow slow DB without canceling mid-response. */
const ANALYSIS_STATUS_TIMEOUT_MS = 90_000;
/** Full run detail (points + snapshots) for resume — larger than status, still no PDF enrichment. */
const ANALYSIS_RUN_DETAIL_TIMEOUT_MS = 90_000;
/** Login / profile — DB pool can be busy while regul workers run. */
const AUTH_API_TIMEOUT_MS = 30_000;
/** Rerun should return quickly after queueing; allow headroom if API is under load. */
const RERUN_API_TIMEOUT_MS = 60_000;
/** Library create/update can persist many regulation points in one request. */
const LIBRARY_WRITE_TIMEOUT_MS = 120_000;
/** Landing AI policy-clause extract (multi-chunk) — align with Bcp:HttpTimeoutMinutes (15). */
const SECTION_EXTRACT_TIMEOUT_MS = 900_000;
/** Kick off async page repair — job runs in background; poll GET /sections for status. */
const REPAIR_SECTION_PAGES_START_TIMEOUT_MS = 60_000;
/** Poll while background repair runs on large manuals. */
const REPAIR_SECTION_PAGES_POLL_MS = 20 * 60 * 1000;
/** Regulation PDF upload to Supabase — allow headroom for large files on slow links. */
const REGULATION_UPLOAD_TIMEOUT_MS = 180_000;
/** Regulation repair / page refresh can touch hundreds of points. */
const REGULATION_PAGE_REPAIR_TIMEOUT_MS = 300_000;
/** Landing AI PDF/Word parse — large manuals can take several minutes. */
const INTERNAL_PARSE_TIMEOUT_MS = 900_000;
/** Demo workspace clear cascades across runs, points and documents in one request. */
const DEMO_CLEAR_TIMEOUT_MS = 300_000;

/** Base the API-unreachable hint on the environment actually configured, not a hardcoded port. */
function apiUnreachableMessage(): string {
  const base = (environment.ndApiUrl || environment.apiUrl || '').replace(/\/+$/, '');
  const isLocal = /localhost|127\.0\.0\.1/i.test(base);
  const where = base ? ` at ${base}` : '';
  return isLocal
    ? `Cannot reach the API${where}. Start it in PowerShell: cd bcp-api; .\\scripts\\restart-api.ps1 — then refresh this page.`
    : `Cannot reach the API${where}. Check your connection and refresh this page; if it persists the service may be restarting.`;
}

function friendlyNdApiError(raw: string, status?: number): string {
  if (
    status === 0 ||
    raw.includes('Unknown Error') ||
    raw.includes('Http failure response')
  ) {
    return apiUnreachableMessage();
  }
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
  if (
    raw.includes('Npgsql.PostgresException') ||
    (raw.includes('relation') && raw.includes('does not exist')) ||
    raw.length > 320
  ) {
    if (raw.startsWith('Could not load demo templates:')) {
      return raw.length > 420 ? `${raw.slice(0, 420)}…` : raw;
    }
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
  pointCount?: number;
  source?: string;
  returnedCount?: number;
  permanentlyDeleted?: boolean;
  status?: number;
  totalMatches?: number;
  requiresConversion?: boolean;
  originalFileName?: string;
  code?: string;
  pagination?: {
    page: number;
    pageSize: number;
    total: number;
    totalPages: number;
  };
};

export type NdUserProfile = {
  id: string;
  fullName: string;
  email?: string;
  role: 'super_admin' | 'maker' | 'checker' | 'reviewer';
  departmentId?: string | null;
  departmentName?: string | null;
  isActive: boolean;
  createdAt?: string;
  isDemo?: boolean;
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
  deletedAnalysisRuns: number;
  inboxPending: number;
};

export type NdInboxFilter = 'all' | 'pending' | 'resolved' | 'overdue';

export type NdInboxItem = {
  id: string;
  analysisRunId: string;
  analysisPointId: string;
  gapIndex: number;
  runName: string;
  runStatus?: string | null;
  workflowEngine?: string | null;
  clauseNo?: string | null;
  clauseTitle?: string | null;
  actionPlan: string;
  status: string;
  priority: string;
  priorityScore: number;
  targetDate?: string | null;
  overdue: boolean;
  resolvedAt?: string | null;
  updatedAt?: string | null;
  assignedDirectly: boolean;
  assignedVia: string;
  owners: { type: string; label: string }[];
};

export type NdActionPlanInbox = {
  counts: { total: number; pending: number; resolved: number; overdue: number };
  departmentId?: string | null;
  departmentName?: string | null;
  items: NdInboxItem[];
};

export type NdDashboardStats = {
  compliant: number;
  partial: number;
  nonCompliant: number;
  criticalGaps: number;
  mediumGaps: number;
  lowGaps: number;
  totalRuns: number;
  lastAnalysisAt?: string | null;
};

@Injectable({ providedIn: 'root' })
export class NdApiService {
  private readonly http = inject(HttpClient);
  /** Share in-flight GETs so duplicate route loads don't queue duplicate API calls. */
  private readonly inflightGets = new Map<string, Promise<NdApiResult<unknown>>>();

  private baseUrl(): string {
    const url = environment.ndApiUrl || environment.apiUrl;
    if (!url) throw new Error('ND API URL is not configured');
    return url.replace(/\/$/, '');
  }

  private async headers(json = true, accessToken?: string | null): Promise<HttpHeaders> {
    const token = accessToken ?? (await getNdAccessToken());
    let h = new HttpHeaders();
    if (token) h = h.set('Authorization', `Bearer ${token}`);
    if (json) h = h.set('Content-Type', 'application/json');
    return h;
  }

  private requestDedupedGet<T>(path: string, timeoutMs: number): Promise<NdApiResult<T>> {
    const inflight = this.inflightGets.get(path);
    if (inflight) return inflight as Promise<NdApiResult<T>>;

    const promise = this.request<T>('GET', path, undefined, true, timeoutMs).finally(() => {
      this.inflightGets.delete(path);
    });
    this.inflightGets.set(path, promise as Promise<NdApiResult<unknown>>);
    return promise;
  }

  private async request<T>(
    method: string,
    path: string,
    body?: unknown,
    json = true,
    timeoutMs = API_TIMEOUT_MS,
    accessToken?: string | null,
  ): Promise<NdApiResult<T>> {
    const url = `${this.baseUrl()}${path}`;
    const options = { headers: await this.headers(json, accessToken), body };
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
                      'Use one API instance and Supabase DbPort 6543. Wait for API bootstrap to finish, then retry.',
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

  getProfile(accessToken?: string | null) {
    return this.request<NdUserProfile>(
      'GET',
      '/nd/auth/me',
      undefined,
      true,
      AUTH_API_TIMEOUT_MS,
      accessToken,
    );
  }

  upsertProfile(body: { fullName?: string; role?: string; departmentId?: string }) {
    return this.request<NdUserProfile>('POST', '/nd/auth/profile', body, true, AUTH_API_TIMEOUT_MS);
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

  getDemoAdminOverview() {
    return this.request<{
      templates: unknown[];
      note?: string;
    }>('GET', '/nd/admin/demo/overview');
  }

  clearDemoWorkspace(body: {
    clearAll?: boolean;
    clearInternalDocuments?: boolean;
    clearRegulationDocuments?: boolean;
    clearLibraries?: boolean;
    clearAnalysisRuns?: boolean;
    clearUsers?: boolean;
  }) {
    return this.request<{
      internalDocuments: number;
      regulationDocuments: number;
      libraries: number;
      analysisRuns: number;
      usersDeactivated: number;
    }>('POST', '/nd/admin/demo/clear', body, true, DEMO_CLEAR_TIMEOUT_MS);
  }

  getDemoAnalysisTemplate(id: string) {
    return this.request<unknown>('GET', `/nd/admin/demo/templates/${id}`);
  }

  updateDemoAnalysisTemplate(
    id: string,
    body: {
      name?: string;
      description?: string;
      regulationNameHint?: string;
      internalNameHint?: string;
      isActive?: boolean;
      sortOrder?: number;
    },
  ) {
    return this.request<unknown>('PUT', `/nd/admin/demo/templates/${id}`, body);
  }

  addDemoAnalysisTemplatePoint(templateId: string, body: Record<string, unknown>) {
    return this.request<unknown>('POST', `/nd/admin/demo/templates/${templateId}/points`, body);
  }

  updateDemoAnalysisTemplatePoint(
    templateId: string,
    pointId: string,
    body: Record<string, unknown>,
  ) {
    return this.request<unknown>(
      'PUT',
      `/nd/admin/demo/templates/${templateId}/points/${pointId}`,
      body,
    );
  }

  deleteDemoAnalysisTemplatePoint(templateId: string, pointId: string) {
    return this.request<unknown>(
      'DELETE',
      `/nd/admin/demo/templates/${templateId}/points/${pointId}`,
    );
  }

  reloadDemoAnalysisTemplateFromSeed(templateId: string) {
    return this.request<unknown>(
      'POST',
      `/nd/admin/demo/templates/${templateId}/reload-from-seed-file`,
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

  getRegulWorkflowLlmSettings() {
    return this.request<import('../../../lib/nd/types').DualVerifyLlmSettings>(
      'GET',
      '/nd/admin/settings/regul-workflow-llm',
    );
  }

  updateRegulWorkflowLlmSettings(body: { provider: string; model: string }) {
    return this.request<import('../../../lib/nd/types').DualVerifyLlmSettings>(
      'PUT',
      '/nd/admin/settings/regul-workflow-llm',
      body,
    );
  }

  getAnalysisPrompts() {
    return this.request<import('../../../lib/nd/types').AnalysisPromptsCatalog>(
      'GET',
      '/nd/admin/prompts',
    );
  }

  createAnalysisPromptSuggestion(body: { promptKey: string; comment: string }) {
    return this.request<import('../../../lib/nd/types').AnalysisPromptSuggestion>(
      'POST',
      '/nd/admin/prompts/suggestions',
      body,
    );
  }

  updateAnalysisPromptSuggestion(id: string, body: { comment: string }) {
    return this.request<import('../../../lib/nd/types').AnalysisPromptSuggestion>(
      'PUT',
      `/nd/admin/prompts/suggestions/${id}`,
      body,
    );
  }

  deleteAnalysisPromptSuggestion(id: string) {
    return this.request<unknown>('DELETE', `/nd/admin/prompts/suggestions/${id}`);
  }

  createAnalysisPromptVersion(body: {
    promptKey: string;
    promptText: string;
    label?: string;
    appliedSuggestionIds?: string[];
  }) {
    return this.request<import('../../../lib/nd/types').AnalysisPromptVersion>(
      'POST',
      '/nd/admin/prompts/versions',
      body,
    );
  }

  getAnalysisPromptLlmProviders() {
    return this.request<import('../../../lib/nd/types').DualVerifyLlmProviderOption[]>(
      'GET',
      '/nd/admin/prompts/llm-providers',
    );
  }

  generateAnalysisPromptVersion(body: {
    promptKey: string;
    suggestionIds: string[];
    provider: string;
    model?: string;
    instruction?: string;
  }) {
    return this.request<import('../../../lib/nd/types').AnalysisPromptGenerateResult>(
      'POST',
      '/nd/admin/prompts/generate',
      body,
    );
  }

  setCurrentAnalysisPromptVersion(versionId: string) {
    return this.request<import('../../../lib/nd/types').AnalysisPromptVersion>(
      'POST',
      `/nd/admin/prompts/versions/${versionId}/set-current`,
    );
  }

  getActiveRegulWorkflowLlm() {
    return this.request<import('../../../lib/nd/types').ActiveDualVerifyLlm>(
      'GET',
      '/nd/settings/regul-workflow-llm',
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

  inviteUser(body: {
    fullName: string;
    email: string;
    role: string;
    password?: string;
    departmentId?: string | null;
  }) {
    return this.request<unknown>('POST', '/nd/users/invite', body);
  }

  getRegulationDocuments(params?: { departmentId?: string; status?: string; hiddenOnly?: boolean }) {
    const q = new URLSearchParams();
    if (params?.departmentId) q.set('departmentId', params.departmentId);
    if (params?.status) q.set('status', params.status);
    if (params?.hiddenOnly) q.set('hiddenOnly', 'true');
    const suffix = q.toString() ? `?${q}` : '';
    return this.request<unknown[]>('GET', `/nd/regulation-documents${suffix}`, undefined, true, CATALOG_LIST_TIMEOUT_MS);
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
    return this.postMultipart<unknown>('/nd/regulation-documents/upload', form, REGULATION_UPLOAD_TIMEOUT_MS);
  }

  parseRegulationDocument(docId: string) {
    return this.request<unknown>('POST', `/nd/regulation-documents/${docId}/parse`);
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
      undefined,
      true,
      REGULATION_PAGE_REPAIR_TIMEOUT_MS,
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
    }>('POST', `/nd/regulation-documents/${docId}/points/repair`, undefined, true, REGULATION_PAGE_REPAIR_TIMEOUT_MS);
  }

  getDocumentPoints(docId: string, opts?: { lite?: boolean; demoScope?: boolean }) {
    const params = new URLSearchParams();
    if (opts?.lite) params.set('lite', 'true');
    if (opts?.demoScope) params.set('demoScope', 'true');
    const q = params.toString();
    return this.request<unknown[]>('GET', `/nd/regulation-documents/${docId}/points${q ? `?${q}` : ''}`);
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
    return this.request<unknown[]>('GET', `/nd/internal-documents${suffix}`, undefined, true, CATALOG_LIST_TIMEOUT_MS);
  }

  getInternalDocumentFileUrl(id: string) {
    return this.request<{ url: string; fileName?: string; expiresIn?: number }>(
      'GET',
      `/nd/internal-documents/${id}/file-url`,
    );
  }

  async openRegulationDocumentPdf(docId: string, page?: number | null): Promise<boolean> {
    const res = await this.getRegulationDocumentFileUrl(docId);
    if (!res.success || !res.data?.url) return false;
    this.openSignedPdfUrl(res.data.url, page);
    return true;
  }

  async openInternalDocumentPdf(docId: string, page?: number | null): Promise<boolean> {
    const res = await this.getInternalDocumentFileUrl(docId);
    if (!res.success || !res.data?.url) return false;
    this.openSignedPdfUrl(res.data.url, page);
    return true;
  }

  private openSignedPdfUrl(url: string, page?: number | null): void {
    const pdfPage = page != null && page > 0 ? page : null;
    const full = pdfPage ? `${url}#page=${pdfPage}` : url;
    window.open(full, '_blank', 'noopener');
  }

  private parseDownloadFilename(header: string | null, fallback: string): string {
    if (!header) return fallback;
    const star = /filename\*=UTF-8''([^;]+)/i.exec(header);
    if (star?.[1]) {
      try {
        return decodeURIComponent(star[1].trim());
      } catch {
        return star[1].trim();
      }
    }
    const quoted = /filename="([^"]+)"/i.exec(header);
    if (quoted?.[1]) return quoted[1].trim();
    const plain = /filename=([^;]+)/i.exec(header);
    if (plain?.[1]) return plain[1].trim().replace(/^"|"$/g, '');
    return fallback;
  }

  async downloadNdExport(path: string, fallbackFilename: string): Promise<NdApiResult<void>> {
    const url = `${this.baseUrl()}${path}`;
    try {
      const token = await getNdAccessToken();
      const headers: Record<string, string> = {};
      if (token) headers['Authorization'] = `Bearer ${token}`;
      const res = await fetch(url, { headers });
      if (!res.ok) {
        const raw = await res.text();
        let message = raw;
        try {
          const parsed = JSON.parse(raw) as { message?: string };
          if (parsed.message) message = parsed.message;
        } catch {
          /* keep raw */
        }
        return {
          success: false,
          message: friendlyNdApiError(message, res.status),
          status: res.status,
        };
      }
      const blob = await res.blob();
      const filename = this.parseDownloadFilename(res.headers.get('Content-Disposition'), fallbackFilename);
      const objectUrl = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = objectUrl;
      anchor.download = filename;
      anchor.rel = 'noopener';
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(objectUrl);
      return { success: true };
    } catch (err: unknown) {
      const e = err as { message?: string };
      return { success: false, message: e.message ?? 'Download failed' };
    }
  }

  downloadRegulationPointsExport(docId: string) {
    return this.downloadNdExport(
      `/nd/regulation-documents/${docId}/export/points`,
      'regulation-points.json',
    );
  }

  downloadRegulationFileExport(docId: string) {
    return this.downloadNdExport(`/nd/regulation-documents/${docId}/export/file`, 'regulation.pdf');
  }

  downloadInternalMarkdownExport(docId: string) {
    return this.downloadNdExport(`/nd/internal-documents/${docId}/export/markdown`, 'internal-document.md');
  }

  downloadInternalFileExport(docId: string) {
    return this.downloadNdExport(`/nd/internal-documents/${docId}/export/file`, 'internal-document.pdf');
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
      undefined,
      true,
      INTERNAL_PARSE_TIMEOUT_MS,
    );
  }

  getInternalDocumentSections(docId: string) {
    return this.request<{
      documentId: string;
      sectionExtractStatus?: string;
      sectionExtractError?: string | null;
      sectionExtractProgressLabel?: string | null;
      sectionExtractProgressPct?: number | null;
      sectionPageRepairStatus?: string | null;
      sectionPageRepairProgressLabel?: string | null;
      sectionPageRepairProgressPct?: number | null;
      sectionPageRepairPagesRefreshed?: number | null;
      sectionPageRepairSectionCount?: number | null;
      sectionPageRepairError?: string | null;
      sectionCount?: number;
      sections: Array<{
        id: string;
        sectionRef: string;
        sectionText: string;
        sourcePage?: number | null;
        displayOrder?: number;
      }>;
    }>('GET', `/nd/internal-documents/${docId}/sections`);
  }

  extractInternalDocumentSections(docId: string, force = false) {
    const q = force ? '?force=true' : '';
    return this.request<{
      id: string;
      sectionExtractStatus?: string;
      sectionCount?: number;
      sectionExtractedAt?: string;
      sectionExtractProgressLabel?: string | null;
      sectionExtractProgressPct?: number | null;
      reusedSaved?: boolean;
    }>('POST', `/nd/internal-documents/${docId}/extract-sections${q}`, undefined, true, SECTION_EXTRACT_TIMEOUT_MS);
  }

  repairInternalDocumentSectionPages(docId: string) {
    return this.request<{
      repairStatus?: string;
      sectionCount?: number;
      pagesRefreshed?: number;
    }>('POST', `/nd/internal-documents/${docId}/repair-section-pages`, undefined, true, REPAIR_SECTION_PAGES_START_TIMEOUT_MS);
  }

  async uploadInternalDocument(file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.postMultipart<unknown>('/nd/internal-documents/upload', form);
  }

  private async postMultipart<T>(path: string, form: FormData, timeoutMs = API_TIMEOUT_MS): Promise<NdApiResult<T>> {
    const token = await getNdAccessToken();
    const url = `${this.baseUrl()}${path}`;
    try {
      return await firstValueFrom(
        this.http.post<NdApiResult<T>>(url, form, {
          headers: new HttpHeaders({ Authorization: `Bearer ${token ?? ''}` }),
        }).pipe(timeout(timeoutMs)),
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
    return this.request<unknown[]>('GET', `/nd/libraries${q}`, undefined, true, CATALOG_LIST_TIMEOUT_MS);
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
    return this.request<NdWorkspaceNavCounts>(
      'GET',
      '/nd/workspace/nav-counts',
      undefined,
      true,
      ANALYSIS_RUNS_LIST_TIMEOUT_MS,
    );
  }

  getDashboardStats(params?: { mineOnly?: boolean }) {
    const q = new URLSearchParams();
    if (params?.mineOnly) q.set('mineOnly', 'true');
    const suffix = q.toString() ? `?${q}` : '';
    return this.request<NdDashboardStats>(
      'GET',
      `/nd/workspace/dashboard-stats${suffix}`,
      undefined,
      true,
      ANALYSIS_RUNS_LIST_TIMEOUT_MS,
    );
  }

  getAnalysisRuns(params?: {
    status?: string;
    mineOnly?: boolean;
    deletedOnly?: boolean;
    ndOnly?: boolean;
    /** Skip heavy gap/review aggregation (nav badges, dashboard list). */
    summaryOnly?: boolean;
    /** When set with summaryOnly, returns paginated list + total count. Omit for overview preview (20 recent). */
    page?: number;
    pageSize?: number;
    /** Skip per-point SQL aggregation (overview first paint — gap cards load separately). */
    skipGapStats?: boolean;
  }) {
    const q = new URLSearchParams();
    if (params?.status) q.set('status', params.status);
    if (params?.mineOnly) q.set('mineOnly', 'true');
    if (params?.deletedOnly) q.set('deletedOnly', 'true');
    if (params?.ndOnly) q.set('ndOnly', 'true');
    if (params?.summaryOnly) q.set('summaryOnly', 'true');
    if (params?.page != null) q.set('page', String(params.page));
    if (params?.pageSize != null) q.set('pageSize', String(params.pageSize));
    if (params?.skipGapStats) q.set('skipGapStats', 'true');
    const suffix = q.toString() ? `?${q}` : '';
    return this.request<unknown[]>(
      'GET',
      `/nd/analysis-runs${suffix}`,
      undefined,
      true,
      ANALYSIS_RUNS_LIST_TIMEOUT_MS,
    );
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

  createDemoAnalysisFromCbuaeSeed() {
    return this.request<{ id: string; pointCount: number; status: string; name?: string }>(
      'POST',
      '/nd/analysis-runs/demo-from-cbuae-seed',
    );
  }

  saveDemoRegulAnalysisRun(body: {
    name?: string;
    regulationDocumentId?: string;
    internalDocumentId?: string;
    regulationNameHint?: string;
    internalNameHint?: string;
    judgments?: unknown[];
    useSeedFile?: boolean;
  }) {
    return this.request<{ id: string; pointCount: number; status: string; name?: string; workflowEngine?: string }>(
      'POST',
      '/nd/analysis-runs/demo-save-regul',
      body,
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

  getAnalysisRun(id: string, opts?: { lite?: boolean }) {
    const q = opts?.lite ? '?lite=true' : '';
    return this.requestDedupedGet<unknown>(
      `/nd/analysis-runs/${id}${q}`,
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
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${id}/stop`,
      undefined,
      true,
      30_000,
    );
  }

  startAnalysisRun(id: string) {
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${id}/start`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  startForwardOnlyAnalysis(id: string) {
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${id}/start-forward`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  confirmRegulClauses(
    id: string,
    clauses: Array<{
      analysisPointId: string;
      pointNumber?: string;
      pointTitle?: string;
      pointContent?: string;
    }>,
  ) {
    return this.request<{ regulClausesConfirmedAt?: string }>(
      'POST',
      `/nd/analysis-runs/${id}/confirm-clauses`,
      { clauses },
    );
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

  /** Re-run every open gap against evidence uploaded on the run. */
  rerunRunWithEvidence(runId: string) {
    return this.request<{ updated?: number; queued?: number; demo?: boolean }>(
      'POST',
      `/nd/analysis-runs/${runId}/rerun-with-evidence`,
      undefined,
      true,
      RERUN_API_TIMEOUT_MS,
    );
  }

  rerunForwardOnly(runId: string) {
    return this.request<unknown>(
      'POST',
      `/nd/analysis-runs/${runId}/rerun-forward`,
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
    return this.requestDedupedGet<unknown>(
      `/nd/results/${runId}`,
      ANALYSIS_RUN_DETAIL_TIMEOUT_MS,
    );
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

  // ---------------------------------------------------------- action plans

  getActionPlans(runId: string) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanEntry[]>(
      'GET',
      `/nd/results/${runId}/action-plans`,
    );
  }

  createActionPlan(
    runId: string,
    body: {
      analysisPointId: string;
      gapIndex?: number;
      actionPlan: string;
      status?: string;
      priority?: string;
      priorityScore?: number;
      targetDate?: string | null;
      responsibilityType?: string;
      responsibilityDepartmentId?: string | null;
      responsibilityUserId?: string | null;
      assignees?: import('../../../lib/nd/action-plan').ActionPlanAssignee[];
      comment?: string | null;
    },
  ) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanEntry>(
      'POST',
      `/nd/results/${runId}/action-plans`,
      body,
    );
  }

  updateActionPlanEntry(
    runId: string,
    planId: string,
    body: {
      gapIndex?: number;
      actionPlan?: string;
      status?: string;
      priority?: string;
      priorityScore?: number;
      targetDate?: string | null;
      targetDateChangeReason?: string | null;
      responsibilityType?: string;
      responsibilityDepartmentId?: string | null;
      responsibilityUserId?: string | null;
      assignees?: import('../../../lib/nd/action-plan').ActionPlanAssignee[];
      comment?: string | null;
    },
  ) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanEntry>(
      'PUT',
      `/nd/results/${runId}/action-plans/${planId}`,
      body,
    );
  }

  deleteActionPlan(runId: string, planId: string) {
    return this.request<unknown>('DELETE', `/nd/results/${runId}/action-plans/${planId}`);
  }

  reorderActionPlan(runId: string, planId: string, direction: 'up' | 'down') {
    return this.request<unknown>('POST', `/nd/results/${runId}/action-plans/${planId}/reorder`, {
      direction,
    });
  }

  getActionPlanDateHistory(runId: string, planId: string) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanTargetDateChange[]>(
      'GET',
      `/nd/results/${runId}/action-plans/${planId}/date-history`,
    );
  }

  addActionPlanReview(runId: string, planId: string, comment: string) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanReviewEntry>(
      'POST',
      `/nd/results/${runId}/action-plans/${planId}/reviews`,
      { comment },
    );
  }

  updateActionPlanReview(runId: string, planId: string, reviewId: string, comment: string) {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanReviewEntry>(
      'PUT',
      `/nd/results/${runId}/action-plans/${planId}/reviews/${reviewId}`,
      { comment },
    );
  }

  deleteActionPlanReview(runId: string, planId: string, reviewId: string) {
    return this.request<unknown>(
      'DELETE',
      `/nd/results/${runId}/action-plans/${planId}/reviews/${reviewId}`,
    );
  }

  getActionPlanResponsibilityOptions() {
    return this.request<import('../../../lib/nd/action-plan').ActionPlanResponsibilityOptions>(
      'GET',
      '/nd/action-plans/responsibility-options',
    );
  }

  /** Actions assigned to the signed-in user, directly or through their department. */
  getActionPlanInbox(status?: NdInboxFilter) {
    const query = status && status !== 'all' ? `?status=${status}` : '';
    return this.request<NdActionPlanInbox>('GET', `/nd/action-plans/inbox${query}`);
  }

  getActionPlanSummary() {
    return this.request<{
      total: number;
      pending: number;
      resolved: number;
      overdue: number;
      byPriority: {
        priority: string;
        total: number;
        pending: number;
        resolved: number;
        overdue: number;
        runCount: number;
      }[];
    }>('GET', '/nd/action-plans/summary');
  }

  getActionPlansByPriority(priority: string, status?: string) {
    const query = status ? `?status=${encodeURIComponent(status)}` : '';
    return this.request<{
      priority: string;
      total: number;
      runs: {
        runId: string;
        runName: string;
        runStatus: string;
        workflowEngine: string;
        createdAt: string;
        createdByName?: string | null;
        actionPlanCount: number;
        pendingCount: number;
        resolvedCount: number;
        overdueCount: number;
        gapCount: number;
        nextTargetDate?: string | null;
        pointIds: string[];
      }[];
    }>('GET', `/nd/action-plans/by-priority/${encodeURIComponent(priority)}${query}`);
  }

  addTempPointReviewComment(runId: string, pointId: string, comment: string) {
    return this.request<import('../../../lib/nd/temp-point-review-comment').TempPointReviewComment>(
      'POST',
      `/nd/results/${runId}/points/${pointId}/temp-review-comments`,
      {
        comment,
      },
    );
  }

  updateTempPointReviewComment(runId: string, pointId: string, commentId: string, comment: string) {
    return this.request<import('../../../lib/nd/temp-point-review-comment').TempPointReviewComment>(
      'PUT',
      `/nd/results/${runId}/points/${pointId}/temp-review-comments/${commentId}`,
      { comment },
    );
  }

  deleteTempPointReviewComment(runId: string, pointId: string, commentId: string) {
    return this.request<unknown>(
      'DELETE',
      `/nd/results/${runId}/points/${pointId}/temp-review-comments/${commentId}`,
    );
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

  /** Attach one evidence document to every open gap in the run. */
  uploadRunGapEvidence(runId: string, files: File[]) {
    return this.uploadMultipart<{ storedDocumentId: string; fileName: string }[]>(
      `/nd/results/${runId}/gap-evidence`,
      files,
    );
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
