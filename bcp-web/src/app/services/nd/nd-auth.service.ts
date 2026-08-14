import { Injectable, inject, signal } from '@angular/core';
import type { Session } from '@supabase/supabase-js';
import { environment } from '../../../environments/environment';
import { NdApiService, type NdUserProfile } from './nd-api.service';
import { getNdSupabaseClient } from './nd-supabase-client';

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
  private readonly api = inject(NdApiService);
  private readonly profileSignal = signal<NdUserProfile | null>(this.readCachedProfile());
  private refreshInFlight: Promise<NdUserProfile | null> | null = null;

  readonly profile = this.profileSignal.asReadonly();

  async getSession(): Promise<Session | null> {
    const { data } = await getNdSupabaseClient().auth.getSession();
    return data.session;
  }

  async getAccessToken(): Promise<string | null> {
    const session = await this.getSession();
    return session?.access_token ?? null;
  }

  getRole(): NdUserProfile['role'] | null {
    return this.profileSignal()?.role ?? null;
  }

  isDemoViewer(): boolean {
    return this.profileSignal()?.isDemo === true;
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

    const supabase = getNdSupabaseClient();
    const { data, error } = await supabase.auth.refreshSession();
    if (!error && data.session) {
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
        const { data } = await getNdSupabaseClient().auth.getUser();
        const sessionEmail = data.user?.email?.trim();
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
    const { data, error } = await getNdSupabaseClient().auth.signInWithPassword({
      email: email.trim(),
      password,
    });
    if (error) return error.message;

    const accessToken = data.session?.access_token;
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
    await getNdSupabaseClient().auth.signOut();
    this.profileSignal.set(null);
    this.writeCachedProfile(null);
  }

  async forgotPassword(email: string): Promise<{ error: string | null; resetLink?: string }> {
    const base =
      environment.appUrl ||
      (typeof window !== 'undefined' ? window.location.origin : '');
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
    const supabase = getNdSupabaseClient();

    if (typeof window !== 'undefined') {
      const url = new URL(window.location.href);
      const hashParams = new URLSearchParams(
        url.hash.startsWith('#') ? url.hash.slice(1) : url.hash,
      );

      const tokenHash = url.searchParams.get('token_hash') ?? hashParams.get('token_hash');
      if (tokenHash) {
        const otpType = (url.searchParams.get('type') ??
          hashParams.get('type') ??
          'recovery') as 'recovery' | 'invite' | 'signup' | 'magiclink' | 'email';
        const { error } = await supabase.auth.verifyOtp({ token_hash: tokenHash, type: otpType });
        if (error) return error.message;
        this.cleanAuthUrl(url);
        return null;
      }

      const accessToken = hashParams.get('access_token');
      const refreshToken = hashParams.get('refresh_token');
      if (accessToken && refreshToken) {
        const { error } = await supabase.auth.setSession({
          access_token: accessToken,
          refresh_token: refreshToken,
        });
        if (error) return error.message;
        this.cleanAuthUrl(url);
        return null;
      }

      const code = url.searchParams.get('code');
      if (code) {
        if (!this.hasPkceVerifier()) {
          return 'This reset link must be opened in the same browser where you requested it, or ask a super admin to set a new password from User Management.';
        }
        const { error } = await supabase.auth.exchangeCodeForSession(code);
        if (error) return error.message;
        this.cleanAuthUrl(url);
      }
    }

    const { data, error } = await supabase.auth.getSession();
    if (error) return error.message;
    if (!data.session) {
      return 'This link is invalid or has expired. Request a new reset email.';
    }
    return null;
  }

  private hasPkceVerifier(): boolean {
    if (typeof localStorage === 'undefined') return false;
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.includes('code-verifier')) return true;
    }
    return false;
  }

  private cleanAuthUrl(url: URL): void {
    ['code', 'token_hash', 'type'].forEach((key) => url.searchParams.delete(key));
    url.hash = '';
    const clean = `${url.pathname}${url.search}`;
    window.history.replaceState({}, '', clean);
  }

  async resetPassword(password: string): Promise<string | null> {
    if (!(await this.getSession())) {
      const sessionErr = await this.establishRecoverySession();
      if (sessionErr) return sessionErr;
    }

    const { error } = await getNdSupabaseClient().auth.updateUser({ password });
    if (error) return error.message;

    await this.signOut();
    return null;
  }

  async acceptInvite(fullName: string, password: string): Promise<string | null> {
    if (!(await this.getSession())) {
      const sessionErr = await this.establishRecoverySession();
      if (sessionErr) return sessionErr;
    }

    const { error } = await getNdSupabaseClient().auth.updateUser({ password });
    if (error) return error.message;
    await this.api.upsertProfile({ fullName: fullName.trim() });
    await this.refreshProfile();
    return null;
  }

  async isAuthenticated(): Promise<boolean> {
    return (await this.getSession()) !== null;
  }
}
