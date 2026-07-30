import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import type { AnalysisPoint } from '../../../../lib/nd/types';
import { resolveAnalysisPointSeverity } from '../../../../lib/nd/point-compliance-status';

type RunStatus = {
  id: string;
  status: string;
  totalPointsCount: number;
  processedPointsCount: number;
  landingAiCompletedCount: number;
  dualVerifyCompletedCount: number;
  dualVerifyFailedCount: number;
  points: AnalysisPoint[];
};

type RunDetail = {
  run: {
    id: string;
    name: string;
    status: string;
    selectedPointsSnapshot: string;
    selectedInternalDocIds: string;
    selectedRegulationDocIds: string;
    totalPointsCount: number;
    processedPointsCount: number;
  };
  points: AnalysisPoint[];
};

@Component({
  selector: 'app-nd-run-analysis-progress',
  standalone: true,
  imports: [CommonModule, RouterLink, NdStatusBadgeComponent],
  templateUrl: './nd-run-analysis-progress.component.html',
  styleUrls: ['../nd-shared.scss'],
})
export class NdRunAnalysisProgressComponent implements OnInit, OnDestroy {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);

  runId = '';
  detail: RunDetail | null = null;
  status: RunStatus | null = null;
  error = '';
  resuming = false;
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
    const runRes = await this.api.getAnalysisRun(this.runId);
    if (runRes.success && runRes.data) {
      this.detail = runRes.data as RunDetail;
    } else {
      this.error = runRes.message ?? 'Failed to load analysis run';
    }
    void this.pollStatus();
    this.pollTimer = setInterval(() => void this.pollStatus(), 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private statusInFlight = false;

  async pollStatus(): Promise<void> {
    if (typeof document !== 'undefined' && document.hidden) return;
    if (this.statusInFlight) return;
    this.statusInFlight = true;
    try {
      const res = await this.api.getAnalysisRunStatus(this.runId);
      if (res.success && res.data) this.status = res.data as RunStatus;
    } finally {
      this.statusInFlight = false;
    }
  }

  get currentStatus(): string {
    return this.status?.status ?? this.detail?.run.status ?? 'draft';
  }

  get totalPoints(): number {
    return this.status?.totalPointsCount ?? this.detail?.run.totalPointsCount ?? 0;
  }

  get processedPoints(): number {
    return this.status?.processedPointsCount ?? this.detail?.run.processedPointsCount ?? 0;
  }

  get progress(): number {
    return this.totalPoints > 0 ? Math.round((this.processedPoints / this.totalPoints) * 100) : 0;
  }

  get isDone(): boolean {
    return ['completed', 'dual_verify_failed', 'landing_ai_complete'].includes(this.currentStatus);
  }

  get canResume(): boolean {
    return ['draft', 'running'].includes(this.currentStatus);
  }

  get displayPoints(): AnalysisPoint[] {
    return this.status?.points ?? this.detail?.points ?? [];
  }

  get snapshotCount(): number {
    return this.parseJsonArray(this.detail?.run.selectedPointsSnapshot).length;
  }

  get internalDocCount(): number {
    return this.parseJsonArray(this.detail?.run.selectedInternalDocIds).length;
  }

  get regulationDocCount(): number {
    return this.parseJsonArray(this.detail?.run.selectedRegulationDocIds).length;
  }

  async handleResume(): Promise<void> {
    this.resuming = true;
    this.error = '';
    const res = await this.api.startAnalysisRun(this.runId);
    if (!res.success) this.error = res.message ?? 'Failed to start analysis';
    this.resuming = false;
  }

  parsePointSnapshot = parsePointSnapshot;

  pointComplianceSeverity(point: AnalysisPoint): string {
    return resolveAnalysisPointSeverity(point) ?? '';
  }

  private parseJsonArray(value: string | undefined): unknown[] {
    if (!value) return [];
    try {
      const parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }
}
