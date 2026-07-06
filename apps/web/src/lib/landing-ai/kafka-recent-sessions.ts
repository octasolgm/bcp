export type KafkaRecentVariant = 'landing-ai' | 'reguliq';

const STORAGE_KEYS: Record<KafkaRecentVariant, string> = {
  'landing-ai': 'bcp-kafka-dual-verify-recent',
  reguliq: 'reguliq-kafka-dual-verify-recent',
};

const MAX_RECENT = 20;

export type RecentKafkaSession = {
  id: string;
  label: string;
  completedPoints: number;
  totalPoints: number;
  savedAt: string;
};

function storageKey(variant: KafkaRecentVariant = 'landing-ai'): string {
  return STORAGE_KEYS[variant];
}

export function readRecentKafkaSessions(
  variant: KafkaRecentVariant = 'landing-ai',
): RecentKafkaSession[] {
  if (typeof window === 'undefined') return [];
  try {
    const raw = window.localStorage.getItem(storageKey(variant));
    if (!raw) return [];
    const parsed = JSON.parse(raw) as RecentKafkaSession[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export function pushRecentKafkaSession(
  session: Omit<RecentKafkaSession, 'savedAt'>,
  variant: KafkaRecentVariant = 'landing-ai',
): void {
  if (typeof window === 'undefined') return;
  const existing = readRecentKafkaSessions(variant).filter(
    (s) => s.id !== session.id,
  );
  const next: RecentKafkaSession[] = [
    { ...session, savedAt: new Date().toISOString() },
    ...existing,
  ].slice(0, MAX_RECENT);
  window.localStorage.setItem(storageKey(variant), JSON.stringify(next));
}

export const KAFKA_SESSION_ID_RE =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function isKafkaSessionId(value: string): boolean {
  return KAFKA_SESSION_ID_RE.test(value.trim());
}
