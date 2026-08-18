import { Component, OnDestroy, OnInit, inject, ChangeDetectorRef, effect } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, Subscription } from 'rxjs';
import { NdAuthService } from '../../services/nd/nd-auth.service';
import { NdApiService } from '../../services/nd/nd-api.service';
import { ThemeService, type ThemeMode } from '../../services/theme.service';
import {
  ActiveAnalysisSessionsService,
} from '../../services/active-analysis-sessions.service';
import { ToastService } from '../../services/toast.service';
import { NdShellFocusService } from '../../services/nd/nd-shell-focus.service';
import { NdWorkspaceNavService } from '../../services/nd/nd-workspace-nav.service';
import type { NdNavBadgeBumps } from '../../../lib/nd/nav-badge-bumps';
import { DeployVersionService } from '../../services/deploy-version.service';
import { BrandLogoComponent } from '../../components/brand-logo/brand-logo.component';
import { startPanelResize } from '../shared/panel-resize';

type NavIcon =
  | 'grid'
  | 'file'
  | 'library'
  | 'clock'
  | 'plus'
  | 'users'
  | 'building'
  | 'check'
  | 'list'
  | 'trash'
  | 'settings'
  | 'inbox'
  | 'chevron';

type NavItem = {
  id: string;
  path: string;
  label: string;
  icon: NavIcon;
  cta?: boolean;
  secondary?: boolean;
  queryParams?: Record<string, string>;
  /** Show nav badge even when count is 0. */
  badgeAlways?: boolean;
};

type NavGroup = {
  id: string;
  label: string;
  icon: NavIcon;
  children: NavItem[];
};

type NavEntry =
  | { kind: 'link'; item: NavItem }
  | { kind: 'group'; group: NavGroup };

@Component({
  selector: 'app-nd-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, BrandLogoComponent, NgTemplateOutlet],
  templateUrl: './nd-shell.component.html',
  styleUrl: './nd-shell.component.scss',
})
export class NdShellComponent implements OnInit, OnDestroy {
  private static readonly SIDEBAR_WIDTH_KEY = 'nd-sidebar-width';
  private static readonly BADGE_CACHE_KEY = 'nd-sidebar-badges';

  readonly auth = inject(NdAuthService);
  readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly api = inject(NdApiService);
  readonly theme = inject(ThemeService);
  readonly activeSessions = inject(ActiveAnalysisSessionsService);
  readonly shellFocus = inject(NdShellFocusService);
  private readonly workspaceNav = inject(NdWorkspaceNavService);
  readonly deployVersion = inject(DeployVersionService);
  private readonly cdr = inject(ChangeDetectorRef);

  readonly profile = this.auth.profile;
  navEntries: NavEntry[] = [];
  /** Group ids the user expanded/collapsed manually. */
  expandedGroups = new Set<string>();
  settingsOpen = false;
  navBadges: Partial<Record<string, number>> = {};
  ndActiveRunCount = 0;
  pass2LlmSummary = '';
  sidebarWidth = 240;
  private navSub: Subscription | null = null;
  private navRefreshSub: Subscription | null = null;
  private badgeRefreshTimer: ReturnType<typeof setTimeout> | null = null;
  private badgeRefreshInFlight = false;
  private badgeRefreshQueued = false;
  private badgeApplyGeneration = 0;
  private lastBadgeBumpMs = 0;
  private badgesLoadedOnce = false;

  /** Demo accounts: hide legacy version picker only (sidebar New analysis stays). */
  private static readonly DEMO_HIDDEN_NAV_IDS = new Set(['analysis-versions', 'analyse-v8']);

  private readonly rebuildNavOnProfile = effect(() => {
    const profile = this.auth.profile();
    const role = profile?.role;
    if (!role) return;
    // Track isDemo so nav rebuilds when demo flag is normalized after profile load.
    void profile?.isDemo;
    this.navEntries = this.navForRole(role);
    this.cdr.markForCheck();
  });
  private sessionsWatching = false;

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

  navGroupChildren(group: NavGroup): NavItem[] {
    if (!this.auth.isDemoViewer()) return group.children;
    return group.children.filter((child) => !NdShellComponent.DEMO_HIDDEN_NAV_IDS.has(child.id));
  }

  showNavPass2Subtitle(child: NavItem): boolean {
    return !this.auth.isDemoViewer() && child.id === 'analyse-v8' && !!this.pass2LlmSummary;
  }

  get newAnalysisLink(): string[] {
    return ['/nd/analyse-regul-full'];
  }

  async ngOnInit(): Promise<void> {
    await this.auth.refreshProfile(true);
    const role = this.auth.getRole();
    if (!role) {
      await this.router.navigate(['/nd/auth/login']);
      return;
    }
    this.navEntries = this.navForRole(role);
    this.sidebarWidth = this.loadSidebarWidth();
    this.expandedGroups = new Set();
    this.syncExpandedGroupsToRoute();
    this.hydrateBadgesFromCache(role);
    this.scheduleNavBadgeRefresh();
    void this.deployVersion.ensureLoaded().then(() => this.cdr.markForCheck());
    if (!this.isOverviewRoute() && !this.auth.isDemoViewer()) {
      setTimeout(() => void this.refreshPass2LlmSummary(), 2500);
    }
    this.syncActiveSessionPolling();
    this.navRefreshSub = this.workspaceNav.refreshRequested.subscribe(() => {
      if (this.badgeRefreshTimer) {
        clearTimeout(this.badgeRefreshTimer);
        this.badgeRefreshTimer = null;
      }
      const sinceBump = Date.now() - this.lastBadgeBumpMs;
      const delayMs = sinceBump < 2500 ? 2500 - sinceBump : 0;
      this.badgeRefreshTimer = setTimeout(() => {
        this.badgeRefreshTimer = null;
        void this.refreshNavBadges();
      }, delayMs);
    });
    this.navRefreshSub.add(
      this.workspaceNav.bumps.subscribe((bumps) => this.applyNavBadgeBumps(bumps)),
    );
    this.navSub = this.router.events
      .pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe(() => {
        this.syncExpandedGroupsToRoute();
        this.scheduleNavBadgeRefresh();
        this.syncActiveSessionPolling();
      });
  }

  ngOnDestroy(): void {
    this.navSub?.unsubscribe();
    this.navRefreshSub?.unsubscribe();
    if (this.badgeRefreshTimer) clearTimeout(this.badgeRefreshTimer);
    if (this.sessionsWatching) {
      this.activeSessions.unwatch();
      this.sessionsWatching = false;
    }
    document.body.classList.remove('panel-resizing');
  }

  startSidebarResize(event: MouseEvent): void {
    if (this.shellFocus.regulationPointsPanelOpen()) return;
    startPanelResize(
      {
        kind: 'sidebar-width',
        startX: event.clientX,
        startY: event.clientY,
        startVal: this.sidebarWidth,
      },
      event,
      (_kind, value) => {
        this.sidebarWidth = value;
        this.saveSidebarWidth();
        this.cdr.markForCheck();
      },
    );
  }

  private loadSidebarWidth(): number {
    try {
      const raw = localStorage.getItem(NdShellComponent.SIDEBAR_WIDTH_KEY);
      const n = raw ? Number(raw) : NaN;
      if (Number.isFinite(n)) return Math.min(380, Math.max(180, n));
    } catch {
      /* ignore */
    }
    return 240;
  }

  private saveSidebarWidth(): void {
    try {
      localStorage.setItem(NdShellComponent.SIDEBAR_WIDTH_KEY, String(this.sidebarWidth));
    } catch {
      /* ignore */
    }
  }

  /** Overview is DB-heavy — do not poll /sessions/active while it loads. */
  private syncActiveSessionPolling(): void {
    if (this.isOverviewRoute()) {
      if (this.sessionsWatching) {
        this.activeSessions.unwatch();
        this.sessionsWatching = false;
      }
      return;
    }
    if (this.sessionsWatching) return;
    setTimeout(() => {
      if (this.isOverviewRoute() || this.sessionsWatching) return;
      this.activeSessions.watch();
      this.sessionsWatching = true;
    }, 8000);
  }

  isGroupExpanded(groupId: string): boolean {
    return this.expandedGroups.has(groupId);
  }

  toggleGroup(groupId: string, event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    if (this.expandedGroups.has(groupId)) this.expandedGroups.delete(groupId);
    else this.expandedGroups.add(groupId);
  }

  groupBadge(group: NavGroup): number | undefined {
    // Analysis group: total runs only — in-progress is a subset, not additive.
    if (group.id === 'analysis') {
      const n = this.navBadges['analysis-runs-all'] ?? 0;
      return group.children.some((c) => c.badgeAlways) ? n : n > 0 ? n : undefined;
    }
    let total = 0;
    let anyAlways = false;
    for (const child of group.children) {
      const n = this.badgeFor(child);
      if (n != null) {
        total += n;
        anyAlways = anyAlways || !!child.badgeAlways;
      }
    }
    if (anyAlways) return total;
    return total > 0 ? total : undefined;
  }

  groupHasActiveChild(group: NavGroup): boolean {
    return group.children.some((c) => this.isChildActive(c));
  }

  isChildActive(item: NavItem): boolean {
    const tree = this.router.parseUrl(this.router.url);
    const pathOnly = this.router.url.split('?')[0];

    if (item.queryParams) {
      if (pathOnly !== item.path && !pathOnly.startsWith(`${item.path}/`)) return false;
      return Object.entries(item.queryParams).every(
        ([k, v]) => String(tree.queryParams[k] ?? '') === v,
      );
    }

    // Exact path for document roots so /deleted does not light up the parent leaf.
    if (
      item.id === 'internal-documents' ||
      item.id === 'regulation-documents' ||
      item.id === 'analysis-runs-all'
    ) {
      if (pathOnly !== item.path) return false;
      // "All analysis" is inactive when filtering to correction queue.
      if (item.id === 'analysis-runs-all' && tree.queryParams['correction']) return false;
      return true;
    }

    return pathOnly === item.path || pathOnly.startsWith(`${item.path}/`);
  }

  private syncExpandedGroupsToRoute(): void {
    for (const entry of this.navEntries) {
      if (entry.kind !== 'group') continue;
      if (entry.group.children.some((c) => this.isChildActive(c))) {
        this.expandedGroups.add(entry.group.id);
      }
    }
  }

  private scheduleNavBadgeRefresh(): void {
    if (this.badgeRefreshTimer) clearTimeout(this.badgeRefreshTimer);
    const path = this.router.url.split('?')[0];
    // The first pass is near-immediate, otherwise the sidebar sits blank for seconds. The short
    // delay just lets the page issue its own requests first (the database is remote, so parallel
    // queries contend); cached badges are already on screen by then.
    const delayMs = !this.badgesLoadedOnce
      ? 500
      : this.isOverviewRoute()
        ? 15_000
        : path.includes('/nd/analysis-runs') || path.includes('/nd/in-progress')
          ? 0
          : path.includes('/nd/regulation-documents') || path.includes('/nd/internal-documents')
            ? 500
          : path.includes('/nd/analyse-regul') || path.includes('/nd/analyse-v')
            ? 2_000
            : 8_000;
    this.badgeRefreshTimer = setTimeout(() => {
      this.badgeRefreshTimer = null;
      void this.refreshNavBadges();
    }, delayMs);
  }

  private badgeCacheKey(role: string): string {
    return `${NdShellComponent.BADGE_CACHE_KEY}:${role}:${this.auth.isDemoViewer() ? 'demo' : 'real'}`;
  }

  /** Paint the previous session's numbers instantly while the fresh counts are in flight. */
  private hydrateBadgesFromCache(role: string): void {
    try {
      const raw = sessionStorage.getItem(this.badgeCacheKey(role));
      if (!raw) return;
      const parsed = JSON.parse(raw) as {
        badges?: Partial<Record<string, number>>;
        inProgress?: number;
      };
      if (parsed.badges) this.navBadges = parsed.badges;
      if (typeof parsed.inProgress === 'number') this.ndActiveRunCount = parsed.inProgress;
      this.cdr.markForCheck();
    } catch {
      /* ignore */
    }
  }

  private cacheBadges(role: string): void {
    try {
      sessionStorage.setItem(
        this.badgeCacheKey(role),
        JSON.stringify({ badges: this.navBadges, inProgress: this.ndActiveRunCount }),
      );
    } catch {
      /* ignore */
    }
  }

  private isOverviewRoute(): boolean {
    return this.router.url.split('?')[0].startsWith('/nd/overview');
  }

  badgeFor(item: NavItem): number | undefined {
    if (item.id === 'in-progress') {
      const n = this.ndActiveRunCount;
      if (item.badgeAlways) return n;
      return n > 0 ? n : undefined;
    }
    const n = this.navBadges[item.id] ?? 0;
    if (item.badgeAlways) return n;
    return n > 0 ? n : undefined;
  }

  private async refreshPass2LlmSummary(): Promise<void> {
    if (this.auth.isDemoViewer()) {
      this.pass2LlmSummary = '';
      return;
    }
    const res = await this.api.getActiveDualVerifyLlm();
    if (!res.success || !res.data) {
      this.pass2LlmSummary = '';
      return;
    }
    const { providerLabel, model } = res.data;
    this.pass2LlmSummary = `${providerLabel} · ${model}`;
  }

  private async refreshNavBadges(): Promise<void> {
    if (this.badgeRefreshInFlight) {
      this.badgeRefreshQueued = true;
      return;
    }
    this.badgeRefreshInFlight = true;
    const genAtStart = this.badgeApplyGeneration;
    try {
      const role = this.auth.getRole();
      if (!role) return;

      const res = await this.api.getWorkspaceNavCounts();
      if (genAtStart !== this.badgeApplyGeneration) return;
      if (this.badgeRefreshQueued) return;
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
        'admin-deleted-runs': c.deletedAnalysisRuns,
        inbox: c.inboxPending,
        'checker-queue': c.checkerQueue,
        'reviewer-queue': c.reviewerQueue,
      };
      this.ndActiveRunCount = c.analysisRunsInProgress;
      this.navBadges = next;
      this.badgesLoadedOnce = true;
      this.cacheBadges(role);
      this.cdr.markForCheck();
    } finally {
      this.badgeRefreshInFlight = false;
      if (this.badgeRefreshQueued) {
        this.badgeRefreshQueued = false;
        void this.refreshNavBadges();
      }
    }
  }

  private applyNavBadgeBumps(bumps: NdNavBadgeBumps): void {
    this.badgeApplyGeneration++;
    this.lastBadgeBumpMs = Date.now();
    const next = { ...this.navBadges };
    if (bumps.analysisRunsAll) {
      next['analysis-runs-all'] = Math.max(0, (next['analysis-runs-all'] ?? 0) + bumps.analysisRunsAll);
    }
    if (bumps.analysisRunsCorrection) {
      next['analysis-runs-correction'] = Math.max(
        0,
        (next['analysis-runs-correction'] ?? 0) + bumps.analysisRunsCorrection,
      );
    }
    if (bumps.adminDeletedRuns) {
      next['admin-deleted-runs'] = Math.max(0, (next['admin-deleted-runs'] ?? 0) + bumps.adminDeletedRuns);
    }
    if (bumps.internalDocuments) {
      next['internal-documents'] = Math.max(0, (next['internal-documents'] ?? 0) + bumps.internalDocuments);
    }
    if (bumps.regulationDocuments) {
      next['regulation-documents'] = Math.max(0, (next['regulation-documents'] ?? 0) + bumps.regulationDocuments);
    }
    if (bumps.internalDocumentsDeleted) {
      next['internal-documents-deleted'] = Math.max(
        0,
        (next['internal-documents-deleted'] ?? 0) + bumps.internalDocumentsDeleted,
      );
    }
    if (bumps.regulationDocumentsDeleted) {
      next['regulation-documents-deleted'] = Math.max(
        0,
        (next['regulation-documents-deleted'] ?? 0) + bumps.regulationDocumentsDeleted,
      );
    }
    this.navBadges = next;
    if (bumps.analysisRunsInProgress) {
      this.ndActiveRunCount = Math.max(0, this.ndActiveRunCount + bumps.analysisRunsInProgress);
    }
    this.cdr.markForCheck();
  }

  toggleSettings(): void {
    this.settingsOpen = !this.settingsOpen;
    if (this.settingsOpen) {
      void this.deployVersion.ensureLoaded().then(() => this.cdr.markForCheck());
    }
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

  private link(item: NavItem): NavEntry {
    return { kind: 'link', item };
  }

  private group(group: NavGroup): NavEntry {
    return { kind: 'group', group };
  }

  private documentsGroup(role: string): NavGroup {
    const children: NavItem[] = [
      { id: 'internal-documents', path: '/nd/internal-documents', label: 'Internal documents', icon: 'file' },
      { id: 'regulation-documents', path: '/nd/regulation-documents', label: 'Regulation docs', icon: 'library' },
      { id: 'libraries', path: '/nd/libraries', label: 'Regulation points library', icon: 'list' },
    ];
    if (role === 'super_admin') {
      children.push(
        {
          id: 'internal-documents-deleted',
          path: '/nd/internal-documents/deleted',
          label: 'Deleted internal docs',
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
    return { id: 'documents', label: 'Documents', icon: 'file', children };
  }

  private analysisGroup(role: string): NavGroup {
    const demo = this.auth.isDemoViewer();
    const children: NavItem[] = [
      { id: 'in-progress', path: '/nd/in-progress', label: 'In progress', icon: 'clock', badgeAlways: true },
      {
        id: 'analysis-runs-all',
        path: '/nd/analysis-runs',
        label: 'All analysis',
        icon: 'list',
        badgeAlways: true,
        ...(role === 'maker' && !this.auth.isDemoViewer() ? { queryParams: { mine: '1' } } : {}),
      },
    ];
    if (role === 'maker' || role === 'super_admin') {
      children.push({
        id: 'analyse-regul-full',
        path: '/nd/analyse-regul-full',
        label: 'New analysis',
        icon: 'plus',
        cta: true,
      });
      if (!demo) {
        children.push({
          id: 'analysis-versions',
          path: '/nd/analysis-versions',
          label: 'Analysis Version',
          icon: 'list',
          cta: true,
        });
      }
    }
    return { id: 'analysis', label: 'Analysis', icon: 'list', children };
  }

  private pendingReviewsGroup(role: string): NavGroup | null {
    const children: NavItem[] = [];
    if (role === 'maker' || role === 'super_admin' || role === 'checker' || role === 'reviewer') {
      children.push({
        id: 'analysis-runs-correction',
        path: '/nd/analysis-runs',
        label: 'Pending correction',
        icon: 'clock',
        badgeAlways: true,
        queryParams:
          role === 'maker' && !this.auth.isDemoViewer()
            ? { mine: '1', correction: '1' }
            : { correction: '1' },
      });
    }
    if (role === 'super_admin' || role === 'checker' || role === 'reviewer') {
      children.push({
        id: 'checker-queue',
        path: '/nd/checker',
        label: 'Pending review',
        icon: 'check',
        badgeAlways: true,
      });
    }
    if (role === 'super_admin' || role === 'reviewer') {
      children.push({
        id: 'reviewer-queue',
        path: '/nd/reviewer',
        label: 'Pending final review',
        icon: 'check',
        badgeAlways: true,
      });
    }
    if (!children.length) return null;
    return { id: 'pending-reviews', label: 'Pending reviews', icon: 'check', children };
  }

  private adminGroup(): NavGroup {
    const children: NavItem[] = [
      { id: 'admin-users', path: '/nd/admin/users', label: 'User management', icon: 'users' },
      { id: 'admin-departments', path: '/nd/admin/departments', label: 'Departments', icon: 'building' },
      { id: 'admin-settings', path: '/nd/admin/settings', label: 'Platform settings', icon: 'settings' },
    ];
    // Demo workspace tools are for real (non-demo) super admins only.
    if (!this.auth.isDemoViewer()) {
      children.push({ id: 'admin-demo', path: '/nd/admin/demo', label: 'Demo group', icon: 'users' });
    }
    children.push(
      { id: 'admin-prompts', path: '/nd/admin/prompts', label: 'Analysis prompts', icon: 'file' },
      {
        id: 'admin-deleted-runs',
        path: '/nd/admin/deleted-runs',
        label: 'Deleted analyses',
        icon: 'trash',
        badgeAlways: true,
      },
    );
    return {
      id: 'admin',
      label: 'Administration',
      icon: 'settings',
      children,
    };
  }

  private navForRole(role: string): NavEntry[] {
    const overview = this.link({ id: 'overview', path: '/nd/overview', label: 'Overview', icon: 'grid' });
    // Every role owns actions, so the inbox sits directly under Overview for all of them.
    const inbox = this.link({
      id: 'inbox',
      path: '/nd/inbox',
      label: 'My actions',
      icon: 'inbox',
      badgeAlways: true,
    });

    switch (role) {
      case 'super_admin': {
        const pending = this.pendingReviewsGroup(role);
        return [
          overview,
          inbox,
          this.group(this.documentsGroup(role)),
          this.group(this.analysisGroup(role)),
          ...(pending ? [this.group(pending)] : []),
          this.group(this.adminGroup()),
        ];
      }
      case 'maker': {
        const pending = this.pendingReviewsGroup(role);
        return [
          overview,
          inbox,
          this.group(this.documentsGroup(role)),
          this.group(this.analysisGroup(role)),
          ...(pending ? [this.group(pending)] : []),
        ];
      }
      case 'checker': {
        const pending = this.pendingReviewsGroup(role);
        return [
          overview,
          inbox,
          this.group({
            id: 'analysis',
            label: 'Analysis',
            icon: 'list',
            children: [
              {
                id: 'analysis-runs-all',
                path: '/nd/analysis-runs',
                label: 'All analysis',
                icon: 'list',
                badgeAlways: true,
              },
            ],
          }),
          ...(pending ? [this.group(pending)] : []),
        ];
      }
      case 'reviewer': {
        const pending = this.pendingReviewsGroup(role);
        return [
          overview,
          inbox,
          this.group({
            id: 'analysis',
            label: 'Analysis',
            icon: 'list',
            children: [
              {
                id: 'analysis-runs-all',
                path: '/nd/analysis-runs',
                label: 'All analysis',
                icon: 'list',
                badgeAlways: true,
              },
            ],
          }),
          ...(pending ? [this.group(pending)] : []),
        ];
      }
      default:
        return [overview, inbox];
    }
  }
}
