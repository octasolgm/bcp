import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ApiService, type DualVerifySessionSummary } from './api.service';

const TERMINAL = new Set(['completed', 'failed', 'cancelled', 'archived', 'unavailable']);
const ACTIVE_STATUS = new Set(['queued', 'processing', 'running', 'in_progress', 'in-progress']);
const STALE_QUEUED_MS = 30 * 60 * 1000;
const STALE_RUNNING_MS = 2 * 60 * 60 * 1000;
const STALE_RUNNING_NO_PROGRESS_MS = 15 * 60 * 1000;
/**
 * Background poll while ND shell is open.
 * Keep slow — each call hits Postgres and competes with dashboard list queries
 * under a small Supabase session pool (often MaxPoolSize 8–15).
 */
const SESSION_POLL_MS = 60_000;

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

  // Zombie ND/Regul run: still "running" in DB but no point is active and nothing progressed.
  if (
    (st === 'processing' || st === 'running') &&
    (opts.runningPoints ?? 0) === 0 &&
    total > 0 &&
    done < total &&
    opts.updatedAt
  ) {
    const age = Date.now() - new Date(opts.updatedAt).getTime();
    const staleMs = done === 0 ? STALE_RUNNING_NO_PROGRESS_MS : STALE_RUNNING_MS;
    if (age > staleMs) return false;
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
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private activeSub: Subscription | null = null;
  private watchers = 0;
  private visibilityBound = false;
  private inFlight = false;
  private lastRefreshAt = 0;
  /** Ignore duplicate refresh() bursts (nav + analyse + stop all call this). */
  private readonly minRefreshGapMs = 30_000;

  readonly sessions = signal<DualVerifySessionSummary[]>([]);
  readonly loading = signal(false);

  watch(): void {
    this.watchers++;
    if (this.watchers === 1) {
      this.bindVisibility();
      this.refresh(true);
      this.schedulePoll();
    }
  }

  unwatch(): void {
    this.watchers = Math.max(0, this.watchers - 1);
    if (this.watchers === 0) {
      this.clearPollTimer();
      this.cancelInFlight();
      this.unbindVisibility();
    }
  }

  /** @param force bypass debounce (first watch / visibility return). */
  refresh(force = false): void {
    if (typeof document !== 'undefined' && document.hidden) return;
    if (this.inFlight) return;
    const now = Date.now();
    if (!force && now - this.lastRefreshAt < this.minRefreshGapMs) return;

    this.cancelInFlight();
    this.inFlight = true;
    this.lastRefreshAt = now;
    this.loading.set(true);
    this.activeSub = this.api.listActiveDualVerifySessions().subscribe({
      next: (r) => {
        const rows = Array.isArray(r.data) ? r.data : [];
        const active = rows.filter((s) => s?.id && isActiveAnalysisSession(s));
        active.sort(
          (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
        );
        this.sessions.set(active);
        this.loading.set(false);
        this.inFlight = false;
        this.activeSub = null;
      },
      error: () => {
        this.loading.set(false);
        this.inFlight = false;
        this.activeSub = null;
      },
    });
  }

  ngOnDestroy(): void {
    this.clearPollTimer();
    this.cancelInFlight();
    this.unbindVisibility();
  }

  private schedulePoll(): void {
    this.clearPollTimer();
    if (this.watchers === 0) return;
    this.pollTimer = setTimeout(() => {
      this.refresh();
      this.schedulePoll();
    }, SESSION_POLL_MS);
  }

  private clearPollTimer(): void {
    if (this.pollTimer) {
      clearTimeout(this.pollTimer);
      this.pollTimer = null;
    }
  }

  private cancelInFlight(): void {
    this.activeSub?.unsubscribe();
    this.activeSub = null;
    this.inFlight = false;
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
