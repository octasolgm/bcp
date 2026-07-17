import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { ActiveSessionsPanelComponent } from '../../components/active-sessions-panel/active-sessions-panel.component';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';
import { shellRoute } from '../../services/app-route-prefix';

@Component({
  selector: 'app-in-progress',
  standalone: true,
  imports: [CommonModule, RouterLink, ActiveSessionsPanelComponent],
  templateUrl: './in-progress.component.html',
  styleUrl: './in-progress.component.scss',
})
export class InProgressComponent implements OnInit, OnDestroy {
  private readonly sessionsService = inject(ActiveAnalysisSessionsService);
  private readonly router = inject(Router);

  readonly sessions = this.sessionsService.sessions;
  readonly loading = this.sessionsService.loading;

  get newAnalysisPath(): string {
    return shellRoute(this.router, '/analyse-v8');
  }

  get resumePath(): string {
    return shellRoute(this.router, '/analyse-v8');
  }

  ngOnInit(): void {
    this.sessionsService.watch();
    this.sessionsService.refresh();
  }

  ngOnDestroy(): void {
    this.sessionsService.unwatch();
  }

  refresh(): void {
    this.sessionsService.refresh();
  }
}
