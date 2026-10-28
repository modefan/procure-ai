import { computed, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import { BrowserLocation } from '../browser/browser-location';
import { LiveConnection } from '../live/live-connection';
import { CacheRegistry } from '../state/cache-registry';

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
  /** True when the gateway could not be asked (5xx, network): we do not know who is signed in. */
  error: boolean;
}

/**
 * Who is signed in. The gateway holds the session (a cookie the script cannot read); this store only
 * knows what /bff/user says. Signing in and registering are full page loads to the gateway, which sends the
 * browser to Keycloak and back. These are the gateway's own BFF endpoints, not service APIs, so they are
 * called by hand and not through a generated client.
 */
export const AuthStore = signalStore(
  { providedIn: 'root' },
  withState<AuthState>({ user: null, loaded: false, error: false }),
  withComputed(({ user }) => ({
    isLoggedIn: computed(() => user() !== null),
    permissions: computed(() => new Set(user()?.permissions ?? [])),
  })),
  withMethods((store) => {
    const http = inject(HttpClient);
    const location = inject(BrowserLocation);
    const router = inject(Router);
    const caches = inject(CacheRegistry);
    const live = inject(LiveConnection);
    let inFlight: Promise<void> | null = null;

    const bffUrl = (action: 'login' | 'register', returnUrl: string) =>
      `/bff/${action}?returnUrl=${encodeURIComponent(returnUrl)}`;

    /** Closes the live connection first, so nothing arrives for the old user, then empties every store. */
    const forget = () => {
      void live.stop();
      patchState(store, { user: null });
      caches.clearAll();
    };

    const signOut = async (url: string): Promise<void> => {
      try {
        await firstValueFrom(http.post(url, null));
      } finally {
        // Even if the call failed (the session was already gone), this browser is signed out as far as the app goes.
        forget();
        await router.navigateByUrl('/');
      }
    };

    return {
      /**
       * Asks the gateway who is signed in (once; later calls return at once, concurrent calls share one request).
       * Only a 401 means nobody is signed in. Any other failure (server error, network) means we do not know:
       * `error` is set, `loaded` stays false so a later call asks again, and the guards show the unavailable page
       * instead of sending the browser to log in (which, with a live Keycloak session, would loop).
       */
      load(): Promise<void> {
        if (store.loaded()) {
          return Promise.resolve();
        }
        inFlight ??= (async () => {
          try {
            const user = await firstValueFrom(http.get<BffUser>('/bff/user'));
            patchState(store, { user, loaded: true, error: false });
          } catch (error) {
            if (error instanceof HttpErrorResponse && error.status === 401) {
              patchState(store, { user: null, loaded: true, error: false });
            } else {
              patchState(store, { user: null, loaded: false, error: true });
            }
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
      logout(): Promise<void> {
        return signOut('/bff/logout');
      },

      /** Ends every session of this user (all browsers), then like `logout()`. */
      logoutAll(): Promise<void> {
        return signOut('/bff/logout-all');
      },

      /** The session is over (the user declined to log in again): stop the live connection, forget the user and everything the stores hold. */
      endSession(): void {
        forget();
      },

      hasPermission(permission: string): boolean {
        return store.permissions().has(permission);
      },
    };
  }),
);
