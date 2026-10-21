import { HttpInterceptorFn } from '@angular/common/http';
import { from, switchMap } from 'rxjs';
import { environment } from '../../environments/environment';
import { getNdSupabaseClient } from './nd/nd-supabase-client';

/** Pre-ND API routes (ApiService). The API now requires a signed-in user on them. */
const LEGACY_PREFIXES = ['/documents', '/landing-ai', '/dual-verify-kafka', '/bcpweb'];

function apiOrigins(): Set<string> {
  const origins = new Set<string>();
  for (const base of [environment.apiUrl, environment.ndApiUrl]) {
    try {
      if (base) origins.add(new URL(base).origin);
    } catch {
      /* ignore malformed base */
    }
  }
  return origins;
}

function isLegacyApiCall(url: string): boolean {
  try {
    const u = new URL(url, window.location.href);
    if (!apiOrigins().has(u.origin)) return false;
    return LEGACY_PREFIXES.some((p) => u.pathname === p || u.pathname.startsWith(`${p}/`));
  } catch {
    return false;
  }
}

/**
 * Adds the signed-in user's Supabase access token to ApiService calls, which never sent one. Only our own
 * API origin gets the token, and requests that already carry an Authorization header are left alone.
 */
export const legacyApiAuthInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.headers.has('Authorization') || !isLegacyApiCall(req.url)) return next(req);

  let session: ReturnType<ReturnType<typeof getNdSupabaseClient>['auth']['getSession']>;
  try {
    session = getNdSupabaseClient().auth.getSession();
  } catch {
    return next(req); // Supabase not configured: send as before, the API answers 401.
  }

  return from(session).pipe(
    switchMap(({ data }) => {
      const token = data.session?.access_token;
      return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
    }),
  );
};
