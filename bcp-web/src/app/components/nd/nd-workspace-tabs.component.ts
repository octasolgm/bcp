import { Component, Input, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NdAuthService } from '../../services/nd/nd-auth.service';

export type NdWorkspaceTab =
  | 'all_analysis'
  | 'pending_correction'
  | 'pending_review'
  | 'pending_final_review';

@Component({
  selector: 'app-nd-workspace-tabs',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="filter-pills tabs nd-shrink">
      @if (showAllAnalysis) {
        <a
          routerLink="/nd/analysis-runs"
          [queryParams]="allAnalysisParams"
          class="tab"
          [class.active]="active === 'all_analysis'"
        >All analysis</a>
      }
      @if (showPendingCorrection) {
        <a
          routerLink="/nd/analysis-runs"
          [queryParams]="correctionParams"
          class="tab"
          [class.active]="active === 'pending_correction'"
        >Pending correction</a>
      }
      @if (showPendingReview) {
        <a
          routerLink="/nd/checker"
          class="tab"
          [class.active]="active === 'pending_review' && primaryActive && !extraTabActive"
        >Pending review</a>
      }
      @if (showPendingFinalReview) {
        <a
          routerLink="/nd/reviewer"
          class="tab"
          [class.active]="active === 'pending_final_review' && primaryActive && !extraTabActive"
        >Pending final review</a>
      }
      @if (extraTabLabel && extraTabLink.length) {
        <a
          [routerLink]="extraTabLink"
          [queryParams]="extraTabQueryParams"
          class="tab"
          [class.active]="extraTabActive"
        >{{ extraTabLabel }}</a>
      }
    </div>
  `,
})
export class NdWorkspaceTabsComponent {
  private readonly auth = inject(NdAuthService);

  @Input({ required: true }) active!: NdWorkspaceTab;
  @Input() primaryActive = true;
  @Input() extraTabLabel: string | null = null;
  @Input() extraTabLink: string[] = [];
  @Input() extraTabQueryParams: Record<string, string> | null = null;
  @Input() extraTabActive = false;

  private get role(): string {
    return this.auth.getRole() ?? '';
  }

  private get mineOnly(): boolean {
    return this.role === 'maker';
  }

  get showAllAnalysis(): boolean {
    return ['maker', 'checker', 'reviewer', 'super_admin'].includes(this.role);
  }

  get showPendingCorrection(): boolean {
    return ['maker', 'checker', 'reviewer', 'super_admin'].includes(this.role);
  }

  get showPendingReview(): boolean {
    return ['checker', 'reviewer', 'super_admin'].includes(this.role);
  }

  get showPendingFinalReview(): boolean {
    return ['reviewer', 'super_admin'].includes(this.role);
  }

  get allAnalysisParams(): Record<string, string> | null {
    return this.mineOnly ? { mine: '1' } : null;
  }

  get correctionParams(): Record<string, string> {
    return this.mineOnly ? { mine: '1', correction: '1' } : { correction: '1' };
  }
}
