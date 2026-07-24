import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { ThemeService, type ThemeMode } from '../services/theme.service';
import { WorkspaceService, type WorkspaceId } from '../services/workspace.service';
import { ToastService } from '../services/toast.service';
import { ApiService } from '../services/api.service';
import { ActiveAnalysisSessionsService } from '../services/active-analysis-sessions.service';
import { AuthService } from '../services/auth.service';
import { BrandLogoComponent } from '../components/brand-logo/brand-logo.component';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BrandLogoComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
})
export class ShellComponent implements OnInit, OnDestroy {
  readonly theme = inject(ThemeService);
  readonly workspace = inject(WorkspaceService);
  readonly toast = inject(ToastService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  readonly activeSessions = inject(ActiveAnalysisSessionsService);
  readonly auth = inject(AuthService);

  settingsOpen = false;
  workspaceMenuOpen = false;
  documentsBadge: number | null = null;
  regulationsBadge: number | null = null;
  private navSub: Subscription | null = null;

  get workspaceNav() {
    return [
      { path: '/old/dashboard', label: 'Overview', icon: 'grid' as const },
      {
        path: '/old/documents',
        label: 'Documents',
        icon: 'file' as const,
        badge: this.documentsBadge ?? undefined,
      },
      {
        path: '/old/regulations',
        label: 'Regulation Docs Library',
        icon: 'library' as const,
        badge: this.regulationsBadge ?? undefined,
      },
    ];
  }

  get profileInitial(): string {
    const name = this.auth.currentUser()?.username?.trim();
    return (name?.charAt(0) ?? 'U').toUpperCase();
  }

  ngOnInit(): void {
    this.refreshNavBadges();
    this.activeSessions.watch();
    this.navSub = this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => {
        this.refreshNavBadges();
        // Debounced inside ActiveAnalysisSessionsService — avoid hammering /sessions/active.
        this.activeSessions.refresh();
      });
  }

  ngOnDestroy(): void {
    this.navSub?.unsubscribe();
    this.activeSessions.unwatch();
  }

  refreshNavBadges(): void {
    const ws = this.workspace.current().id;
    this.api.listStoredDocuments('document', ws).subscribe({
      next: (r) => {
        this.documentsBadge = (r.data ?? []).length || null;
      },
      error: () => {
        this.documentsBadge = null;
      },
    });
    this.api.listStoredDocuments('regulation', ws).subscribe({
      next: (r) => {
        this.regulationsBadge = (r.data ?? []).length || null;
      },
      error: () => {
        this.regulationsBadge = null;
      },
    });
  }

  toggleSettings(): void {
    this.workspaceMenuOpen = false;
    this.settingsOpen = !this.settingsOpen;
  }

  closeSettings(): void {
    this.settingsOpen = false;
  }

  setTheme(mode: ThemeMode): void {
    this.theme.setMode(mode);
    this.toast.show(`Theme set to ${mode}`, 'success', 2000);
  }

  toggleWorkspaceMenu(): void {
    this.workspaceMenuOpen = !this.workspaceMenuOpen;
  }

  selectWorkspace(id: WorkspaceId): void {
    this.workspace.setWorkspace(id);
    this.workspaceMenuOpen = false;
    this.refreshNavBadges();
    this.toast.show(`Workspace switched to ${this.workspace.current().label}`, 'success');
  }

  logout(): void {
    this.auth.logout();
    this.closeSettings();
    this.router.navigate(['/login']);
  }
}
