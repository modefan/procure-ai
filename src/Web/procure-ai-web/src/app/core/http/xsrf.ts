/**
 * The gateway's anti-forgery rule: unsafe methods carry the value of the XSRF-TOKEN cookie in the X-XSRF-TOKEN header.
 * `provideHttpClient(withXsrfConfiguration(...))` does it for HttpClient calls; the answer stream (a hand-written `fetch`) reads the
 * same names from here.
 */
export const XSRF_COOKIE_NAME = 'XSRF-TOKEN';
export const XSRF_HEADER_NAME = 'X-XSRF-TOKEN';
