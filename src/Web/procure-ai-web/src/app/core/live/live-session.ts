import { effect, inject, Service } from '@angular/core';
import { AuthStore } from '../auth/auth.store';
import { SystemAvailability } from '../system/system-availability';
import { LiveConnection } from './live-connection';

/**
 * Keeps the live connection in step with who is signed in: opens it when a user is known (at boot, or later when a
 * failed first `/bff/user` call is retried by a guard) and closes it when nobody is, or when the demo is switched off (the gateway refuses
 * the hub then). Logout also stops it explicitly.
 */
@Service()
export class LiveSession {
  constructor() {
    const auth = inject(AuthStore);
    const live = inject(LiveConnection);
    const availability = inject(SystemAvailability);
    effect(() => {
      if (auth.isLoggedIn() && !availability.disabled()) {
        live.start();
      } else {
        void live.stop();
      }
    });
  }
}
