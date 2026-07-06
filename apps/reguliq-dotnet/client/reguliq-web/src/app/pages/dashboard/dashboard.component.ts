import { Component, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CommonModule } from '@angular/common';
import { forkJoin } from 'rxjs';
import {
  ApiService,
  ComplianceSessionSummary,
  DashboardMetrics,
  DualVerifyHealth,
  DualVerifySessionSummary,
} from '../../services/api.service';

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

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    forkJoin({
      dash: this.api.getDashboard(),
      health: this.api.getDualVerifyHealth(),
      kafka: this.api.listDualVerifySessions(),
      compliance: this.api.listComplianceSessions('dual-leaf', 10),
    }).subscribe({
      next: ({ dash, health, kafka, compliance }) => {
        this.seed = dash.data;
        this.health = health.data;
        const merged = [...(kafka.data ?? [])];
        for (const c of compliance.sessions ?? []) {
          if (!merged.some((s) => s.id === c.id)) {
            merged.push({
              id: c.id,
              status: 'saved',
              granularity: c.granularity ?? 'dual-leaf',
              totalPoints: c.comparedPoints,
              completedPoints: c.comparedPoints,
              failedPoints: 0,
              phase2Model: 'saved',
              transport: 'db',
              updatedAt: c.updatedAt ?? c.label,
              label: c.label,
            });
          }
        }
        this.sessions = merged;
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
    return this.sessions.filter((s) => s.status === 'running' || s.status === 'queued' || s.status === 'processing').length;
  }
}
