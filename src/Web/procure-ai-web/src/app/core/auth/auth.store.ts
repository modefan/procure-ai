import { computed, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import { BrowserLocation } from '../browser/browser-location';

/** What the gateway's GET /bff/user returns. */
export interface BffUser {
  userId: string;
  name: string | null;
  tenantId: string | null;
  roles: string[];
  permissions: string[];
}

export interface AuthState {
  user: BffUser | null;
  /** True once /bff/user has been asked at least once (answered or failed). */
  loaded: boolean;
}

/**
 * Who is signed in. The gateway holds the session (a cookie the script cannot read); this store only
 * knows what /bff/user says. Signing in and registering are full page loads to the gateway, which sends the
 * browser to Keycloak and back. These are the gateway's own BFF endpoints, not service APIs, so they are
 * called by hand and not through a generated client.
 */
export const AuthStore = signalStore(
  { providedIn: 'root' },
  withState<AuthState>({ user: null, loaded: false }),
  withComputed(({ user }) => ({
    isLoggedIn: computed(() => user() !== null),
    permissions: computed(() => new Set(user()?.permissions ?? [])),
  })),
  withMethods((store) => {
    const http = inject(HttpClient);
    const location = inject(BrowserLocation);
    const router = inject(Router);
    let inFlight: Promise<void> | null = null;

    const bffUrl = (action: 'login' | 'register', returnUrl: string) =>
      `/bff/${action}?returnUrl=${encodeURIComponent(returnUrl)}`;

    return {
      /**
       * Asks the gateway who is signed in (once; later calls return at once, concurrent calls share one request).
       * A 401, or any failure, means nobody: the app then shows its signed-out face.
       */
      load(): Promise<void> {
        if (store.loaded()) {
          return Promise.resolve();
        }
        inFlight ??= (async () => {
          try {
            const user = await firstValueFrom(http.get<BffUser>('/bff/user'));
            patchState(store, { user, loaded: true });
          } catch {
            patchState(store, { user: null, loaded: true });
          } finally {
            inFlight = null;
          }
        })();
        return inFlight;
      },

      login(returnUrl: string): void {
        location.assign(bffUrl('login', returnUrl));
      },

      register(returnUrl: string): void {
        location.assign(bffUrl('register', returnUrl));
      },

      /** Ends the session at the gateway (and Keycloak), forgets the user and goes to the welcome page. */
      async logout(): Promise<void> {
        try {
          await firstValueFrom(http.post('/bff/logout', null));
        } finally {
          // Even if the call failed (the session was already gone), this browser is signed out as far as the app goes.
          patchState(store, { user: null });
          await router.navigateByUrl('/');
        }
      },

      hasPermission(permission: string): boolean {
        return store.permissions().has(permission);
      },
    };
  }),
);
