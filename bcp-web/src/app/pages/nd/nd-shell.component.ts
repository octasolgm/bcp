import { Component, OnDestroy, OnInit, inject, ChangeDetectorRef } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { NdApiService } from '../../services/nd/nd-api.service';
import { ThemeService, type ThemeMode } from '../../services/theme.service';
import {
  ActiveAnalysisSessionsService,
} from '../../services/active-analysis-sessions.service';
import { NdShellFocusService } from '../../services/nd/nd-shell-focus.service';
import { BrandLogoComponent } from '../../components/brand-logo/brand-logo.component';

type NavItem = {
  id: string;
  path: string;
  label: string;
  icon: 'grid' | 'file' | 'library' | 'clock' | 'plus' | 'users' | 'building' | 'check' | 'list' | 'trash' | 'settings';
  cta?: boolean;
  secondary?: boolean;
  queryParams?: Record<string, string>;
  /** Show nav badge even when count is 0. */
  badgeAlways?: boolean;
};

@Component({
  selector: 'app-nd-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BrandLogoComponent],
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
  private readonly cdr = inject(ChangeDetectorRef);

  readonly profile = this.auth.profile;
  navItems: NavItem[] = [];
  settingsOpen = false;
  navBadges: Partial<Record<string, number>> = {};
  ndActiveRunCount = 0;
  pass2LlmSummary = '';
  private navSub: Subscription | null = null;
  private badgeRefreshTimer: ReturnType<typeof setTimeout> | null = null;
  private badgeRefreshInFlight = false;

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
    if (!this.auth.profile()) {
      await this.auth.refreshProfile();
    }
    const role = this.auth.getRole();
    if (!role) {
      await this.router.navigate(['/nd/auth/login']);
      return;
    }
    this.navItems = this.navForRole(role);
    void this.refreshPass2LlmSummary();
    this.activeSessions.watch();
    this.scheduleNavBadgeRefresh();
    this.navSub = this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => {
        this.scheduleNavBadgeRefresh();
      });
  }

  ngOnDestroy(): void {
    this.navSub?.unsubscribe();
    if (this.badgeRefreshTimer) clearTimeout(this.badgeRefreshTimer);
    this.activeSessions.unwatch();
  }

  private scheduleNavBadgeRefresh(): void {
    if (this.badgeRefreshTimer) clearTimeout(this.badgeRefreshTimer);
    this.badgeRefreshTimer = setTimeout(() => {
      this.badgeRefreshTimer = null;
      void this.refreshNavBadges();
    }, 1200);
  }

  badgeFor(item: NavItem): number | undefined {
    if (item.id === 'in-progress') {
      const n = this.ndActiveRunCount + this.activeSessions.sessions().length;
      if (item.badgeAlways) return n;
      return n > 0 ? n : undefined;
    }
    const n = this.navBadges[item.id] ?? 0;
    if (item.badgeAlways) return n;
    return n > 0 ? n : undefined;
  }

  private async refreshPass2LlmSummary(): Promise<void> {
    const res = await this.api.getActiveDualVerifyLlm();
    if (!res.success || !res.data) {
      this.pass2LlmSummary = '';
      return;
    }
    const { providerLabel, model } = res.data;
    this.pass2LlmSummary = `${providerLabel} · ${model}`;
  }

  private async refreshNavBadges(): Promise<void> {
    if (this.badgeRefreshInFlight) return;
    this.badgeRefreshInFlight = true;
    try {
      const role = this.auth.getRole();
      if (!role) return;

      const res = await this.api.getWorkspaceNavCounts();
      if (!res.success || !res.data) {
        this.ndActiveRunCount = 0;
        this.cdr.markForCheck();
        return;
      }

      const c = res.data;
      const next: Partial<Record<string, number>> = {
        'analysis-runs-all': c.analysisRunsAll,
        'analysis-runs-correction': c.analysisRunsCorrection,
        'internal-documents': c.internalDocuments,
        'regulation-documents': c.regulationDocuments,
        libraries: c.libraries,
        'internal-documents-deleted': c.internalDocumentsDeleted,
        'regulation-documents-deleted': c.regulationDocumentsDeleted,
        'admin-users': c.adminUsers,
        'admin-departments': c.adminDepartments,
        'checker-queue': c.checkerQueue,
        'reviewer-queue': c.reviewerQueue,
      };
      this.ndActiveRunCount = c.analysisRunsInProgress;
      this.navBadges = next;
      this.cdr.markForCheck();
    } finally {
      this.badgeRefreshInFlight = false;
    }
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
      badgeAlways: true,
      ...(role === 'maker' ? { queryParams: { mine: '1' } } : {}),
    };
    const items: NavItem[] = [
      { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
      { id: 'internal-documents', path: '/nd/internal-documents', label: 'Documents', icon: 'file' },
      { id: 'regulation-documents', path: '/nd/regulation-documents', label: 'Regulation Docs Library', icon: 'library' },
    ];
    if (role === 'super_admin') {
      items.push(
        {
          id: 'internal-documents-deleted',
          path: '/nd/internal-documents/deleted',
          label: 'Deleted documents',
          icon: 'trash',
          secondary: true,
        },
        {
          id: 'regulation-documents-deleted',
          path: '/nd/regulation-documents/deleted',
          label: 'Deleted regulations',
          icon: 'trash',
          secondary: true,
        },
      );
    }
    items.push(
      { id: 'libraries', path: '/nd/libraries', label: 'Regulation Points Libraries', icon: 'list' },
      { id: 'in-progress', path: '/nd/in-progress', label: 'In progress', icon: 'clock', badgeAlways: true },
      viewAll,
      { id: 'analyse-v8', path: '/nd/analyse-v8', label: 'New analysis', icon: 'plus', cta: true },
    );
    return items;
  }

  private navForRole(role: string): NavItem[] {
    switch (role) {
      case 'super_admin':
        return [
          ...this.workspaceNav(role),
          { id: 'admin-users', path: '/nd/admin/users', label: 'User Management', icon: 'users' },
          { id: 'admin-departments', path: '/nd/admin/departments', label: 'Departments', icon: 'building' },
          { id: 'admin-settings', path: '/nd/admin/settings', label: 'Platform settings', icon: 'settings' },
          { id: 'admin-deleted-runs', path: '/nd/admin/deleted-runs', label: 'Deleted analyses', icon: 'trash' },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            badgeAlways: true,
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check', badgeAlways: true },
          { id: 'reviewer-queue', path: '/nd/reviewer', label: 'Pending final review', icon: 'check', badgeAlways: true },
        ];
      case 'maker':
        return [
          ...this.workspaceNav(role),
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            badgeAlways: true,
            queryParams: { mine: '1', correction: '1' },
          },
        ];
      case 'checker':
        return [
          { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { id: 'analysis-runs-all', path: '/nd/analysis-runs', label: 'All analysis', icon: 'list', badgeAlways: true },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            badgeAlways: true,
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check', badgeAlways: true },
        ];
      case 'reviewer':
        return [
          { id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' },
          { id: 'analysis-runs-all', path: '/nd/analysis-runs', label: 'All analysis', icon: 'list', badgeAlways: true },
          {
            id: 'analysis-runs-correction',
            path: '/nd/analysis-runs',
            label: 'Pending correction',
            icon: 'clock',
            badgeAlways: true,
            queryParams: { correction: '1' },
          },
          { id: 'checker-queue', path: '/nd/checker', label: 'Pending review', icon: 'check', badgeAlways: true },
          { id: 'reviewer-queue', path: '/nd/reviewer', label: 'Pending final review', icon: 'check', badgeAlways: true },
        ];
      default:
        return [{ id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' }];
    }
  }
}
