import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'reguliq-auth-session';
const DEMO_USER = 'admin';
const DEMO_PASS = '123456';

export type AuthUser = {
  username: string;
  loggedInAt: number;
};

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly user = signal<AuthUser | null>(this.readSession());

  readonly currentUser = this.user.asReadonly();

  isAuthenticated(): boolean {
    return this.user() !== null;
  }

  login(username: string, password: string): boolean {
    const u = username.trim();
    const p = password;
    if (u !== DEMO_USER || p !== DEMO_PASS) return false;

    const session: AuthUser = { username: u, loggedInAt: Date.now() };
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    this.user.set(session);
    return true;
  }

  logout(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    this.user.set(null);
  }

  private readSession(): AuthUser | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as AuthUser;
      return parsed?.username ? parsed : null;
    } catch {
      return null;
    }
  }
}
