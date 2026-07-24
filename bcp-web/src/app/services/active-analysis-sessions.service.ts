import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import { ApiService, type DualVerifySessionSummary } from './api.service';

const TERMINAL = new Set(['completed', 'failed', 'cancelled', 'archived', 'unavailable']);
const ACTIVE_STATUS = new Set(['queued', 'processing', 'running', 'in_progress', 'in-progress']);
const STALE_QUEUED_MS = 30 * 60 * 1000;
const STALE_RUNNING_MS = 2 * 60 * 60 * 1000;
/**
 * Background poll while ND shell is open.
 * Keep slow — each call hits Postgres and competes with /status + analysis processor
 * under a small Supabase session pool (often MaxPoolSize 5–8).
 */
const SESSION_POLL_MS = 45_000;

/** Shared rule for nav badge, in-progress page, and document analysis runs. */
export function isAnalysisStillActive(opts: {
  status: string;
  completedPoints?: number;
  failedPoints?: number;
  totalPoints?: number;
  updatedAt?: string;
  runningPoints?: number;
}): boolean {
  const st = (opts.status || '').toLowerCase();
  if (TERMINAL.has(st)) return false;
  const done = (opts.completedPoints ?? 0) + (opts.failedPoints ?? 0);
  const total = opts.totalPoints ?? 0;
  if (total > 0 && done >= total) return false;

  if (
    st === 'queued' &&
    done === 0 &&
    (opts.runningPoints ?? 0) === 0 &&
    opts.updatedAt
  ) {
    const age = Date.now() - new Date(opts.updatedAt).getTime();
    if (age > STALE_QUEUED_MS) return false;
  }

  if (
    (st === 'processing' || st === 'running') &&
    (opts.runningPoints ?? 0) > 0 &&
    total > 0 &&
    done < total &&
    opts.updatedAt
  ) {
    const age = Date.now() - new Date(opts.updatedAt).getTime();
    if (age > STALE_RUNNING_MS) return false;
  }

  if (ACTIVE_STATUS.has(st)) return true;
  return total > 0 && done < total;
}

export function isActiveAnalysisSession(s: DualVerifySessionSummary): boolean {
  return isAnalysisStillActive({
    status: s.status,
    completedPoints: s.completedPoints,
    failedPoints: s.failedPoints,
    totalPoints: s.totalPoints,
    updatedAt: s.updatedAt,
    runningPoints: s.runningPoints,
  });
}

export function isActiveDocumentRun(run: {
  status: string;
  completedPoints?: number;
  failedPoints?: number;
  pointCount?: number;
  isActive?: boolean;
  updatedAt?: string;
  runningPoints?: number;
}): boolean {
  if (run.isActive != null) return run.isActive;
  return isAnalysisStillActive({
    status: run.status,
    completedPoints: run.completedPoints,
    failedPoints: run.failedPoints,
    totalPoints: run.pointCount,
    updatedAt: run.updatedAt,
    runningPoints: run.runningPoints,
  });
}

/** @deprecated Prefer isActiveDocumentRun — status alone misses finished point counts. */
export function isActiveRunStatus(status: string): boolean {
  return isAnalysisStillActive({ status });
}

/** Polls workspace dual-verify sessions and exposes in-progress runs. */
@Injectable({ providedIn: 'root' })
export class ActiveAnalysisSessionsService implements OnDestroy {
  private readonly api = inject(ApiService);
  private timer: ReturnType<typeof setInterval> | null = null;
  private watchers = 0;
  private visibilityBound = false;
  private inFlight = false;
  private lastRefreshAt = 0;
  /** Ignore duplicate refresh() bursts (nav + analyse + stop all call this). */
  private readonly minRefreshGapMs = 8_000;

  readonly sessions = signal<DualVerifySessionSummary[]>([]);
  readonly loading = signal(false);

  watch(): void {
    this.watchers++;
    if (this.watchers === 1) {
      this.bindVisibility();
      this.refresh(true);
      this.timer = setInterval(() => this.refresh(), SESSION_POLL_MS);
    }
  }

  unwatch(): void {
    this.watchers = Math.max(0, this.watchers - 1);
    if (this.watchers === 0) {
      this.stopTimer();
      this.unbindVisibility();
    }
  }

  /** @param force bypass debounce (first watch / visibility return). */
  refresh(force = false): void {
    if (typeof document !== 'undefined' && document.hidden) return;
    if (this.inFlight) return;
    const now = Date.now();
    if (!force && now - this.lastRefreshAt < this.minRefreshGapMs) return;

    this.inFlight = true;
    this.lastRefreshAt = now;
    this.loading.set(true);
    this.api.listActiveDualVerifySessions().subscribe({
      next: (r) => {
        const rows = Array.isArray(r.data) ? r.data : [];
        const active = rows.filter((s) => s?.id && isActiveAnalysisSession(s));
        active.sort(
          (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
        );
        this.sessions.set(active);
        this.loading.set(false);
        this.inFlight = false;
      },
      error: () => {
        this.loading.set(false);
        this.inFlight = false;
      },
    });
  }

  ngOnDestroy(): void {
    this.stopTimer();
    this.unbindVisibility();
  }

  private stopTimer(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private bindVisibility(): void {
    if (this.visibilityBound || typeof document === 'undefined') return;
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    this.visibilityBound = true;
  }

  private unbindVisibility(): void {
    if (!this.visibilityBound || typeof document === 'undefined') return;
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    this.visibilityBound = false;
  }

  private readonly onVisibilityChange = (): void => {
    if (!document.hidden && this.watchers > 0) this.refresh(true);
  };
}
