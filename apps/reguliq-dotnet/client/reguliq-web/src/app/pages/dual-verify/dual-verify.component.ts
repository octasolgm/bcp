import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, DualVerifyHealth, GovPoint, SessionProgress } from '../../services/api.service';

@Component({
  selector: 'app-dual-verify',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './dual-verify.component.html',
  styleUrl: './dual-verify.component.scss',
})
export class DualVerifyComponent implements OnInit, OnDestroy {
  health: DualVerifyHealth | null = null;
  govPoints: GovPoint[] = [];
  selected = new Set<string>();
  granularity: 'leaf' | 'section' = 'leaf';
  aiModel = 'gemini-2.5-flash-lite';
  forceRefresh = false;
  internalFile: File | null = null;
  sessionId: string | null = null;
  progress: SessionProgress | null = null;
  running = false;
  error = '';
  manualSessionId = '';
  pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly models = [
    'gemini-2.5-flash-lite',
    'gemini-2.5-flash',
    'gemini-3.5-flash',
  ];

  constructor(
    private api: ApiService,
    private route: ActivatedRoute,
  ) {}

  ngOnInit(): void {
    this.route.queryParams.subscribe((p) => {
      if (p['session']) this.manualSessionId = p['session'];
    });
    this.api.getDualVerifyHealth().subscribe((r) => (this.health = r.data));
    this.loadGovPoints();
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  loadGovPoints(): void {
    this.api.getGovPoints().subscribe({
      next: (r) => {
        this.govPoints = this.filterGranularity(r.points ?? []);
        if (this.selected.size === 0) {
          this.govPoints.slice(0, 3).forEach((p) => this.selected.add(p.point_id));
        }
      },
      error: () => (this.error = 'Failed to load gov points'),
    });
  }

  filterGranularity(points: GovPoint[]): GovPoint[] {
    return this.granularity === 'leaf'
      ? points.filter((p) => p.point_id.split('.').length >= 3)
      : points.filter((p) => p.point_id.split('.').length === 2);
  }

  onGranularityChange(): void {
    this.api.getGovPoints().subscribe((r) => {
      this.govPoints = this.filterGranularity(r.points ?? []);
      this.selected.clear();
      this.govPoints.slice(0, 3).forEach((p) => this.selected.add(p.point_id));
    });
  }

  seedBuiltin(): void {
    this.api.seedBuiltin().subscribe(() => this.loadGovPoints());
  }

  toggle(id: string): void {
    if (this.selected.has(id)) this.selected.delete(id);
    else this.selected.add(id);
  }

  onFile(e: Event): void {
    const input = e.target as HTMLInputElement;
    this.internalFile = input.files?.[0] ?? null;
  }

  get persistenceOk(): boolean {
    const m = this.health?.persistence?.mode;
    return m === 'supabase' || m === 'file';
  }

  startPipeline(): void {
    if (!this.persistenceOk) {
      this.error = 'Persistence not ready — configure database connection.';
      return;
    }
    if (!this.internalFile) {
      this.error = 'Attach internal PDF for Phase 2.';
      return;
    }
    const ids = [...this.selected];
    if (!ids.length) {
      this.error = 'Select at least one gov point.';
      return;
    }

    const form = new FormData();
    form.append('pointIds', JSON.stringify(ids));
    form.append('granularity', this.granularity);
    form.append('govDocId', 'gov-tfs-guidelines');
    form.append('internalDocId', 'internal-imptfs');
    form.append('phase2Model', this.aiModel);
    form.append('forceRefresh', String(this.forceRefresh));
    form.append('internalFile', this.internalFile);

    this.running = true;
    this.error = '';
    this.api.startJob(form).subscribe({
      next: (r) => {
        this.sessionId = (r.data as { id: string }).id;
        this.poll(this.sessionId!);
      },
      error: (e) => {
        this.running = false;
        this.error = e?.error?.message ?? 'Start failed';
      },
    });
  }

  poll(id: string): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = setInterval(() => {
      this.api.getJob(id).subscribe({
        next: (r) => {
          this.progress = r.data;
          const st = r.data.session.status;
          if (st === 'completed' || st === 'failed') {
            this.running = false;
            if (this.pollTimer) clearInterval(this.pollTimer);
          }
        },
        error: () => {
          this.running = false;
          if (this.pollTimer) clearInterval(this.pollTimer);
        },
      });
    }, 2500);
  }

  loadSession(): void {
    const id = this.manualSessionId.trim();
    if (!id) return;
    this.api.getJob(id).subscribe({
      next: (r) => {
        this.progress = r.data;
        this.sessionId = id;
      },
      error: () => (this.error = 'Session not found'),
    });
  }

  retryFailed(): void {
    if (!this.sessionId) return;
    this.api.retryFailed(this.sessionId).subscribe(() => {
      this.running = true;
      this.poll(this.sessionId!);
    });
  }

  get pct(): number {
    if (!this.progress?.session.totalPoints) return 0;
    const s = this.progress.session;
    return Math.round(((s.completedPoints + s.failedPoints) / s.totalPoints) * 100);
  }
}
