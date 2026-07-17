import { Router } from '@angular/router';

/** `/nd` when the app is under the new dashboard shell, otherwise ``. */
export function appShellPrefix(router: Router): string {
  const url = router.url;
  return url.startsWith('/nd') ? '/nd' : '';
}

export function shellRoute(router: Router, path: string): string {
  const normalized = path.startsWith('/') ? path : `/${path}`;
  const prefix = appShellPrefix(router);
  return prefix ? `${prefix}${normalized}` : normalized;
}

export function shellRouteSegments(router: Router, path: string): string[] {
  return [shellRoute(router, path)];
}
