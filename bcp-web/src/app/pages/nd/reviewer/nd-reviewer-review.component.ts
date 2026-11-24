import { Component, OnInit, inject, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { NdGapAnalysisComponent } from '../gap-analysis/nd-gap-analysis.component';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-reviewer-review',
  standalone: true,
  imports: [NdGapAnalysisComponent],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    @if (runId) {
      <app-nd-gap-analysis [embedRunId]="runId" reviewWorkspaceMode="reviewer" />
    }
  `,
})
export class NdReviewerReviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(NdAuthService);

  runId = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
  }
}
