import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { NdAuthService } from '../services/nd/nd-auth.service';

export const ndRoleGuard: CanActivateFn = async (route) => {
  const auth = inject(NdAuthService);
  const router = inject(Router);
  const roles = route.data['ndRoles'] as string[] | undefined;
  if (!roles?.length) return true;

  await auth.refreshProfile();
  const role = auth.getRole();
  if (role && (roles.includes(role) || role === 'super_admin')) return true;

  return router.createUrlTree(['/nd/overview']);
};
