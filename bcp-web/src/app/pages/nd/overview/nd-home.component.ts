import { Component, OnInit, inject } from '@angular/core';
import { DashboardComponent } from '../../dashboard/dashboard.component';
import { NdOverviewComponent } from './nd-overview.component';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-home',
  standalone: true,
  imports: [DashboardComponent, NdOverviewComponent],
  template: `
    @if (showComplianceDashboard) {
      <app-dashboard />
    } @else {
      <app-nd-overview />
    }
  `,
})
export class NdHomeComponent implements OnInit {
  private readonly auth = inject(NdAuthService);
  showComplianceDashboard = false;

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    const role = this.auth.getRole();
    this.showComplianceDashboard = role === 'maker' || role === 'super_admin';
  }
}
