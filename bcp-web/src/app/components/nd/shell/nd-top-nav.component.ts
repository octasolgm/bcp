import { Component, Input, inject } from '@angular/core';
import { NdAuthService } from '../../../services/nd/nd-auth.service';

@Component({
  selector: 'app-nd-top-nav',
  standalone: true,
  template: `
    <header class="nd-topnav">
      <h1>{{ title }}</h1>
      @if (auth.profile(); as p) {
        <span class="nd-muted">{{ p.fullName }}</span>
      }
    </header>
  `,
  styles: [
    `
      .nd-topnav {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 1rem 1.5rem;
        border-bottom: 1px solid var(--border);
      }
      h1 {
        margin: 0;
        font-size: 1.125rem;
        font-weight: 600;
      }
    `,
  ],
})
export class NdTopNavComponent {
  @Input({ required: true }) title!: string;
  readonly auth = inject(NdAuthService);
}
