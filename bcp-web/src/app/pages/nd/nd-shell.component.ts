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
import { NdShellFocusService } from '../../services/nd/nd-shell-focus.service';
import type { AnalysisRunSummary } from '../../../lib/nd/types';

type NavItem = {
  id: string;
  path: string;
  label: string;
  icon: 'grid' | 'file' | 'library' | 'clock' | 'plus' | 'users' | 'building' | 'check' | 'list' | 'trash';
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
  readonly shellFocus = inject(NdShellFocusService);

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
    if (item.id === 'in-progress') {
      const n = this.activeSessions.sessions().length + this.ndActiveRunCount;
      return n > 0 ? n : undefined;
    }
    const n = this.navBadges[item.id];
    return n && n > 0 ? n : undefined;
  }

  private async refreshNavBadges(): Promise<void> {
    const role = this.auth.getRole();
    if (!role) return;

    const next: Partial<Record<string, number>> = {};
    const set = (id: string, count: number) => {
      if (count > 0) next[id] = count;
    };

    const tasks: Promise<void>[] = [];

    if (role === 'maker' || role === 'super_admin') {
      tasks.push(
        this.api.getInternalDocuments().then((res) => {
          set('internal-documents', res.data?.length ?? 0);
        }),
      );
      tasks.push(
        this.api.getRegulationDocuments().then((res) => {
          set('regulation-documents', res.data?.length ?? 0);
        }),
      );
      tasks.push(
        this.api.getLibraries().then((res) => {
          set('libraries', res.data?.length ?? 0);
        }),
      );
    }

    const runsMineOnly = role === 'maker';
    tasks.push(
      this.api
        .getAnalysisRuns(runsMineOnly ? { mineOnly: true, ndOnly: true } : { ndOnly: true })
        .then((res) => {
          const runs = (res.data ?? []) as AnalysisRunSummary[];
          set('analysis-runs-all', runs.length);

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

    tasks.push(
      this.api
        .getAnalysisRuns({
          ndOnly: true,
          status: 'pulled_back',
          ...(runsMineOnly ? { mineOnly: true } : {}),
        })
        .then((res) => {
          set('analysis-runs-correction', res.data?.length ?? 0);
        }),
    );

    if (role === 'super_admin') {
      tasks.push(
        this.api.getUsers().then((res) => {
          set('admin-users', res.data?.length ?? 0);
        }),
        this.api.getDepartments().then((res) => {
          set('admin-departments', res.data?.length ?? 0);
        }),
        this.api.getCheckerQueue().then((res) => {
          set('checker-queue', res.data?.length ?? 0);
        }),
        this.api.getReviewerQueue().then((res) => {
          set('reviewer-queue', res.data?.length ?? 0);
        }),
      );
    } else if (role === 'checker') {
      tasks.push(
        this.api.getCheckerQueue().then((res) => {
          set('checker-queue', res.data?.length ?? 0);
        }),
      );
    } else if (role === 'reviewer') {
      tasks.push(
        this.api.getCheckerQueue().then((res) => {
          set('checker-queue', res.data?.length ?? 0);
        }),
        this.api.getReviewerQueue().then((res) => {
          set('reviewer-queue', res.data?.length ?? 0);
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
      id: 'analysis-runs-all',
      path: '/nd/analysis-runs',
      label: 'All analysis',
      icon: 'list',
      secondary: true,
      ...(role === 'maker' ? { queryParams: { mine: '1' } } : {}),
    };
    return [
      { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
      { id: 'internal-documents', path: '/nd/internal-documents', label: 'Documents', icon: 'file' },
      { id: 'regulation-documents', path: '/nd/regulation-documents', label: 'Regulation Docs Library', icon: 'library' },
      { id: 'libraries', path: '/nd/libraries', label: 'Regulation Points Libraries', icon: 'list' },
      { id: 'in-progress', path: '/nd/in-progress', label: 'In progress', icon: 'clock' },
      viewAll,
      { id: 'analyse-v8', path: '/nd/analyse-v8', label: 'New analysis', icon: 'plus', cta: true },
    ];
  }

  private navForRole(role: string): NavItem[] {
    switch (role) {
      case 'super_admin':
        return [
          ...this.workspaceNav(role),
          { id: 'admin-users', path: '/nd/admin/users', label: 'User Management', icon: 'users' },
          { id: 'admin-departments', path: '/nd/admin/departments', label: 'Departments', icon: 'building' },
          { id: 'admin-deleted-runs', path: '/nd/admin/deleted-runs', label: 'Deleted analyses', icon: 'trash' },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check' },
          { id: 'reviewer-queue', path: '/nd/reviewer', label: 'Pending final review', icon: 'check' },
        ];
      case 'maker':
        return [
          ...this.workspaceNav(role),
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            queryParams: { mine: '1', correction: '1' },
          },
        ];
      case 'checker':
        return [
          { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { id: 'analysis-runs-all', path: '/nd/analysis-runs', label: 'All analysis', icon: 'list' },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check' },
        ];
      case 'reviewer':
        return [
          { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { id: 'analysis-runs-all', path: '/nd/analysis-runs', label: 'All analysis', icon: 'list' },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check' },
          { id: 'reviewer-queue', path: '/nd/reviewer', label: 'Pending final review', icon: 'check' },
        ];
      default:
        return [{ id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' }];
    }
  }
}
