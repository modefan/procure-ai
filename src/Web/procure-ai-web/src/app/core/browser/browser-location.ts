import { Service } from '@angular/core';

/** The browser's address bar, behind a seam so tests can watch navigations away from the app. */
@Service()
export class BrowserLocation {
  /** Leaves the single-page app (a full page load), for example to the gateway's login. */
  assign(url: string): void {
    window.location.assign(url);
  }
}
