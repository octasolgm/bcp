import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NdGapPointDetailComponent } from '../../../components/nd/nd-gap-point-detail.component';
import { NdStatusBadgeComponent } from '../../../components/nd/nd-status-badge.component';
import { NdApiService } from '../../../services/nd/nd-api.service';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import { parsePointSnapshot } from '../../../../lib/nd/utils';
import { countCapGapsForAnalysisPoint } from '../../../../lib/nd/cap-gap-count';
import { resolveAnalysisPointSeverity } from '../../../../lib/nd/point-compliance-status';
import type { ActionPlanHistoryEntry, AnalysisPoint, ResultsData } from '../../../../lib/nd/types';

@Component({
  selector: 'app-nd-reviewer-review',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NdStatusBadgeComponent, NdGapPointDetailComponent],
  templateUrl: './nd-reviewer-review.component.html',
  styleUrls: ['../nd-shared.scss'],
})
export class NdReviewerReviewComponent implements OnInit {
  private readonly api = inject(NdApiService);
  private readonly auth = inject(NdAuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  runId = '';
  data: ResultsData | null = null;
  selectedId: string | null = null;
  overallComment = '';
  loading = true;
  submitting = false;
  error = '';
  history: ActionPlanHistoryEntry[] = [];
  historyOpen = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
    const res = await this.api.getResults(this.runId);
    if (res.success && res.data) {
      this.data = res.data as ResultsData;
      if (this.data.points.length > 0) this.selectedId = this.data.points[0].id;
    }
    this.loading = false;
  }

  get selected(): AnalysisPoint | null {
    return this.data?.points.find((p) => p.id === this.selectedId) ?? null;
  }

  selectPoint(id: string): void {
    this.selectedId = id;
    this.historyOpen = false;
    this.history = [];
  }

  async openHistory(): Promise<void> {
    if (!this.selectedId) return;
    const res = await this.api.getActionPlanHistory(this.runId, this.selectedId);
    if (res.success && res.data) {
      this.history = res.data as ActionPlanHistoryEntry[];
      this.historyOpen = true;
    }
  }

  closeHistory(): void {
    this.historyOpen = false;
    this.history = [];
  }

  async toggleHistory(): Promise<void> {
    if (this.historyOpen) this.closeHistory();
    else await this.openHistory();
  }

  showCap(point: AnalysisPoint): boolean {
    return true;
  }

  pointSeverity(point: AnalysisPoint): string {
    return resolveAnalysisPointSeverity(point);
  }

  gapCountForPoint(point: AnalysisPoint): number {
    return countCapGapsForAnalysisPoint(point);
  }

  async handleFinalize(): Promise<void> {
    this.submitting = true;
    this.error = '';
    const res = await this.api.finalizeAnalysis(this.runId, {
      overallComment: this.overallComment.trim() || undefined,
    });
    if (res.success) {
      await this.router.navigate(['/nd/reviewer']);
    } else {
      this.error = res.message ?? 'Failed to finalize';
    }
    this.submitting = false;
  }

  async handlePullBack(): Promise<void> {
    this.submitting = true;
    this.error = '';
    const res = await this.api.pullBackToChecker(this.runId, {
      overallComment: this.overallComment.trim() || undefined,
    });
    if (res.success) {
      await this.router.navigate(['/nd/reviewer']);
    } else {
      this.error = res.message ?? 'Failed to pull back';
    }
    this.submitting = false;
  }

  parsePointSnapshot = parsePointSnapshot;
}
