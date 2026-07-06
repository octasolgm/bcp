import type {
  BcpwebAnalysisSession,
  BcpwebApiResponse,
  BcpwebBranch,
  BcpwebComplianceItem,
  BcpwebDashboardMetrics,
  BcpwebDocument,
  BcpwebRegulation,
  BcpwebUpdateItemDto,
} from '@/types';

const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:4000';

async function fetchApi<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers,
    },
    cache: 'no-store',
  });
  if (!res.ok) {
    throw new Error(`API error ${res.status}: ${path}`);
  }
  const json = (await res.json()) as BcpwebApiResponse<T>;
  return json.data;
}

export async function getDashboard(): Promise<BcpwebDashboardMetrics> {
  return fetchApi<BcpwebDashboardMetrics>('/bcpweb/dashboard');
}

export async function getBranches(): Promise<BcpwebBranch[]> {
  return fetchApi<BcpwebBranch[]>('/bcpweb/branches');
}

export async function getRegulations(category?: string): Promise<{
  items: BcpwebRegulation[];
  counts: Record<string, number>;
}> {
  const q = category && category !== 'all' ? `?category=${encodeURIComponent(category)}` : '';
  return fetchApi(`/bcpweb/regulations${q}`);
}

export async function getDocuments(category?: string): Promise<BcpwebDocument[]> {
  const q = category && category !== 'all' ? `?category=${encodeURIComponent(category)}` : '';
  return fetchApi<BcpwebDocument[]>(`/bcpweb/documents${q}`);
}

export async function getSession(id: string): Promise<BcpwebAnalysisSession> {
  return fetchApi<BcpwebAnalysisSession>(`/bcpweb/analysis/sessions/${id}`);
}

export async function getSessionItems(id: string): Promise<BcpwebComplianceItem[]> {
  return fetchApi<BcpwebComplianceItem[]>(`/bcpweb/analysis/sessions/${id}/items`);
}

export async function getDemoSession(): Promise<BcpwebAnalysisSession> {
  return fetchApi<BcpwebAnalysisSession>('/bcpweb/analysis/sessions/demo');
}

export async function createAnalysisSession(body: {
  regulationId: string;
  internalFile: File;
  regulationFile: File;
}): Promise<BcpwebAnalysisSession> {
  const form = new FormData();
  form.append('regulationId', body.regulationId);
  form.append('internalFile', body.internalFile);
  form.append('regulationFile', body.regulationFile);

  const res = await fetch(`${API_BASE}/bcpweb/analysis/sessions/upload`, {
    method: 'POST',
    body: form,
    cache: 'no-store',
  });
  if (!res.ok) {
    const err = await res.text();
    throw new Error(`Analysis upload failed: ${err}`);
  }
  const json = (await res.json()) as BcpwebApiResponse<BcpwebAnalysisSession>;
  return json.data;
}

export async function pollSessionProgress(id: string): Promise<BcpwebAnalysisSession> {
  return fetchApi<BcpwebAnalysisSession>(`/bcpweb/analysis/sessions/${id}/progress`);
}

export async function updateComplianceItem(
  sessionId: string,
  itemId: string,
  dto: BcpwebUpdateItemDto,
): Promise<BcpwebComplianceItem> {
  return fetchApi<BcpwebComplianceItem>(
    `/bcpweb/analysis/sessions/${sessionId}/items/${itemId}`,
    { method: 'PATCH', body: JSON.stringify(dto) },
  );
}

export async function signOffItem(
  sessionId: string,
  itemId: string,
): Promise<BcpwebComplianceItem> {
  return fetchApi<BcpwebComplianceItem>(
    `/bcpweb/analysis/sessions/${sessionId}/items/${itemId}/sign-off`,
    { method: 'POST' },
  );
}

export function getExcelExportUrl(sessionId: string): string {
  return `${API_BASE}/bcpweb/excel/export/${sessionId}`;
}

export async function getPdfPage(
  sessionId: string,
  source: 'regulation' | 'policy',
  page: number,
  itemId?: string,
): Promise<{ extractedText: string; totalPages: number; title: string }> {
  const q = new URLSearchParams({
    sessionId,
    source,
    page: String(page),
  });
  if (itemId) q.set('itemId', itemId);
  return fetchApi(`/bcpweb/pdf/page?${q.toString()}`);
}
