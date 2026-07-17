export type UserProfile = {
  id: string;
  fullName: string;
  role: "super_admin" | "maker" | "checker" | "reviewer";
  departmentId?: string | null;
  departmentName?: string | null;
  isActive: boolean;
  createdAt?: string;
};

import { getPublicApiUrl } from "@/lib/config";

export type ApiResult<T> = { success: boolean; data?: T; message?: string };

const baseUrl = () => getPublicApiUrl();

async function apiFetch<T>(
  path: string,
  token: string,
  init?: RequestInit,
): Promise<ApiResult<T>> {
  const res = await fetch(`${baseUrl()}${path}`, {
    ...init,
    headers: {
      Authorization: `Bearer ${token}`,
      ...(init?.body instanceof FormData ? {} : { "Content-Type": "application/json" }),
      ...init?.headers,
    },
  });
  const json = await res.json().catch(() => ({ success: false, message: res.statusText }));
  if (!res.ok) {
    return { success: false, message: json.message ?? res.statusText };
  }
  return json as ApiResult<T>;
}

export async function getProfile(token: string) {
  return apiFetch<UserProfile>("/nd/auth/me", token);
}

export async function upsertProfile(
  token: string,
  body: { fullName?: string; role?: string; departmentId?: string },
) {
  return apiFetch<UserProfile>("/nd/auth/profile", token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function getDepartments(token: string) {
  return apiFetch<unknown[]>("/nd/departments", token);
}

export async function createDepartment(
  token: string,
  body: { name: string; description?: string },
) {
  return apiFetch<unknown>("/nd/departments", token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function updateDepartment(
  token: string,
  id: string,
  body: { name: string; description?: string; isActive?: boolean },
) {
  return apiFetch<unknown>(`/nd/departments/${id}`, token, {
    method: "PUT",
    body: JSON.stringify(body),
  });
}

export async function deleteDepartment(token: string, id: string) {
  return apiFetch<unknown>(`/nd/departments/${id}`, token, { method: "DELETE" });
}

export async function getUsers(token: string) {
  return apiFetch<unknown[]>("/nd/users", token);
}

export async function updateUser(
  token: string,
  id: string,
  body: Record<string, unknown>,
) {
  return apiFetch<unknown>(`/nd/users/${id}`, token, {
    method: "PUT",
    body: JSON.stringify(body),
  });
}

export async function deactivateUser(token: string, id: string) {
  return apiFetch<unknown>(`/nd/users/${id}/deactivate`, token, { method: "POST" });
}

export async function activateUser(token: string, id: string) {
  return apiFetch<unknown>(`/nd/users/${id}/activate`, token, { method: "POST" });
}

export async function inviteUser(
  token: string,
  body: { fullName: string; email: string; role: string; departmentId?: string },
) {
  return apiFetch<unknown>("/nd/users/invite", token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function getRegulationDocuments(
  token: string,
  params?: { departmentId?: string; status?: string },
) {
  const q = new URLSearchParams();
  if (params?.departmentId) q.set("departmentId", params.departmentId);
  if (params?.status) q.set("status", params.status);
  const suffix = q.toString() ? `?${q}` : "";
  return apiFetch<unknown[]>(`/nd/regulation-documents${suffix}`, token);
}

export async function getRegulationDocument(token: string, id: string) {
  return apiFetch<unknown>(`/nd/regulation-documents/${id}`, token);
}

export async function uploadRegulationDocument(
  token: string,
  file: File,
  departmentId?: string,
) {
  const form = new FormData();
  form.append("file", file);
  if (departmentId) form.append("departmentId", departmentId);
  return apiFetch<unknown>("/nd/regulation-documents/upload", token, {
    method: "POST",
    body: form,
  });
}

export async function extractRegulationDocument(token: string, docId: string) {
  return apiFetch<unknown>(`/nd/regulation-documents/${docId}/extract`, token, {
    method: "POST",
  });
}

export async function getDocumentPoints(token: string, docId: string) {
  return apiFetch<unknown[]>(`/nd/regulation-documents/${docId}/points`, token);
}

export async function getInternalDocuments(token: string) {
  return apiFetch<unknown[]>("/nd/internal-documents", token);
}

export async function uploadInternalDocument(token: string, file: File) {
  const form = new FormData();
  form.append("file", file);
  return apiFetch<unknown>("/nd/internal-documents/upload", token, {
    method: "POST",
    body: form,
  });
}

export async function getLibraries(token: string, departmentId?: string) {
  const q = departmentId ? `?departmentId=${departmentId}` : "";
  return apiFetch<unknown[]>(`/nd/libraries${q}`, token);
}

export async function getLibrary(token: string, id: string) {
  return apiFetch<unknown>(`/nd/libraries/${id}`, token);
}

export async function createLibrary(token: string, body: unknown) {
  return apiFetch<{ id: string }>("/nd/libraries", token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function updateLibrary(token: string, id: string, body: unknown) {
  return apiFetch<unknown>(`/nd/libraries/${id}`, token, {
    method: "PUT",
    body: JSON.stringify(body),
  });
}

export async function deleteLibrary(token: string, id: string) {
  return apiFetch<unknown>(`/nd/libraries/${id}`, token, { method: "DELETE" });
}

export async function getAnalysisRuns(
  token: string,
  params?: { status?: string; mineOnly?: boolean; deletedOnly?: boolean },
) {
  const q = new URLSearchParams();
  if (params?.status) q.set("status", params.status);
  if (params?.mineOnly) q.set("mineOnly", "true");
  if (params?.deletedOnly) q.set("deletedOnly", "true");
  const suffix = q.toString() ? `?${q}` : "";
  return apiFetch<unknown[]>(`/nd/analysis-runs${suffix}`, token);
}

export async function createAnalysisRun(token: string, body: unknown) {
  return apiFetch<{ id: string }>("/nd/analysis-runs", token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function getAnalysisRun(token: string, id: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${id}`, token);
}

export async function getAnalysisRunStatus(token: string, id: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${id}/status`, token);
}

export async function startAnalysisRun(token: string, id: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${id}/start`, token, { method: "POST" });
}

export async function rerunPoint(token: string, runId: string, pointId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/rerun-point/${pointId}`, token, {
    method: "POST",
  });
}

export async function rerunDualVerify(token: string, runId: string, pointId: string) {
  return apiFetch<unknown>(
    `/nd/analysis-runs/${runId}/rerun-dual-verify/${pointId}`,
    token,
    { method: "POST" },
  );
}

export async function rerunAllFailedDualVerify(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/rerun-dual-verify/all`, token, {
    method: "POST",
  });
}

export async function submitForReview(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/submit-for-review`, token, {
    method: "POST",
  });
}

export async function resubmitForReview(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/resubmit-for-review`, token, {
    method: "POST",
  });
}

export async function softDeleteAnalysisRun(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/soft-delete`, token, {
    method: "POST",
  });
}

export async function restoreAnalysisRun(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/analysis-runs/${runId}/restore`, token, {
    method: "POST",
  });
}

export async function getResults(token: string, runId: string) {
  return apiFetch<unknown>(`/nd/results/${runId}`, token);
}

export async function updateActionPlan(
  token: string,
  runId: string,
  pointId: string,
  content: string,
  revertToVersion?: number,
) {
  return apiFetch<unknown>(`/nd/results/${runId}/action-plan/${pointId}`, token, {
    method: "PUT",
    body: JSON.stringify({ content, revertToVersion }),
  });
}

export async function getActionPlanHistory(token: string, runId: string, pointId: string) {
  return apiFetch<unknown[]>(`/nd/results/${runId}/action-plan-history/${pointId}`, token);
}

export async function getCheckerQueue(token: string) {
  return apiFetch<unknown[]>("/nd/checker/queue", token);
}

export async function getCheckerHistory(token: string) {
  return apiFetch<unknown[]>("/nd/checker/history", token);
}

export async function approveAnalysis(
  token: string,
  runId: string,
  body: { overallComment?: string; pointComments?: { analysisPointId: string; comment: string }[] },
) {
  return apiFetch<unknown>(`/nd/checker/review/${runId}/approve`, token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function pullBackAnalysis(
  token: string,
  runId: string,
  body: { overallComment?: string; pointComments?: { analysisPointId: string; comment: string }[] },
) {
  return apiFetch<unknown>(`/nd/checker/review/${runId}/pull-back`, token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function getReviewerQueue(token: string) {
  return apiFetch<unknown[]>("/nd/reviewer/queue", token);
}

export async function getReviewerHistory(token: string) {
  return apiFetch<unknown[]>("/nd/reviewer/history", token);
}

export async function finalizeAnalysis(
  token: string,
  runId: string,
  body: { overallComment?: string },
) {
  return apiFetch<unknown>(`/nd/reviewer/review/${runId}/finalize`, token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function pullBackToChecker(
  token: string,
  runId: string,
  body: { overallComment?: string },
) {
  return apiFetch<unknown>(`/nd/reviewer/review/${runId}/pull-back`, token, {
    method: "POST",
    body: JSON.stringify(body),
  });
}
