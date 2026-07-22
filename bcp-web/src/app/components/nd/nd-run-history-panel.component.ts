import { Component, EventEmitter, Input, Output, inject, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NdApiService } from '../../services/nd/nd-api.service';
import { formatDate } from '../../../lib/nd/utils';
import {
  actorInitials,
  runHistoryRoleLabel,
  runHistoryRolePillClass,
  runHistoryTargetLabel,
  type RunHistoryEvent,
  type RunHistoryTimeline,
} from '../../../lib/nd/run-history';
import { analysisRunStatusLabel } from '../../../lib/nd/analysis-run-status';
import { attachmentCountsByPoint } from '../../../lib/nd/nd-review-run-helpers';
import {
  computeRunGapStats,
  type RunGapStatsSummary,
} from '../../../lib/nd/run-gap-stats';
import type { ResultsData } from '../../../lib/nd/types';
import { NdStatusBadgeComponent } from './nd-status-badge.component';

@Component({
  selector: 'app-nd-run-history-panel',
  standalone: true,
  imports: [CommonModule, NdStatusBadgeComponent],
  templateUrl: './nd-run-history-panel.component.html',
  styleUrls: ['./nd-run-history-panel.component.scss', '../../pages/nd/nd-shared.scss'],
})
export class NdRunHistoryPanelComponent implements OnChanges {
  private readonly api = inject(NdApiService);

  @Input() open = false;
  @Input() runId: string | null = null;
  @Input() runName = '';
  /** When opened from a runs table row, use the same gap/review counts as that row. */
  @Input() runStats: RunGapStatsSummary | null = null;
  @Output() closed = new EventEmitter<void>();

  loading = false;
  error = '';
  timeline: RunHistoryTimeline | null = null;
  gapStats: RunGapStatsSummary | null = null;

  readonly formatDate = formatDate;
  readonly roleLabel = runHistoryRoleLabel;
  readonly rolePillClass = runHistoryRolePillClass;
  readonly actorInitials = actorInitials;
  readonly targetLabel = runHistoryTargetLabel;
  readonly statusLabel = analysisRunStatusLabel;

  get displayStats(): RunGapStatsSummary | null {
    return (
      this.gapStats ??
      this.runStats ??
      (this.timeline
        ? {
            totalGaps: this.timeline.totalGaps,
            reviewedActions: this.timeline.reviewedActions ?? 0,
            totalReviews: this.timeline.totalActionReviews,
          }
        : null)
    );
  }

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['open'] || changes['runId']) && this.open && this.runId) {
      void this.load();
    }
    if (changes['open'] && !this.open) {
      this.timeline = null;
      this.gapStats = null;
      this.error = '';
    }
  }

  close(): void {
    this.closed.emit();
  }

  formatWhen(iso: string): string {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  formatDueDate(value: string | null | undefined): string | null {
    if (!value) return null;
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return value;
    return d.toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  eventGapCount(event: RunHistoryEvent): number | null {
    const stats = this.displayStats;
    if (!stats || stats.totalGaps <= 0) return null;
    if (event.kind === 'created') return null;
    if (event.gapCount != null && event.gapCount > 0) return stats.totalGaps;
    return null;
  }

  eventMetaChips(event: RunHistoryEvent): string[] {
    const chips: string[] = [];

    const gaps = this.eventGapCount(event);
    if (gaps != null && gaps > 0) {
      chips.push(`${gaps} gap${gaps === 1 ? '' : 's'}`);
    }

    const progress = this.eventReviewProgress(event);
    if (progress) {
      chips.push(`${progress.reviewed}/${progress.total} reviewed`);
    } else if (event.actionReviewCount != null && event.actionReviewCount > 0) {
      chips.push(`${event.actionReviewCount} review entr${event.actionReviewCount === 1 ? 'y' : 'ies'}`);
    }

    if (event.targetRole) {
      chips.push(this.targetLabel(event.targetRole));
    }

    if (event.reviewStatus) {
      chips.push(event.reviewStatus.replace(/_/g, ' '));
    }

    if (event.priority != null && event.priority >= 0) {
      chips.push(`Priority ${event.priority}`);
    }

    if (event.responsibility?.trim()) {
      chips.push(event.responsibility.trim());
    }

    if (event.pointCount != null && event.pointCount > 0 && event.kind === 'created') {
      chips.push(`${event.pointCount} points`);
    }

    return chips;
  }

  eventReviewProgress(event: RunHistoryEvent): { reviewed: number; total: number } | null {
    const stats = this.displayStats;
    const total = stats?.totalGaps ?? event.gapCount ?? 0;
    if (total <= 0 || event.kind === 'created') return null;

    const reviewed =
      event.reviewedActionsAtEvent ??
      stats?.reviewedActions ??
      0;

    return { reviewed, total };
  }

  eventChipClass(chip: string): string {
    if (/^\d+\s+gap/i.test(chip)) return 'chip-gaps';
    if (/reviewed$/i.test(chip)) return 'chip-reviewed';
    if (/^With /i.test(chip) || chip === 'Complete') return 'chip-stage';
    if (/^Priority /i.test(chip)) return 'chip-priority';
    if (/^Status /i.test(chip) || chip === 'need modify' || chip === 'approve') return 'chip-status';
    return 'chip-default';
  }

  hasActor(event: RunHistoryEvent): boolean {
    return !!(event.actorName?.trim() || event.actorRole?.trim() || event.actorId?.trim());
  }

  actorDisplayName(event: RunHistoryEvent): string {
    const name = event.actorName?.trim();
    if (name && name.toLowerCase() !== event.actorRole?.trim().toLowerCase()) return name;
    if (name) return name;
    if (event.actorRole) return runHistoryRoleLabel(event.actorRole);
    return 'Unknown user';
  }

  eventKindClass(event: RunHistoryEvent): string {
    if (event.kind === 'created') return 'kind-created';
    if (event.reviewAction === 'pulled_back') return 'kind-pullback';
    if (event.reviewAction === 'approved' || event.reviewAction === 'finalized') return 'kind-approved';
    if (event.reviewAction === 'submitted') return 'kind-submitted';
    return 'kind-status';
  }

  eventKindIcon(event: RunHistoryEvent): string {
    if (event.kind === 'created') return '●';
    if (event.reviewAction === 'pulled_back') return '↩';
    if (event.reviewAction === 'approved' || event.reviewAction === 'finalized') return '✓';
    if (event.reviewAction === 'submitted') return '→';
    return '◦';
  }

  private async load(): Promise<void> {
    if (!this.runId) return;
    this.loading = true;
    this.error = '';
    this.gapStats = this.runStats;

    const [historyRes, resultsRes] = await Promise.all([
      this.api.getAnalysisRunHistory(this.runId),
      this.api.getResults(this.runId),
    ]);

    this.loading = false;
    if (historyRes.success && historyRes.data) {
      this.timeline = historyRes.data as RunHistoryTimeline;
    } else {
      this.timeline = null;
      this.error = historyRes.message ?? 'Could not load history';
    }

    if (resultsRes.success && resultsRes.data) {
      const data = resultsRes.data as ResultsData;
      this.gapStats = computeRunGapStats(
        data.points,
        data.actionItemReviews,
        attachmentCountsByPoint(data),
      );
    }
  }
}
