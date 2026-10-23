import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { environment } from '../../environments/environment';
import { ND_NEW_ANALYSIS_PATH } from '../../lib/nd/demo-analysis-routes';

/** Client installs (app-config.json edition "client") ship only the live New analysis and Azure document pages:
 *  document pages of the other parse engines go to the Azure page of the same kind, older analysis versions and
 *  the legacy /old pages go to New analysis. */
export const ndDenyClientEditionGuard: CanActivateFn = (route) => {
  if (!environment.clientEdition) return true;
  const router = inject(Router);
  const path = route.routeConfig?.path ?? '';
  if (path.startsWith('internal-documents')) return router.createUrlTree(['/nd/internal-documents-azure-di']);
  if (path.startsWith('regulation-documents')) return router.createUrlTree(['/nd/regulation-documents-azure-di']);
  return router.createUrlTree([ND_NEW_ANALYSIS_PATH]);
};
