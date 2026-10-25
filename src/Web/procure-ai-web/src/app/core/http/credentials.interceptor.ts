import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AppConfigService } from '../config/app-config';

/**
 * The session cookie is sent by the browser on its own for same-origin calls, which is every call in a normal
 * deployment (the gateway serves the app and the API from one origin; that is also why no CORS is needed).
 * `withCredentials` matters only when the app runs on another origin than the gateway (`apiBaseUrl` is set, for
 * example a stand-alone dev server), so it is set for those calls and for nothing else.
 *
 * The XSRF header is not done here: `provideHttpClient(withXsrfConfiguration(...))` copies the XSRF-TOKEN cookie
 * into X-XSRF-TOKEN on unsafe methods for relative urls, which is exactly the gateway's rule.
 */
export const credentialsInterceptor: HttpInterceptorFn = (request, next) => {
  const base = inject(AppConfigService).config().apiBaseUrl;
  return base !== '' && request.url.startsWith(base) ? next(request.clone({ withCredentials: true })) : next(request);
};
