import { computed, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import { BrowserLocation } from '../browser/browser-location';
import { LiveConnection } from '../live/live-connection';
import { CacheRegistry } from '../state/cache-registry';
import { SKIP_FORBIDDEN_REDIRECT } from '../http/skip-forbidden';

/** What the gateway's GET /bff/user returns. */
export interface BffUser {
  userId: string;
  name: string | null;
  tenantId: string | null;
  /** The company's display name; null when Access could not be asked (show the tenant ID then). */
  tenantName?: string | null;
  /** The companies this user belongs to (more than one: the user menu offers "Switch company"). Absent from an older gateway. */
  tenants?: BffTenant[];
  roles: string[];
  permissions: string[];
}

/** A company the user belongs to. */
export interface BffTenant {
  id: string;
  name: string;
}

/** What the gateway's POST /bff/switch-tenant answers: the page to open next (a path on this site). */
interface SwitchAnswer {
  navigateTo: string;
}

/** The messages of a refused switch (the toast). */
export const SWITCH_REFUSED = 'You are not a member of that company any more.';
export const SWITCH_UNAVAILABLE = 'Switching company is not available right now. Please try again.';

/** Only a path on this site: no other host, no protocol-relative `//host` or `/\host`. */
function localPath(url: string | undefined): string {
  return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\') ? url : '/';
}

export interface AuthState {
  user: BffUser | null;
  /** True once /bff/user has been asked at least once (answered or failed). */
  loaded: boolean;
  /** True when the gateway could not be asked (5xx, network): we do not know who is signed in. */
  error: boolean;
  /** True while a company switch waits for the gateway (the menu does not start a second one). */
  switching: boolean;
  /** Why the last company switch was refused (shown as a toast until dismissed); null when there is nothing to say. */
  switchError: string | null;
}

/**
 * Who is signed in. The gateway holds the session (a cookie the script cannot read); this store only
 * knows what /bff/user says. Signing in and registering are full page loads to the gateway, which sends the
 * browser to Keycloak and back. These are the gateway's own BFF endpoints, not service APIs, so they are
 * called by hand and not through a generated client.
 */
export const AuthStore = signalStore(
  { providedIn: 'root' },
  withState<AuthState>({ user: null, loaded: false, error: false, switching: false, switchError: null }),
  withComputed(({ user }) => ({
    isLoggedIn: computed(() => user() !== null),
    permissions: computed(() => new Set(user()?.permissions ?? [])),
    tenants: computed<BffTenant[]>(() => user()?.tenants ?? []),
    /** The current company's name; its ID when the gateway could not name it. */
    tenantName: computed(() => user()?.tenantName?.trim() || user()?.tenantId || ''),
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

      /**
       * Moves to another company the user belongs to. The gateway is asked first (a POST with the XSRF header, which HttpClient adds):
       * when it refuses (not a member: 403; Access or Keycloak unavailable: 5xx), nothing here changes and `switchError` says why.
       * When it agrees, nothing of the current company may stay on screen or arrive afterwards: every cache is cleared and the live
       * connection stopped, then the browser opens the page the gateway named (a full page load). For a demo session the switch is
       * already done and that is the return url; for a Keycloak session it is the gateway's single-use URL that gets the token for that
       * company. The app starts again there, with a new connection.
       */
      async switchTenant(tenantId: string, returnUrl = '/app/dashboard'): Promise<void> {
        if (store.switching()) {
          return;
        }
        patchState(store, { switching: true, switchError: null });
        let navigateTo: string;
        try {
          const answer = await firstValueFrom(
            http.post<SwitchAnswer>(
              '/bff/switch-tenant',
              { tenant: tenantId, returnUrl },
              { context: new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true) },
            ),
          );
          navigateTo = localPath(answer?.navigateTo);
        } catch (error) {
          const refused = error instanceof HttpErrorResponse && error.status === 403;
          patchState(store, { switching: false, switchError: refused ? SWITCH_REFUSED : SWITCH_UNAVAILABLE });
          return;
        }

        caches.clearAll();
        try {
          await live.stop();
        } finally {
          // Even if the connection did not close cleanly: the page load that follows ends it anyway.
          location.assign(navigateTo);
        }
      },

      /** Hides the toast of a refused switch. */
      dismissSwitchError(): void {
        patchState(store, { switchError: null });
      },

      hasPermission(permission: string): boolean {
        return store.permissions().has(permission);
      },
    };
  }),
);
