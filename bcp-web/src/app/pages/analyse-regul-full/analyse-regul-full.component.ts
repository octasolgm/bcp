import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { InProgressNavButtonComponent } from '../../components/in-progress-nav-button/in-progress-nav-button.component';
import { NdGapPointDetailComponent } from '../../components/nd/nd-gap-point-detail.component';
import { NdPointSortControlsComponent } from '../../components/nd/nd-point-sort-controls.component';
import { NdPointNumberTreeComponent } from '../nd/shared/nd-point-number-tree.component';
import { NdStatusBadgeComponent } from '../../components/nd/nd-status-badge.component';
import { NdGapAnalysisComponent } from '../nd/gap-analysis/nd-gap-analysis.component';
import { REGUL_PIPELINE_FULL } from '../../../lib/nd/regul-fields';
import { AnalyseRegulComponent } from '../analyse-regul/analyse-regul.component';

/**
 * V4 — cloned from analyse-regul (V3) with full-markdown forward-only Regul workflow.
 * V3 page and `regul_pipeline` engine behavior stay unchanged.
 */
@Component({
  selector: 'app-analyse-regul-full',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    InProgressNavButtonComponent,
    NdGapPointDetailComponent,
    NdPointSortControlsComponent,
    NdPointNumberTreeComponent,
    NdStatusBadgeComponent,
    NdGapAnalysisComponent,
  ],
  templateUrl: '../analyse-regul/analyse-regul.component.html',
  styleUrl: '../analyse-regul/analyse-regul.component.scss',
})
export class AnalyseRegulFullComponent extends AnalyseRegulComponent {
  override readonly versionLabel = '';
  override readonly versionPath = '/analyse-regul-full';

  protected override readonly regulWorkflowEngineId = REGUL_PIPELINE_FULL;
  protected override readonly regulAnalysisRoute = '/nd/analyse-regul-full';
  protected override showReversePipelineUi = false;

  /** V4 always runs forward-only (full markdown); reverse is not used on this page. */
  override runAnalysisAndScroll(): void {
    this.runForwardOnlyAndScroll();
  }

  override async loadNdRunPoints(runId: string): Promise<void> {
    await super.loadNdRunPoints(runId);
    this.validateV4RunEngine();
  }

  protected override regulRunConfirmHint(): string {
    if (this.ndAuth.isDemoViewer()) {
      return 'Demo analysis replays saved CBUAE results — queued, running, and done states update quickly with no live AI.';
    }
    const model = this.regulWorkflowLlmSummary || 'admin-selected model';
    return (
      `Regul full-markdown analysis using ${model}. ` +
      'Sends complete parsed markdown for every attached internal file (any page count, multiple files supported; no section ranking). ' +
      'Forward judgment only — reverse coverage is skipped. Type start to confirm.'
    );
  }

  private validateV4RunEngine(): void {
    if (!this.ndRunId || !this.ndWorkflowEngine) return;
    if (this.ndWorkflowEngine === REGUL_PIPELINE_FULL) return;

    const msg =
      'This run is V3 (regul_pipeline), not V4 full markdown. ' +
      'Click Stop, then open Analyse Regul Full without ?run= and start a new run.';
    this.error = msg;
    this.toast.show(msg, 'error', 12000);

    if (this.ndRunStatus === 'running') {
      this.toast.show(
        'V3 runs on this page do slow section extraction before forward — Stop and create a new V4 run.',
        'warning',
        12000,
      );
    }
  }

  override get runBlockedReason(): string | null {
    if (this.ndAuth.isDemoViewer() && this.isNdShell) {
      return super.runBlockedReason;
    }
    if (
      this.ndRunId &&
      this.ndWorkflowEngine &&
      this.ndWorkflowEngine !== REGUL_PIPELINE_FULL
    ) {
      return 'Wrong workflow engine (V3). Stop this run and start fresh without ?run= in the URL.';
    }
    return super.runBlockedReason;
  }
}
