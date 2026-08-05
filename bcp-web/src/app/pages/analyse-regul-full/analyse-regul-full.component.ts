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
  override readonly versionLabel = 'V4 — Regul Full Markdown';
  override readonly versionPath = '/analyse-regul-full';

  protected override readonly regulWorkflowEngineId = REGUL_PIPELINE_FULL;
  protected override readonly regulAnalysisRoute = '/nd/analyse-regul-full';

  protected override regulRunConfirmHint(): string {
    const llm = this.regulWorkflowLlmSummary || 'admin-selected LLM';
    return (
      `Regul full-markdown analysis using ${llm}. ` +
      'Sends complete parsed markdown for every attached internal file (no section ranking). ' +
      'Forward judgment only — reverse coverage is skipped. Type start to confirm.'
    );
  }
}
