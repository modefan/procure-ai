import { effect, inject, Service } from '@angular/core';
import { AuthStore } from '../auth/auth.store';
import { LiveConnection } from './live-connection';

/**
 * Keeps the live connection in step with who is signed in: opens it when a user is known (at boot, or later when a
 * failed first `/bff/user` call is retried by a guard) and closes it when nobody is. Logout also stops it explicitly.
 */
@Service()
export class LiveSession {
  constructor() {
    const auth = inject(AuthStore);
    const live = inject(LiveConnection);
    effect(() => {
      if (auth.isLoggedIn()) {
        live.start();
      } else {
        void live.stop();
      }
    });
  }
}
