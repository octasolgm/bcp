import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';
import { shellRoute } from '../../services/app-route-prefix';

@Component({
  selector: 'app-in-progress-nav-button',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './in-progress-nav-button.component.html',
  styleUrl: './in-progress-nav-button.component.scss',
})
export class InProgressNavButtonComponent {
  private readonly sessionsService = inject(ActiveAnalysisSessionsService);
  private readonly router = inject(Router);

  readonly sessions = this.sessionsService.sessions;
  readonly loading = this.sessionsService.loading;

  openInProgressPage(): void {
    this.router.navigate([shellRoute(this.router, '/in-progress')]);
  }
}
