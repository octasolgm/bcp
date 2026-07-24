import { Component, Input, OnDestroy, effect, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';
import { ApiService, type DualVerifySessionSummary, type SessionProgress } from '../../services/api.service';
import { ToastService } from '../../services/toast.service';
import { shellRoute } from '../../services/app-route-prefix';

type PointRow = SessionProgress['points'][number];

@Component({
  selector: 'app-active-sessions-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './active-sessions-panel.component.html',
  styleUrl: './active-sessions-panel.component.scss',
})
export class ActiveSessionsPanelComponent implements OnDestroy {
  @Input() resumePath: string | null = null;
  @Input() currentSessionId: string | null = null;
  @Input() compact = false;
  @Input() mode: 'inline' | 'page' = 'inline';
  /** When false, hide the page empty-state (e.g. parent already lists ND runs). */
  @Input() showEmpty = true;

  private readonly sessionsService = inject(ActiveAnalysisSessionsService);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  stoppingSessionId: string | null = null;
  expandedSessionId: string | null = null;
  sessionPoints: Record<string, PointRow[]> = {};
  loadingSessionId: string | null = null;
  /** Per-session point history panel; omitted = expanded */
  pointHistoryOpen: Record<string, boolean> = {};

  readonly sessions = this.sessionsService.sessions;
  readonly loading = this.sessionsService.loading;

  private readonly pageDetailsEffect = effect(() => {
    if (this.mode !== 'page') return;
    const list = this.sessions();
    for (const s of list) {
      this.ensureSessionPoints(s.id);
    }
    const ids = new Set(list.map((s) => s.id));
    for (const id of Object.keys(this.sessionPoints)) {
      if (!ids.has(id)) delete this.sessionPoints[id];
    }
    for (const id of Object.keys(this.pointHistoryOpen)) {
      if (!ids.has(id)) delete this.pointHistoryOpen[id];
    }
  });

  ngOnDestroy(): void {
    // effect cleanup is automatic
  }

  sessionTitle(s: DualVerifySessionSummary): string {
    const reg = this.sessionRegName(s);
    const internal = this.sessionIntName(s);
    if (reg) return internal ? `${reg} × ${internal}` : reg;
    if (s.label && !/^\s*leaf\b/i.test(s.label)) return s.label;
    return 'Gap analysis';
  }

  sessionRegName(s: DualVerifySessionSummary): string {
    return (s.regulationFileName || s.govFileName || '').trim();
  }

  sessionIntName(s: DualVerifySessionSummary): string {
    return (s.internalFileName || '').trim();
  }

  sessionFilePair(s: DualVerifySessionSummary): { reg: string; internal: string } | null {
    const reg = this.sessionRegName(s);
    if (!reg) return null;
    return { reg, internal: this.sessionIntName(s) || 'compliance' };
  }

  statusLabel(s: DualVerifySessionSummary): string {
    const st = (s.status || '').toLowerCase();
    if (st === 'processing' || st === 'running' || st === 'queued') return 'In progress';
    if (st === 'in_progress' || st === 'in-progress') return 'In progress';
    return s.status || '—';
  }

  progressLabel(s: DualVerifySessionSummary): string {
    const done = (s.completedPoints ?? 0) + (s.failedPoints ?? 0);
    const total = s.totalPoints ?? 0;
    const running = s.runningPoints ?? 0;
    if (total <= 0) return this.statusLabel(s);
    const parts = [`${done}/${total} pts`];
    if (running > 0) parts.push(`${running} running`);
    if ((s.failedPoints ?? 0) > 0) parts.push(`${s.failedPoints} failed`);
    return parts.join(' · ');
  }

  progressPct(s: DualVerifySessionSummary): number {
    const done = (s.completedPoints ?? 0) + (s.failedPoints ?? 0);
    const total = s.totalPoints ?? 0;
    if (total <= 0) return 8;
    return Math.max(8, Math.min(100, Math.round((done / total) * 100)));
  }

  toggleDetails(s: DualVerifySessionSummary, event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    if (this.expandedSessionId === s.id) {
      this.expandedSessionId = null;
      return;
    }
    this.expandedSessionId = s.id;
    this.ensureSessionPoints(s.id);
  }

  isExpanded(s: DualVerifySessionSummary): boolean {
    return this.mode === 'page' || this.expandedSessionId === s.id;
  }

  isPointHistoryOpen(sessionId: string): boolean {
    return this.pointHistoryOpen[sessionId] !== false;
  }

  togglePointHistory(sessionId: string, event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    const open = this.isPointHistoryOpen(sessionId);
    this.pointHistoryOpen = { ...this.pointHistoryOpen, [sessionId]: !open };
  }

  openSession(s: DualVerifySessionSummary): void {
    const path = this.resumePath ?? shellRoute(this.router, '/analyse-v8');
    this.router.navigate([path], { queryParams: { session: s.id } });
  }

  stopSession(s: DualVerifySessionSummary, event: Event): void {
    event.stopPropagation();
    event.preventDefault();
    const label = this.sessionTitle(s);
    const ok = window.confirm(
      `Stop "${label}"?\n\nQueued points will be cancelled. A point already being analysed may finish its current pass first.`,
    );
    if (!ok) return;

    this.stoppingSessionId = s.id;
    this.api.cancelSession(s.id).subscribe({
      next: () => {
        this.stoppingSessionId = null;
        this.toast.show('Analysis stopped', 'warning', 3500);
        this.sessionsService.refresh();
      },
      error: (e: HttpErrorResponse) => {
        this.stoppingSessionId = null;
        this.toast.show(e.error?.message ?? 'Could not stop analysis', 'error', 4000);
      },
    });
  }

  formatWhen(iso: string): string {
    try {
      const d = new Date(iso);
      const diff = Date.now() - d.getTime();
      if (diff < 60_000) return 'just now';
      if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`;
      if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`;
      return d.toLocaleString(undefined, {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return iso;
    }
  }

  formatPointStatus(status: string): string {
    const st = (status || '').toLowerCase();
    if (st === 'completed') return 'Done';
    if (st === 'failed') return 'Failed';
    if (st === 'running') return 'Running';
    if (st === 'queued') return 'Queued';
    if (st === 'cancelled') return 'Cancelled';
    return status || '—';
  }

  private ensureSessionPoints(sessionId: string): void {
    if (this.sessionPoints[sessionId] || this.loadingSessionId === sessionId) return;
    this.loadingSessionId = sessionId;
    this.api.getJob(sessionId).subscribe({
      next: (r) => {
        this.loadingSessionId = null;
        if (r.success && r.data?.points?.length) {
          this.sessionPoints = {
            ...this.sessionPoints,
            [sessionId]: r.data.points,
          };
        }
      },
      error: () => {
        this.loadingSessionId = null;
      },
    });
  }
}
