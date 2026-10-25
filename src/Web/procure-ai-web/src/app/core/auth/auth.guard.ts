import { inject } from '@angular/core';
import { CanMatchFn, Router } from '@angular/router';
import { AuthStore } from './auth.store';

/**
 * Guards the lazy `app` routes with canMatch: when it says no, the app code is not even downloaded.
 * Waits for /bff/user, then lets a signed-in user through; anyone else is sent to the gateway's login, which
 * brings them back to the page they asked for. (UX only: the APIs check the token again.)
 */
export const authGuard: CanMatchFn = async (_route, segments) => {
  const auth = inject(AuthStore);
  const router = inject(Router);

  await auth.load();
  if (auth.isLoggedIn()) {
    return true;
  }

  // The full requested url, with its query string, while a navigation is running; the matched segments otherwise.
  const navigation = router.currentNavigation();
  const requested = navigation ? router.serializeUrl(navigation.extractedUrl) : '/' + segments.map((s) => s.path).join('/');
  auth.login(requested);
  return false;
};
