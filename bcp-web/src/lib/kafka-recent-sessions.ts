export type RecentKafkaSession = {
  id: string;
  label: string;
  completedPoints: number;
  totalPoints: number;
  savedAt: string;
};

const STORAGE_KEY = 'bcp-app-kafka-dual-verify-recent';
const MAX_RECENT = 20;

export function readRecentKafkaSessions(): RecentKafkaSession[] {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as RecentKafkaSession[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export function pushRecentKafkaSession(session: Omit<RecentKafkaSession, 'savedAt'>): void {
  if (typeof window === 'undefined') return;
  const existing = readRecentKafkaSessions().filter((s) => s.id !== session.id);
  const next: RecentKafkaSession[] = [{ ...session, savedAt: new Date().toISOString() }, ...existing].slice(0, MAX_RECENT);
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
}

export const KAFKA_SESSION_ID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function isKafkaSessionId(value: string): boolean {
  return KAFKA_SESSION_ID_RE.test(value.trim());
}
