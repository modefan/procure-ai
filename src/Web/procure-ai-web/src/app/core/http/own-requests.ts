import { HttpRequest } from '@angular/common/http';

/**
 * Is this request to our own gateway? Relative urls are (same origin); absolute ones only when they start with
 * the configured gateway origin (`apiBaseUrl`, set only when the app is not served by the gateway itself).
 * Our headers and credentials must never go to a third party.
 */
export function isOwnRequest(request: HttpRequest<unknown>, apiBaseUrl: string): boolean {
  const absolute = /^[a-z][a-z0-9+.-]*:\/\//i.test(request.url) || request.url.startsWith('//');
  return !absolute || (apiBaseUrl !== '' && request.url.startsWith(apiBaseUrl));
}
