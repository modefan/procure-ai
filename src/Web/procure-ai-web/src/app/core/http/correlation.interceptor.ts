import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AppConfigService } from '../config/app-config';
import { isOwnRequest } from './own-requests';

export const CORRELATION_HEADER = 'X-Correlation-Id';

/** Gives every call to the gateway its own correlation id; the gateway and services carry it through logs and messages. */
export const correlationInterceptor: HttpInterceptorFn = (request, next) => {
  if (request.headers.has(CORRELATION_HEADER) || !isOwnRequest(request, inject(AppConfigService).config().apiBaseUrl)) {
    return next(request);
  }
  return next(request.clone({ setHeaders: { [CORRELATION_HEADER]: crypto.randomUUID() } }));
};
