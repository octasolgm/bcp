import {
  reportItemsToSortedArray,
  type DualVerifyReportItem,
} from './dual-verify-report';

const STORAGE_KEY = 'bcp-app-dual-verify-report';

type StoredReport = {
  sessionId?: string | null;
  complianceSessionId?: string | null;
  items: DualVerifyReportItem[];
  savedAt: string;
};

export function saveReportBagToStorage(
  bag: Map<string, DualVerifyReportItem>,
  meta?: { sessionId?: string | null; complianceSessionId?: string | null },
): void {
  if (typeof window === 'undefined') return;
  try {
    const payload: StoredReport = {
      sessionId: meta?.sessionId ?? null,
      complianceSessionId: meta?.complianceSessionId ?? null,
      items: reportItemsToSortedArray(bag),
      savedAt: new Date().toISOString(),
    };
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
  } catch {
    /* quota / private mode */
  }
}

export function loadReportBagFromStorage(): StoredReport | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as StoredReport;
    if (!parsed?.items?.length) return null;
    return parsed;
  } catch {
    return null;
  }
}

export function clearReportBagStorage(): void {
  if (typeof window === 'undefined') return;
  window.localStorage.removeItem(STORAGE_KEY);
}
