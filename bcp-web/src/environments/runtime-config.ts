import { resolveApiUrl } from './api-url';
import { environment } from './environment';

export interface RuntimeAppConfig {
  supabaseUrl?: string;
  supabaseAnonKey?: string;
  ndApiUrl?: string;
  appUrl?: string;
}

/** Overlay build-time environment with values from /assets/app-config.json (or Azure App Settings via server.js). */
export function applyRuntimeConfig(cfg: RuntimeAppConfig | null | undefined): void {
  if (!cfg) return;
  const url = cfg.supabaseUrl?.trim();
  const key = cfg.supabaseAnonKey?.trim();
  const ndApi = cfg.ndApiUrl?.trim();
  const app = cfg.appUrl?.trim();
  if (url) environment.supabaseUrl = url;
  if (key) environment.supabaseAnonKey = key;
  // app-config.json targets Azure; ng serve on localhost must keep using the local API.
  if (ndApi) {
    const isLocalHost =
      typeof window !== 'undefined'
      && (window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1');
    environment.ndApiUrl = isLocalHost ? resolveApiUrl() : ndApi;
  }
  if (app) environment.appUrl = app;
}

export function supabaseConfigured(): boolean {
  return Boolean(environment.supabaseUrl?.trim() && environment.supabaseAnonKey?.trim());
}
