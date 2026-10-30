import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { AuthStore } from './core/auth/auth.store';
import { LiveSession } from './core/live/live-session';
import { provideAppConfig } from './core/config/app-config';
import { credentialsInterceptor } from './core/http/credentials.interceptor';
import { correlationInterceptor } from './core/http/correlation.interceptor';
import { errorsInterceptor } from './core/http/errors.interceptor';
import { XSRF_COOKIE_NAME, XSRF_HEADER_NAME } from './core/http/xsrf';
import { provideLogger } from './core/logging/provide-logger';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Order matters: the config is loaded first, the logger and everything after read it.
    provideAppConfig(),
    provideLogger(),
    provideHttpClient(
      // The gateway's rule: unsafe methods carry X-XSRF-TOKEN, copied from the XSRF-TOKEN cookie. Angular does this
      // for relative (same-origin) urls only, which is all this app calls.
      withXsrfConfiguration({ cookieName: XSRF_COOKIE_NAME, headerName: XSRF_HEADER_NAME }),
      withInterceptors([credentialsInterceptor, correlationInterceptor, errorsInterceptor]),
    ),
    provideRouter(routes, withComponentInputBinding()),
    // Ask the gateway who is signed in before the first page, so the header is right from the first paint. The live
    // connection follows `isLoggedIn()` (LiveSession), so it also starts when the user becomes known later (a failed
    // first load that a guard retries).
    provideAppInitializer(() => {
      inject(LiveSession);
      return inject(AuthStore).load();
    }),
  ],
};
