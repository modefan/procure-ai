import { computed, inject, Service } from '@angular/core';
import { AuthStore } from '../auth/auth.store';
import { SystemAvailability } from '../system/system-availability';

/**
 * One rule for the whole frame: a session is there when somebody is signed in and the demo is switched on. Only then do the menu's signed-in
 * pages, the session menu and everything that calls /bff, /api or the hubs for a user exist; while the demo is off the frame shows the public
 * menu only.
 */
@Service()
export class SessionState {
  private readonly auth = inject(AuthStore);
  private readonly availability = inject(SystemAvailability);

  readonly active = computed(() => this.auth.isLoggedIn() && !this.availability.disabled());
}
