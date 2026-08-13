import { Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';

/** @deprecated Use `/nd/analyse-regul-full` (new) or `/nd/analysis-runs` (list). */
@Component({
  selector: 'app-nd-run-analysis',
  standalone: true,
  template: `<p class="muted-loading">Redirecting…</p>`,
  styles: [
    `
      .muted-loading {
        padding: 2rem;
        color: var(--text-muted);
      }
    `,
  ],
})
export class NdRunAnalysisComponent implements OnInit {
  private readonly router = inject(Router);

  ngOnInit(): void {
    const path = typeof window !== 'undefined' ? window.location.pathname : this.router.url;
    const target = path.includes('/run-analysis/') ? '/nd/analysis-runs' : '/nd/analyse-regul-full';
    void this.router.navigate([target], { replaceUrl: true, queryParamsHandling: 'merge' });
  }
}
