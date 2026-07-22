import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { NdAuthService } from '../services/nd/nd-auth.service';

export const ndAuthGuard: CanActivateFn = async () => {
  const auth = inject(NdAuthService);
  const router = inject(Router);
  if (!(await auth.isAuthenticated())) {
    return router.createUrlTree(['/nd/auth/login']);
  }
  if (auth.profile()) {
    return true;
  }
  const profile = await auth.refreshProfile();
  if (!profile) {
    return router.createUrlTree(['/nd/auth/login']);
  }
  return true;
};

export const ndGuestGuard: CanActivateFn = async () => {
  const auth = inject(NdAuthService);
  const router = inject(Router);
  if (await auth.isAuthenticated()) {
    return router.createUrlTree(['/nd/overview']);
  }
  return true;
};
