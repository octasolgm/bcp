/**
 * Per-viewer "seen" set for inbox messages, kept in localStorage. A key re-appearing after
 * being marked read (e.g. a report pulled back again after being resubmitted) reads as
 * unread again, since callers key by id + status.
 */
const STORAGE_KEY = 'nd-inbox-read-keys';
const MAX_KEYS = 500;

export class InboxReadTracker {
  private seen: Set<string>;

  constructor() {
    this.seen = InboxReadTracker.load();
  }

  private static load(): Set<string> {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return new Set();
      const arr = JSON.parse(raw);
      return Array.isArray(arr) ? new Set(arr) : new Set();
    } catch {
      return new Set();
    }
  }

  private save(): void {
    try {
      const arr = [...this.seen].slice(-MAX_KEYS);
      localStorage.setItem(STORAGE_KEY, JSON.stringify(arr));
    } catch {
      /* ignore — read tracking is a convenience, not critical state */
    }
  }

  isUnread(key: string): boolean {
    return !this.seen.has(key);
  }

  markRead(key: string): void {
    if (this.seen.has(key)) return;
    this.seen.add(key);
    this.save();
  }
}
