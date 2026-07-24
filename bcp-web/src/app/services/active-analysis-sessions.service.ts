import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import { ApiService, type DualVerifySessionSummary } from './api.service';

const TERMINAL = new Set(['completed', 'failed', 'cancelled', 'archived', 'unavailable']);
const ACTIVE_STATUS = new Set(['queued', 'processing', 'running', 'in_progress', 'in-progress']);
const STALE_QUEUED_MS = 30 * 60 * 1000;
const STALE_RUNNING_MS = 2 * 60 * 60 * 1000;

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

  readonly sessions = signal<DualVerifySessionSummary[]>([]);
  readonly loading = signal(false);

  watch(): void {
    this.watchers++;
    if (this.watchers === 1) {
      this.refresh();
      this.timer = setInterval(() => this.refresh(), 10000);
    }
  }

  unwatch(): void {
    this.watchers = Math.max(0, this.watchers - 1);
    if (this.watchers === 0) this.stopTimer();
  }

  refresh(): void {
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
      },
      error: () => this.loading.set(false),
    });
  }

  ngOnDestroy(): void {
    this.stopTimer();
  }

  private stopTimer(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
