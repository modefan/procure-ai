import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthStore } from '../auth/auth.store';
import { DEMO_LOGIN_USERS_URL } from '../auth/demo-login';
import { SessionExpired } from '../auth/session-expired';
import { AppConfigService } from '../config/app-config';
import { Logger } from '../logging/logger';
import { CORRELATION_HEADER } from './correlation.interceptor';
import { isOwnRequest } from './own-requests';

/**
 * Set on a call whose 403 is part of the page's own story (a secondary section the user may not read, a refused write):
 * the caller handles the error and the page stays where it is, instead of the forbidden page taking over.
 */
export const SKIP_FORBIDDEN_REDIRECT = new HttpContextToken<boolean>(() => false);

/**
 * What every failed call to the gateway does besides failing:
 * - it is logged (5xx and network errors as errors, other statuses as warnings);
 * - 401 while signed in: the session ended, so the session-expired dialog opens (the page is kept as the return url);
 * - 403: the forbidden page (unless the call sets `SKIP_FORBIDDEN_REDIRECT`).
 * The caller still gets the error. /bff/user answering 401 is the normal "nobody is signed in" and is left alone, like
 * the demo-user list answering 404 (demo login off).
 */
export const errorsInterceptor: HttpInterceptorFn = (request, next) => {
  const log = inject(Logger).withSource('Http');
  const auth = inject(AuthStore);
  const router = inject(Router);
  const sessionExpired = inject(SessionExpired);
  const ownRequest = isOwnRequest(request, inject(AppConfigService).config().apiBaseUrl);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && ownRequest) {
        const path = request.url.split('?')[0];
        // Probes whose failure is a normal answer: nobody signed in (401), demo login off (404).
        const isProbe = path === '/bff/user' || path === DEMO_LOGIN_USERS_URL;
        if (!isProbe) {
          const details = { method: request.method, url: path, status: error.status, correlationId: request.headers.get(CORRELATION_HEADER) };
          if (error.status === 0 || error.status >= 500) {
            log.error(`${request.method} ${path} failed`, details);
          } else {
            log.warn(`${request.method} ${path} returned ${error.status}`, details);
          }
        }

        if (error.status === 401 && !isProbe && auth.isLoggedIn()) {
          sessionExpired.show(router.url);
        } else if (error.status === 403 && !request.context.get(SKIP_FORBIDDEN_REDIRECT)) {
          void router.navigateByUrl('/forbidden');
        }
      }
      return throwError(() => error);
    }),
  );
};
