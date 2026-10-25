import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

/**
 * Needs `data: { permission: 'requests.read' }` on the route. It sits below `authGuard` (which has loaded the
 * user), so it answers at once. A route that names no permission is a mistake, and fails closed.
 */
export const permissionGuard: CanActivateFn = (route) => {
  const permission = route.data['permission'];
  if (typeof permission === 'string' && inject(AuthStore).hasPermission(permission)) {
    return true;
  }
  return inject(Router).createUrlTree(['/forbidden']);
};
