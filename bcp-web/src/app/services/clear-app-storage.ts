/**
 * Clears stale / demo browser caches used by Reguliq.
 * Keeps theme + workspace preference.
 *
 * Bump CLEAR_VERSION when you need another one-shot wipe of local caches.
 */
export const REGULIQ_STORAGE_CLEAR_VERSION = '2026-07-08';

export const REGULIQ_EPHEMERAL_STORAGE_KEYS = [
  'reguliq-gap-report',
  'reguliq-gap-drafts',
  'reguliq-documents',
  'bcp-app-dual-verify-report',
  'bcp-app-kafka-dual-verify-recent',
] as const;

const KEEP_KEYS = new Set(['reguliq-theme', 'reguliq-workspace']);
const MARKER_KEY = 'reguliq-storage-cleared';

export function clearEphemeralAppStorage(): string[] {
  if (typeof window === 'undefined') return [];
  const removed: string[] = [];

  for (const key of REGULIQ_EPHEMERAL_STORAGE_KEYS) {
    if (localStorage.getItem(key) != null) {
      localStorage.removeItem(key);
      removed.push(key);
    }
  }

  const toRemove: string[] = [];
  for (let i = 0; i < localStorage.length; i++) {
    const k = localStorage.key(i);
    if (!k) continue;
    if (KEEP_KEYS.has(k) || k === MARKER_KEY) continue;
    if (k.startsWith('reguliq-') || k.startsWith('bcp-app-')) {
      toRemove.push(k);
    }
  }
  for (const k of toRemove) {
    localStorage.removeItem(k);
    if (!removed.includes(k)) removed.push(k);
  }

  try {
    sessionStorage.clear();
  } catch {
    /* ignore */
  }

  return removed;
}

/** Run once per CLEAR_VERSION so old demo/local caches are dropped. */
export function clearEphemeralAppStorageOnce(): string[] {
  if (typeof window === 'undefined') return [];
  try {
    if (localStorage.getItem(MARKER_KEY) === REGULIQ_STORAGE_CLEAR_VERSION) {
      return [];
    }
    const removed = clearEphemeralAppStorage();
    localStorage.setItem(MARKER_KEY, REGULIQ_STORAGE_CLEAR_VERSION);
    return removed;
  } catch {
    return [];
  }
}
