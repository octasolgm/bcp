import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ActiveSessionsPanelComponent } from '../../components/active-sessions-panel/active-sessions-panel.component';
import { ActiveAnalysisSessionsService } from '../../services/active-analysis-sessions.service';

@Component({
  selector: 'app-in-progress',
  standalone: true,
  imports: [CommonModule, RouterLink, ActiveSessionsPanelComponent],
  templateUrl: './in-progress.component.html',
  styleUrl: './in-progress.component.scss',
})
export class InProgressComponent implements OnInit, OnDestroy {
  private readonly sessionsService = inject(ActiveAnalysisSessionsService);

  readonly sessions = this.sessionsService.sessions;
  readonly loading = this.sessionsService.loading;

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
