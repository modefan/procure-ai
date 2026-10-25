import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

/** For /login and /register: someone who is already signed in goes to the dashboard instead. */
export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthStore);
  const router = inject(Router);

  await auth.load();
  if (auth.error()) {
    return router.createUrlTree(['/unavailable']);
  }
  return auth.isLoggedIn() ? router.createUrlTree(['/app/dashboard']) : true;
};
