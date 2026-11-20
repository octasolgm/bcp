import { Injectable, inject, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { NdApiService, type NdUserProfile } from './nd-api.service';
import {
  clearNdSession,
  getNdAccessToken,
  getNdSession,
  refreshNdSession,
  setNdSession,
  type NdSession,
} from './nd-session';

const PROFILE_CACHE_KEY = 'reguliq-nd-profile-cache';

function normalizeNdProfile(profile: NdUserProfile): NdUserProfile {
  const email = (profile.email ?? '').toLowerCase();
  const name = (profile.fullName ?? '').toLowerCase();
  const isDemo =
    profile.isDemo === true || name.includes('demo') || email.includes('demo');
  return { ...profile, isDemo };
}

@Injectable({ providedIn: 'root' })
export class NdAuthService {
  /** Every pre-workspace row and every demo account lives here. */
  static readonly DEFAULT_WORKSPACE_ID = '00000000-0000-0000-0000-000000000001';

  private readonly api = inject(NdApiService);
  private readonly profileSignal = signal<NdUserProfile | null>(this.readCachedProfile());
  private refreshInFlight: Promise<NdUserProfile | null> | null = null;

  readonly profile = this.profileSignal.asReadonly();

  async getSession(): Promise<NdSession | null> {
    return getNdSession();
  }

  async getAccessToken(): Promise<string | null> {
    return getNdAccessToken();
  }

  getRole(): NdUserProfile['role'] | null {
    return this.profileSignal()?.role ?? null;
  }

  isDemoViewer(): boolean {
    return this.profileSignal()?.isDemo === true;
  }

  /** Platform super admin (all workspaces). A workspace admin has role super_admin but not this flag. */
  isPlatformAdmin(): boolean {
    const p = this.profileSignal();
    return p?.role === 'super_admin' && p.isPlatformAdmin === true;
  }

  /**
   * The platform's own Default workspace. The pre-workspace endpoints (/dual-verify-kafka, /documents,
   * /bcpweb) read tables that carry no workspace of their own, so they answer 403 anywhere else and
   * must not be called from a client business.
   */
  isDefaultWorkspace(): boolean {
    const p = this.profileSignal();
    if (!p) return false;
    return (p.workspace?.id ?? p.workspaceId) === NdAuthService.DEFAULT_WORKSPACE_ID;
  }

  /** Platform super admin working in the Default workspace (not inside a client business). Internal
   *  tools such as Evals are shown only here. */
  isPlatformAdminInDefaultWorkspace(): boolean {
    return this.isPlatformAdmin() && !this.isDemoViewer() && this.isDefaultWorkspace();
  }

  /** Workspace management is platform-only and never shown to demo accounts. */
  canManageWorkspaces(): boolean {
    return this.isPlatformAdmin() && !this.isDemoViewer();
  }

  /** "Super admin" for the platform owner, "Admin" for a workspace's own administrator. */
  roleLabel(role: string | null | undefined = this.getRole()): string {
    switch (role) {
      case 'super_admin':
        return this.isPlatformAdmin() ? 'Super admin' : 'Admin';
      case 'maker':
        return 'Maker';
      case 'checker':
        return 'Checker';
      case 'reviewer':
        return 'Reviewer';
      default:
        return role ?? '';
    }
  }

  /** Demo tenant admin (super_admin role, or profile name containing "admin"). */
  isDemoAdmin(): boolean {
    if (!this.isDemoViewer()) return false;
    if (this.getRole() === 'super_admin') return true;
    const name = (this.profileSignal()?.fullName ?? '').toLowerCase();
    return name.includes('admin');
  }

  async refreshProfile(force = false): Promise<NdUserProfile | null> {
    if (!force && this.profileSignal()) {
      return this.profileSignal();
    }
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }

    this.refreshInFlight = this.doRefreshProfile();
    try {
      return await this.refreshInFlight;
    } finally {
      this.refreshInFlight = null;
    }
  }

  private async doRefreshProfile(): Promise<NdUserProfile | null> {
    const profile = await this.loadProfile();
    if (profile) return profile;

    const refreshed = await refreshNdSession();
    if (refreshed) {
      const retried = await this.loadProfile();
      if (retried) return retried;
    }

    await this.signOut();
    return null;
  }

  private readCachedProfile(): NdUserProfile | null {
    if (typeof sessionStorage === 'undefined') return null;
    try {
      const raw = sessionStorage.getItem(PROFILE_CACHE_KEY);
      if (!raw) return null;
      return normalizeNdProfile(JSON.parse(raw) as NdUserProfile);
    } catch {
      return null;
    }
  }

  private writeCachedProfile(profile: NdUserProfile | null): void {
    if (typeof sessionStorage === 'undefined') return;
    try {
      if (!profile) {
        sessionStorage.removeItem(PROFILE_CACHE_KEY);
        return;
      }
      sessionStorage.setItem(PROFILE_CACHE_KEY, JSON.stringify(profile));
    } catch {
      // ignore quota errors
    }
  }

  private async loadProfile(): Promise<NdUserProfile | null> {
    const res = await this.api.getProfile();
    if (res.success && res.data) {
      let profile = res.data;
      if (!profile.email) {
        const sessionEmail = getNdSession()?.email?.trim();
        if (sessionEmail) {
          profile = { ...profile, email: sessionEmail };
        }
      }
      const normalized = normalizeNdProfile(profile);
      this.profileSignal.set(normalized);
      this.writeCachedProfile(normalized);
      return normalized;
    }
    this.profileSignal.set(null);
    this.writeCachedProfile(null);
    return null;
  }

  async signIn(email: string, password: string): Promise<string | null> {
    const res = await this.api.login(email.trim(), password);
    if (!res.success || !res.data) {
      return res.message ?? 'Invalid email or password.';
    }

    const session = setNdSession(res.data);
    const accessToken = session?.accessToken;
    if (!accessToken) {
      return 'Sign-in succeeded but no session token was returned. Try again.';
    }

    const profileRes = await this.api.getProfile(accessToken);
    if (!profileRes.success || !profileRes.data) {
      await this.signOut();
      const msg = profileRes.message ?? 'Could not load profile after sign in';
      if (msg.includes('timed out') || msg.includes('temporarily unreachable')) {
        return (
          'Sign-in succeeded but the database is slow or busy. ' +
          'Wait 1 minute with only one API window open, then try again. ' +
          'To cancel stuck runs without logging in: bcp-api\\scripts\\stop-stuck-runs.ps1'
        );
      }
      return msg;
    }
    if (!profileRes.data.isActive) {
      await this.signOut();
      return 'Account deactivated';
    }
    const profile = normalizeNdProfile(profileRes.data);
    this.profileSignal.set(profile);
    this.writeCachedProfile(profile);
    return null;
  }

  async signOut(): Promise<void> {
    try {
      if (getNdSession()) await this.api.logout();
    } catch {
      // the local session is cleared either way
    }
    clearNdSession();
    this.profileSignal.set(null);
    this.writeCachedProfile(null);
  }

  async forgotPassword(email: string): Promise<{ error: string | null; resetLink?: string }> {
    // The site the user is on, so the reset link brings them back to the same address.
    const base =
      (typeof window !== 'undefined' ? window.location.origin : '') ||
      environment.appUrl;
    if (!base) return { error: 'App URL is not configured' };

    const res = await this.api.forgotPassword(email.trim(), base.replace(/\/$/, ''));
    if (!res.success) {
      const msg = res.message ?? 'Request failed';
      if (msg.toLowerCase().includes('rate limit')) {
        return {
          error: 'Too many reset emails were requested. Wait about an hour, then try again.',
        };
      }
      return { error: msg };
    }
    return { error: null, resetLink: res.data?.resetLink };
  }

  /** Parse recovery / invite tokens from the email link and establish a session. */
  async establishRecoverySession(): Promise<string | null> {
    if (typeof window !== 'undefined') {
      const url = new URL(window.location.href);
      const hashParams = new URLSearchParams(
        url.hash.startsWith('#') ? url.hash.slice(1) : url.hash,
      );

      const tokenHash = url.searchParams.get('token_hash') ?? hashParams.get('token_hash');
      if (tokenHash) {
        const otpType = url.searchParams.get('type') ?? hashParams.get('type') ?? 'recovery';
        const res = await this.api.verifyOtp(tokenHash, otpType);
        if (!res.success || !res.data) {
          return res.message ?? 'This link is invalid or has expired. Request a new reset email.';
        }
        setNdSession(res.data);
        this.cleanAuthUrl(url);
        return null;
      }

      const accessToken = hashParams.get('access_token');
      const refreshToken = hashParams.get('refresh_token');
      if (accessToken && refreshToken) {
        const expiresAt = Number(hashParams.get('expires_at'));
        setNdSession({
          accessToken,
          refreshToken,
          expiresAt: Number.isFinite(expiresAt) && expiresAt > 0 ? expiresAt : null,
        });
        this.cleanAuthUrl(url);
        return null;
      }

      if (url.searchParams.get('code')) {
        return 'This reset link is in an old format. Request a new reset email, or ask a super admin to set a new password from User Management.';
      }
    }

    if (!getNdSession()) {
      return 'This link is invalid or has expired. Request a new reset email.';
    }
    return null;
  }

  private cleanAuthUrl(url: URL): void {
    ['code', 'token_hash', 'type'].forEach((key) => url.searchParams.delete(key));
    url.hash = '';
    const clean = `${url.pathname}${url.search}`;
    window.history.replaceState({}, '', clean);
  }

  /** Signed-in user updates their own password (profile page). Does not sign out. */
  async changePassword(password: string): Promise<string | null> {
    if (!(await this.getSession())) {
      return 'You are not signed in.';
    }
    const res = await this.api.changePassword(password);
    if (!res.success) return res.message ?? 'Could not update the password.';
    return null;
  }

  async resetPassword(password: string): Promise<string | null> {
    if (!(await this.getSession())) {
      const sessionErr = await this.establishRecoverySession();
      if (sessionErr) return sessionErr;
    }

    const res = await this.api.changePassword(password);
    if (!res.success) return res.message ?? 'Could not update the password.';

    await this.signOut();
    return null;
  }

  async acceptInvite(fullName: string, password: string): Promise<string | null> {
    if (!(await this.getSession())) {
      const sessionErr = await this.establishRecoverySession();
      if (sessionErr) return sessionErr;
    }

    const res = await this.api.changePassword(password);
    if (!res.success) return res.message ?? 'Could not update the password.';
    await this.api.upsertProfile({ fullName: fullName.trim() });
    await this.refreshProfile();
    return null;
  }

  async isAuthenticated(): Promise<boolean> {
    return (await this.getSession()) !== null;
  }
}
