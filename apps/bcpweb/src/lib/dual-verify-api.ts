const API_BASE = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:4000';

export type DualVerifyPersistenceMode = 'supabase' | 'file' | 'memory';

export type DualVerifyHealth = {
  transport: 'kafka' | 'local';
  kafkaConfigured: boolean;
  topics: { jobs: string; retry: string; dlq: string; results: string };
  persistence?: {
    dualVerifyTablesReady: boolean;
    complianceSessionsTableReady: boolean;
    fileFallbackReady: boolean;
    fileDataDir: string;
    mode: DualVerifyPersistenceMode;
    hint?: string;
  };
};

export type DualVerifySessionSummary = {
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
};

export type ComplianceSessionSummary = {
  id: string;
  label: string;
  comparedPoints: number;
  granularity?: string;
  source?: string;
};

async function fetchJson<T>(path: string): Promise<T | null> {
  try {
    const res = await fetch(`${API_BASE}${path}`, { cache: 'no-store' });
    if (!res.ok) return null;
    return (await res.json()) as T;
  } catch {
    return null;
  }
}

/** Kafka dual-verify module health */
export async function getDualVerifyHealth(): Promise<DualVerifyHealth | null> {
  const json = await fetchJson<{ data: DualVerifyHealth }>('/dual-verify-kafka/health');
  return json?.data ?? null;
}

/** Recent Kafka dual-verify sessions */
export async function listDualVerifySessions(): Promise<DualVerifySessionSummary[]> {
  const json = await fetchJson<{ data: DualVerifySessionSummary[] }>(
    '/dual-verify-kafka/sessions',
  );
  return json?.data ?? [];
}

/** Saved compliance sessions (dual-leaf / dual-section) */
export async function listComplianceSessions(
  granularity: 'dual-leaf' | 'dual-section' = 'dual-leaf',
  limit = 20,
): Promise<ComplianceSessionSummary[]> {
  const json = await fetchJson<{
    sessions?: ComplianceSessionSummary[];
  }>(`/landing-ai/compliance-sessions?limit=${limit}&granularity=${granularity}`);
  return (json?.sessions ?? []).filter((s) => s.source !== 'compare_cache');
}

export { API_BASE as DUAL_VERIFY_API_BASE };
