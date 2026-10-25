import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

/**
 * Behind /login and /register (the route's `data.action` says which): hands the browser to the gateway, which sends it to Keycloak. A visitor who is
 * already signed in never gets here (guestGuard). `?returnUrl=` says where to land afterwards.
 */
@Component({
  selector: 'app-auth-redirect-page',
  template: `
    <section class="mx-auto max-w-xl px-4 py-24 text-center" aria-live="polite">
      <p class="text-slate-600">Taking you to the sign-in page...</p>
    </section>
  `,
})
export class AuthRedirectPage {
  constructor() {
    const auth = inject(AuthStore);
    const route = inject(ActivatedRoute).snapshot;
    const returnUrl = route.queryParamMap.get('returnUrl') ?? '/app/dashboard';
    if (route.data['action'] === 'register') {
      auth.register(returnUrl);
    } else {
      auth.login(returnUrl);
    }
  }
}
