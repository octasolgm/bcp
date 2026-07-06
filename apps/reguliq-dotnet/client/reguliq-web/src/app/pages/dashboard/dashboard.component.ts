import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { forkJoin } from 'rxjs';
import { ApiService, DashboardMetrics, DualVerifyHealth, DualVerifySessionSummary } from '../../services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit {
  seed: DashboardMetrics | null = null;
  health: DualVerifyHealth | null = null;
  sessions: DualVerifySessionSummary[] = [];
  loading = true;

  ngOnInit(): void {
    forkJoin({
      dash: this.api.getDashboard(),
      health: this.api.getDualVerifyHealth(),
      sessions: this.api.listDualVerifySessions(),
    }).subscribe({
      next: ({ dash, health, sessions }) => {
        this.seed = dash.data;
        this.health = health.data;
        this.sessions = sessions.data ?? [];
        this.loading = false;
      },
      error: () => (this.loading = false),
    });
  }

  get persistenceMode(): string {
    return this.health?.persistence?.mode ?? 'memory';
  }

  get completedPoints(): number {
    return this.sessions.reduce((n, s) => n + s.completedPoints, 0);
  }

  get activeJobs(): number {
    return this.sessions.filter((s) => s.status === 'running' || s.status === 'queued').length;
  }

  constructor(private api: ApiService) {}
}
