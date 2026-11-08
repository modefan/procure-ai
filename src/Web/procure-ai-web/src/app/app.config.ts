import { Location } from '@angular/common';
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
import { devPanelInterceptor } from './core/dev-panel/dev-panel.interceptor';
import { provideLogger } from './core/logging/provide-logger';
import { AppConfigService } from './core/config/app-config';
import { startUp, SystemAvailability } from './core/system/system-availability';

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
      withInterceptors([credentialsInterceptor, correlationInterceptor, devPanelInterceptor, errorsInterceptor]),
    ),
    provideRouter(routes, withComponentInputBinding()),
    // Before the first page: is the demo switched on (after config.json)? Off: the maintenance page and nothing else. On: ask the gateway
    // who is signed in, so the header is right from the first paint. The live connection follows `isLoggedIn()` (LiveSession), so it also
    // starts when the user becomes known later (a failed first load that a guard retries).
    provideAppInitializer(() => {
      inject(LiveSession);
      return startUp({
        config: inject(AppConfigService),
        availability: inject(SystemAvailability),
        auth: inject(AuthStore),
        location: inject(Location),
      });
    }),
  ],
};
