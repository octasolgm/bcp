import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { NdApiService } from '../../services/nd/nd-api.service';
import { ThemeService, type ThemeMode } from '../../services/theme.service';
import {
  ActiveAnalysisSessionsService,
  isActiveDocumentRun,
} from '../../services/active-analysis-sessions.service';
import type { AnalysisRunSummary } from '../../../lib/nd/types';

type NavItem = {
  path: string;
  label: string;
  icon: 'grid' | 'file' | 'library' | 'clock' | 'plus' | 'users' | 'building' | 'check' | 'list';
  cta?: boolean;
  secondary?: boolean;
  queryParams?: Record<string, string>;
};

@Component({
  selector: 'app-nd-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './nd-shell.component.html',
  styleUrl: './nd-shell.component.scss',
})
export class NdShellComponent implements OnInit, OnDestroy {
  readonly auth = inject(NdAuthService);
  private readonly router = inject(Router);
  private readonly api = inject(NdApiService);
  readonly theme = inject(ThemeService);
  readonly activeSessions = inject(ActiveAnalysisSessionsService);

  readonly profile = this.auth.profile;
  navItems: NavItem[] = [];
  settingsOpen = false;
  navBadges: Partial<Record<string, number>> = {};
  ndActiveRunCount = 0;
  private navSub: Subscription | null = null;

  get profileInitial(): string {
    const name = this.profile()?.fullName?.trim();
    return (name?.charAt(0) ?? 'U').toUpperCase();
  }

  get roleLabel(): string {
    const role = this.auth.getRole();
    return role ? role.replace(/_/g, ' ') : '';
  }

  get showNewAnalysis(): boolean {
    const role = this.auth.getRole();
    return role === 'maker' || role === 'super_admin';
  }

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile();
    const role = this.auth.getRole();
    if (!role) {
      await this.router.navigate(['/nd/auth/login']);
      return;
    }
    this.navItems = this.navForRole(role);
    this.activeSessions.watch();
    void this.refreshNavBadges();
    this.navSub = this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => {
        void this.refreshNavBadges();
        this.activeSessions.refresh();
      });
  }

  ngOnDestroy(): void {
    this.navSub?.unsubscribe();
    this.activeSessions.unwatch();
  }

  badgeFor(item: NavItem): number | undefined {
    if (item.path === '/nd/in-progress') {
      const n = this.activeSessions.sessions().length + this.ndActiveRunCount;
      return n > 0 ? n : undefined;
    }
    const n = this.navBadges[item.path];
    return n && n > 0 ? n : undefined;
  }

  private async refreshNavBadges(): Promise<void> {
    const role = this.auth.getRole();
    if (!role) return;

    const next: Partial<Record<string, number>> = {};
    const set = (path: string, count: number) => {
      if (count > 0) next[path] = count;
    };

    const tasks: Promise<void>[] = [];

    if (role === 'maker' || role === 'super_admin') {
      tasks.push(
        this.api.getInternalDocuments().then((res) => {
          set('/nd/internal-documents', res.data?.length ?? 0);
        }),
      );
      tasks.push(
        this.api.getRegulationDocuments().then((res) => {
          set('/nd/regulation-documents', res.data?.length ?? 0);
        }),
      );
      tasks.push(
        this.api.getLibraries().then((res) => {
          set('/nd/libraries', res.data?.length ?? 0);
        }),
      );
    }

    const runsMineOnly = role === 'maker';
    tasks.push(
      this.api.getAnalysisRuns(runsMineOnly ? { mineOnly: true } : undefined).then((res) => {
        const runs = (res.data ?? []) as AnalysisRunSummary[];
        set('/nd/analysis-runs', runs.length);

        const ndActive = runs.filter(
          (r) =>
            r.source === 'nd_analysis' &&
            isActiveDocumentRun({
              status: r.status,
              completedPoints: r.processedPointsCount,
              pointCount: r.totalPointsCount,
              updatedAt: r.createdAt,
            }),
        ).length;
        this.ndActiveRunCount = ndActive;
      }),
    );

    if (role === 'super_admin') {
      tasks.push(
        this.api.getUsers().then((res) => {
          set('/nd/admin/users', res.data?.length ?? 0);
        }),
        this.api.getDepartments().then((res) => {
          set('/nd/admin/departments', res.data?.length ?? 0);
        }),
        this.api.getCheckerQueue().then((res) => {
          set('/nd/checker', res.data?.length ?? 0);
        }),
        this.api.getReviewerQueue().then((res) => {
          set('/nd/reviewer', res.data?.length ?? 0);
        }),
      );
    } else if (role === 'checker') {
      tasks.push(
        this.api.getCheckerQueue().then((res) => {
          set('/nd/checker', res.data?.length ?? 0);
        }),
      );
    } else if (role === 'reviewer') {
      tasks.push(
        this.api.getReviewerQueue().then((res) => {
          set('/nd/reviewer', res.data?.length ?? 0);
        }),
      );
    }

    await Promise.all(tasks);
    this.navBadges = next;
  }

  toggleSettings(): void {
    this.settingsOpen = !this.settingsOpen;
  }

  closeSettings(): void {
    this.settingsOpen = false;
  }

  setTheme(mode: ThemeMode): void {
    this.theme.setMode(mode);
  }

  async logout(): Promise<void> {
    await this.auth.signOut();
    this.closeSettings();
    await this.router.navigate(['/nd/auth/login']);
  }

  private workspaceNav(role: string): NavItem[] {
    const viewAll: NavItem = {
      path: '/nd/analysis-runs',
      label: 'All analysis runs',
      icon: 'list',
      secondary: true,
      ...(role === 'maker' ? { queryParams: { mine: '1' } } : {}),
    };
    return [
      { path: '/nd/overview', label: 'Overview', icon: 'grid' },
      { path: '/nd/internal-documents', label: 'Documents', icon: 'file' },
      { path: '/nd/regulation-documents', label: 'Regulation Docs Library', icon: 'library' },
      { path: '/nd/libraries', label: 'Regulation Points Libraries', icon: 'list' },
      { path: '/nd/in-progress', label: 'In progress', icon: 'clock' },
      viewAll,
      { path: '/nd/analyse-v8', label: 'New analysis', icon: 'plus', cta: true },
    ];
  }

  private navForRole(role: string): NavItem[] {
    switch (role) {
      case 'super_admin':
        return [
          ...this.workspaceNav(role),
          { path: '/nd/admin/users', label: 'User Management', icon: 'users' },
          { path: '/nd/admin/departments', label: 'Departments', icon: 'building' },
          { path: '/nd/checker', label: 'Pending Review', icon: 'check' },
          { path: '/nd/reviewer', label: 'Pending Final Review', icon: 'check' },
        ];
      case 'maker':
        return this.workspaceNav(role);
      case 'checker':
        return [
          { path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { path: '/nd/checker', label: 'Pending Review', icon: 'check' },
          { path: '/nd/analysis-runs', label: 'All analysis runs', icon: 'list', secondary: true },
        ];
      case 'reviewer':
        return [
          { path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { path: '/nd/reviewer', label: 'Pending Final Review', icon: 'check' },
          { path: '/nd/analysis-runs', label: 'All analysis runs', icon: 'list', secondary: true },
        ];
      default:
        return [{ path: '/nd/overview', label: 'Overview', icon: 'grid' }];
    }
  }
}
