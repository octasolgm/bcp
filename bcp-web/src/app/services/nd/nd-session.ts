/**
 * Session store for the ND app.
 *
 * The browser never talks to the identity provider. Sign-in, refresh, sign-out, password changes and
 * reset-link verification all go to our own API (/nd/auth/...), which holds the provider keys and makes
 * those calls server side. This file only keeps the tokens the API hands back and puts a fresh access
 * token on outgoing API requests.
 */
import { environment } from '../../../environments/environment';

const SESSION_KEY = 'cs-nd-session';
/** Refresh this far before the token actually expires, so an in-flight request never lands expired. */
const REFRESH_SKEW_SECONDS = 60;

export interface NdSession {
  accessToken: string;
  refreshToken: string | null;
  /** Epoch seconds, when the API reported one. */
  expiresAt: number | null;
  userId: string | null;
  email: string | null;
}

export interface NdSessionPayload {
  accessToken?: string | null;
  refreshToken?: string | null;
  expiresAt?: number | null;
  expiresIn?: number | null;
  userId?: string | null;
  email?: string | null;
}

let refreshInFlight: Promise<NdSession | null> | null = null;

function apiBaseUrl(): string {
  const url = environment.ndApiUrl || environment.apiUrl;
  if (!url) throw new Error('ND API URL is not configured');
  return url.replace(/\/$/, '');
}

export function normalizeNdSession(payload: NdSessionPayload | null | undefined): NdSession | null {
  const accessToken = payload?.accessToken?.trim();
  if (!accessToken) return null;

  let expiresAt = typeof payload?.expiresAt === 'number' ? payload.expiresAt : null;
  if (!expiresAt && typeof payload?.expiresIn === 'number') {
    expiresAt = Math.floor(Date.now() / 1000) + payload.expiresIn;
  }

  return {
    accessToken,
    refreshToken: payload?.refreshToken?.trim() || null,
    expiresAt,
    userId: payload?.userId ?? null,
    email: payload?.email ?? null,
  };
}

export function getNdSession(): NdSession | null {
  if (typeof localStorage === 'undefined') return null;
  try {
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    return normalizeNdSession(JSON.parse(raw) as NdSessionPayload);
  } catch {
    return null;
  }
}

export function setNdSession(payload: NdSessionPayload | null): NdSession | null {
  const session = normalizeNdSession(payload);
  if (typeof localStorage === 'undefined') return session;
  try {
    if (!session) localStorage.removeItem(SESSION_KEY);
    else localStorage.setItem(SESSION_KEY, JSON.stringify(session));
  } catch {
    // private mode / quota: the session simply does not survive a reload
  }
  return session;
}

export function clearNdSession(): void {
  setNdSession(null);
}

function isExpiring(session: NdSession): boolean {
  if (!session.expiresAt) return false;
  return session.expiresAt - REFRESH_SKEW_SECONDS <= Math.floor(Date.now() / 1000);
}

/** Swap the refresh token for a new access token, through our API. */
export async function refreshNdSession(): Promise<NdSession | null> {
  if (refreshInFlight) return refreshInFlight;

  const current = getNdSession();
  if (!current?.refreshToken) return null;

  refreshInFlight = (async () => {
    try {
      const res = await fetch(`${apiBaseUrl()}/nd/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: current.refreshToken }),
      });
      const body = (await res.json().catch(() => null)) as
        | { success?: boolean; data?: NdSessionPayload }
        | null;
      if (!res.ok || !body?.success || !body.data?.accessToken) {
        if (res.status === 401) clearNdSession();
        return null;
      }
      // The provider may not rotate the refresh token: keep the old one when none comes back.
      return setNdSession({
        ...body.data,
        refreshToken: body.data.refreshToken ?? current.refreshToken,
      });
    } catch {
      return null;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

/** A usable access token, refreshed first when it is about to expire. */
export async function getNdAccessToken(): Promise<string | null> {
  const session = getNdSession();
  if (!session) return null;
  if (!isExpiring(session)) return session.accessToken;
  const refreshed = await refreshNdSession();
  return refreshed?.accessToken ?? null;
}
