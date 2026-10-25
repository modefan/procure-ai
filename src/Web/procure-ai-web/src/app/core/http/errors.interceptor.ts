import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthStore } from '../auth/auth.store';
import { SessionExpired } from '../auth/session-expired';
import { AppConfigService } from '../config/app-config';
import { Logger } from '../logging/logger';
import { CORRELATION_HEADER } from './correlation.interceptor';
import { isOwnRequest } from './own-requests';

/**
 * What every failed call to the gateway does besides failing:
 * - it is logged (5xx and network errors as errors, other statuses as warnings);
 * - 401 while signed in: the session ended, so the session-expired dialog opens (the page is kept as the return url);
 * - 403: the forbidden page.
 * The caller still gets the error. /bff/user answering 401 is the normal "nobody is signed in" and is left alone.
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
        const isProbe = path === '/bff/user';
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
        } else if (error.status === 403) {
          void router.navigateByUrl('/forbidden');
        }
      }
      return throwError(() => error);
    }),
  );
};
