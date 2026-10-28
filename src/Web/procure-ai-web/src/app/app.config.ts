import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { AuthStore } from './core/auth/auth.store';
import { LiveConnection } from './core/live/live-connection';
import { provideAppConfig } from './core/config/app-config';
import { credentialsInterceptor } from './core/http/credentials.interceptor';
import { correlationInterceptor } from './core/http/correlation.interceptor';
import { errorsInterceptor } from './core/http/errors.interceptor';
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
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
      withInterceptors([credentialsInterceptor, correlationInterceptor, errorsInterceptor]),
    ),
    provideRouter(routes),
    // Ask the gateway who is signed in before the first page, so the header is right from the first paint, then open
    // the live connection for a signed-in user (not awaited: the page must not wait for the socket).
    provideAppInitializer(async () => {
      const auth = inject(AuthStore);
      const live = inject(LiveConnection);
      await auth.load();
      if (auth.isLoggedIn()) {
        live.start();
      }
    }),
  ],
};
