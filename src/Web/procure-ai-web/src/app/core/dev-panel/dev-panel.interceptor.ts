import { HttpErrorResponse, HttpEvent, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { tap } from 'rxjs';
import { AppConfigService } from '../config/app-config';
import { isOwnRequest } from '../http/own-requests';
import { DEV_PANEL_HEADER, devCallFrom, DevPanelStore } from './dev-panel.store';

/**
 * While the "Behind the scenes" panel is open, asks the gateway for the diagnostic response headers and records each call.
 * Always registered; does nothing when `devPanel.enabled` is false or the panel is closed.
 */
export const devPanelInterceptor: HttpInterceptorFn = (request, next) => {
  const store = inject(DevPanelStore);
  if (!store.active() || !isOwnRequest(request, inject(AppConfigService).config().apiBaseUrl)) {
    return next(request);
  }
  const started = performance.now();
  const record = (status: number, headers: HttpResponse<unknown>['headers']) =>
    store.record(devCallFrom(request.method, request.urlWithParams, status, performance.now() - started, headers));
  return next(request.clone({ setHeaders: { [DEV_PANEL_HEADER]: '1' } })).pipe(
    tap({
      next: (event: HttpEvent<unknown>) => {
        if (event instanceof HttpResponse) {
          record(event.status, event.headers);
        }
      },
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse) {
          record(error.status, error.headers);
        }
      },
    }),
  );
};
