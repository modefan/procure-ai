import { inject, Service } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { BrowserLocation } from '../browser/browser-location';

/** One demo role the gateway offers (GET /bff/demo-login/users). No username or password: the browser only knows the key. */
export interface DemoLoginUser {
  key: string;
  label: string;
  /** The company's display name, e.g. "Acme Corp". */
  tenant: string;
}

/** The gateway's demo-user list. 404 when demo login is off: the welcome page then shows no demo buttons. */
export const DEMO_LOGIN_USERS_URL = '/bff/demo-login/users';

/**
 * One-click demo login: the gateway signs the listed user in and sets the session cookie. Like the gateway's own BFF endpoints,
 * these are called by hand, not through a generated client. The POST carries the XSRF header (the users call hands out the
 * cookie); afterwards the dashboard opens with a full page load, so the app starts fresh for the new user, as after the normal login.
 */
@Service()
export class DemoLogin {
  private readonly http = inject(HttpClient);
  private readonly location = inject(BrowserLocation);

  /** The demo roles on offer; empty when demo login is off (404) or the gateway cannot answer. */
  async users(): Promise<DemoLoginUser[]> {
    try {
      const users = await firstValueFrom(this.http.get<DemoLoginUser[]>(DEMO_LOGIN_USERS_URL));
      return Array.isArray(users) ? users : [];
    } catch {
      return [];
    }
  }

  async signIn(key: string): Promise<void> {
    await firstValueFrom(this.http.post('/bff/demo-login', { user: key }));
    this.location.assign('/app/dashboard');
  }
}
