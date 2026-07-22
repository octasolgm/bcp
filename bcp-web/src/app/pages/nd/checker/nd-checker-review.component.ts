import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { NdGapAnalysisComponent } from '../gap-analysis/nd-gap-analysis.component';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-checker-review',
  standalone: true,
  imports: [NdGapAnalysisComponent],
  template: `
    @if (runId) {
      <app-nd-gap-analysis [embedRunId]="runId" reviewWorkspaceMode="checker" />
    }
  `,
})
export class NdCheckerReviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly auth = inject(NdAuthService);

  runId = '';

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    this.runId = this.route.snapshot.paramMap.get('runId') ?? '';
  }
}
