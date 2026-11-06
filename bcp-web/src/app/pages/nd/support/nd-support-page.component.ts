import { Component } from '@angular/core';
import { NdSupportCenterComponent } from '../../../components/nd/support/nd-support-center.component';

/** Full-page view of a workspace's support requests (same content as the floating Support panel). */
@Component({
  selector: 'app-nd-support-page',
  standalone: true,
  imports: [NdSupportCenterComponent],
  template: `
    <div class="nd-page-body">
      <div class="nd-page">
        <div class="nd-card support-card">
          <app-nd-support-center mode="page" />
        </div>
      </div>
    </div>
  `,
  styleUrls: ['../nd-shared.scss'],
  styles: [
    `
      .support-card {
        max-width: 860px;
        padding: 1.25rem;
      }
    `,
  ],
})
export class NdSupportPageComponent {}
