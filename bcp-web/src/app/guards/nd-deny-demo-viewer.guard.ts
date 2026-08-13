import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { ND_NEW_ANALYSIS_PATH } from '../../lib/nd/demo-analysis-routes';
import { NdAuthService } from '../services/nd/nd-auth.service';

/** Redirect demo accounts away from routes that list legacy analysis versions. */
export const ndDenyDemoViewerGuard: CanActivateFn = async () => {
  const auth = inject(NdAuthService);
  const router = inject(Router);

  if (!auth.profile()) {
    await auth.refreshProfile(true);
  }
  if (auth.isDemoViewer()) {
    return router.createUrlTree([ND_NEW_ANALYSIS_PATH]);
  }
  return true;
};
