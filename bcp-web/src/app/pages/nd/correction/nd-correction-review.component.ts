import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { NdGapAnalysisComponent } from '../gap-analysis/nd-gap-analysis.component';
import { NdAuthService } from '../../../services/nd/nd-auth.service';
import type { RunReviewPanelMode } from '../../../components/nd/nd-run-review-panel.component';

@Component({
  selector: 'app-nd-correction-review',
  standalone: true,
  imports: [NdGapAnalysisComponent],
  template: `
    @if (runId) {
      <app-nd-gap-analysis [embedRunId]="runId" [reviewWorkspaceMode]="reviewMode" />
    }
  `,
})
export class NdCorrectionReviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(NdAuthService);

  runId = '';
  reviewMode: RunReviewPanelMode = 'maker';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
    const role = this.auth.getRole();
    this.reviewMode = role === 'maker' || role === 'super_admin' ? 'maker' : 'none';
  }
}
