import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from './auth.store';
import { BrowserLocation } from '../browser/browser-location';
import { CacheRegistry } from '../state/cache-registry';
import { LiveConnection } from '../live/live-connection';
import { SKIP_FORBIDDEN_REDIRECT } from '../http/skip-forbidden';

describe('AuthStore', () => {
  let http: HttpTestingController;
  let assign: ReturnType<typeof vi.fn>;
  let store: InstanceType<typeof AuthStore>;

  const tom = { userId: 'u1', name: 'Tom', tenantId: 'acme', roles: ['requester'], permissions: ['requests.read', 'requests.create'] };

  beforeEach(() => {
    assign = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: BrowserLocation, useValue: { assign } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(AuthStore);
  });

  const userRequest = () => vi.waitFor(() => http.expectOne('/bff/user'));

  it('starts signed out and not loaded', () => {
    expect(store.isLoggedIn()).toBe(false);
    expect(store.loaded()).toBe(false);
  });

  it('load() reads the user and derives the permission set', async () => {
    const loading = store.load();
    (await userRequest()).flush(tom);
    await loading;

    expect(store.isLoggedIn()).toBe(true);
    expect(store.loaded()).toBe(true);
    expect(store.permissions()).toEqual(new Set(['requests.read', 'requests.create']));
    expect(store.hasPermission('requests.read')).toBe(true);
    expect(store.hasPermission('requests.approve')).toBe(false);
  });

  it('load() treats 401 as nobody signed in', async () => {
    const loading = store.load();
    (await userRequest()).flush(null, { status: 401, statusText: 'Unauthorized' });
    await loading;

    expect(store.isLoggedIn()).toBe(false);
    expect(store.loaded()).toBe(true);
  });

  it.each([500, 503, 0])('load() with status %i is a failure, not "signed out", and can be retried', async (status) => {
    const loading = store.load();
    (await userRequest()).flush(null, { status, statusText: 'Server Error' });
    await loading;

    expect(store.isLoggedIn()).toBe(false);
    expect(store.error()).toBe(true);
    expect(store.loaded()).toBe(false);

    const retry = store.load();
    (await userRequest()).flush(tom);
    await retry;
    expect(store.isLoggedIn()).toBe(true);
    expect(store.error()).toBe(false);
  });

  it('endSession() forgets the user', async () => {
    const loading = store.load();
    (await userRequest()).flush(tom);
    await loading;

    store.endSession();

    expect(store.isLoggedIn()).toBe(false);
  });

  it('load() shares one request between concurrent callers and asks only once', async () => {
    const first = store.load();
    const second = store.load();
    (await userRequest()).flush(tom);
    await Promise.all([first, second]);
    await store.load();

    http.expectNone('/bff/user');
  });

  it('login and register send the browser to the gateway with the encoded return url', () => {
    store.login('/app/requests?page=2');
    store.register('/app/dashboard');

    expect(assign).toHaveBeenNthCalledWith(1, '/bff/login?returnUrl=%2Fapp%2Frequests%3Fpage%3D2');
    expect(assign).toHaveBeenNthCalledWith(2, '/bff/register?returnUrl=%2Fapp%2Fdashboard');
  });

  it('logout() posts to the gateway, forgets the user and goes to the welcome page', async () => {
    const loading = store.load();
    (await userRequest()).flush(tom);
    await loading;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const loggingOut = store.logout();
    const request = await vi.waitFor(() => http.expectOne('/bff/logout'));
    expect(request.request.method).toBe('POST');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await loggingOut;

    expect(store.isLoggedIn()).toBe(false);
    expect(navigate).toHaveBeenCalledWith('/');
  });
  it('logout() clears every registered cache, also when the gateway call fails', async () => {
    const clearCache = vi.fn();
    TestBed.inject(CacheRegistry).register({ clearCache });
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const loggingOut = store.logout();
    (await vi.waitFor(() => http.expectOne('/bff/logout'))).flush(null, { status: 500, statusText: 'Server Error' });
    await loggingOut.catch(() => undefined);

    expect(clearCache).toHaveBeenCalledTimes(1);
  });

  it('endSession() clears every registered cache', () => {
    const clearCache = vi.fn();
    TestBed.inject(CacheRegistry).register({ clearCache });

    store.endSession();

    expect(clearCache).toHaveBeenCalledTimes(1);
  });

  it('logout_stops_connection_and_clears_presence', async () => {
    const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
    const clearCache = vi.fn();
    TestBed.inject(CacheRegistry).register({ clearCache });
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const loggingOut = store.logout();
    (await vi.waitFor(() => http.expectOne('/bff/logout'))).flush(null, { status: 204, statusText: 'No Content' });
    await loggingOut;

    expect(stop).toHaveBeenCalledTimes(1);
    expect(clearCache).toHaveBeenCalledTimes(1);
  });

  it('logoutAll posts to /bff/logout-all, stops the connection and clears the caches', async () => {
    const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
    const clearCache = vi.fn();
    TestBed.inject(CacheRegistry).register({ clearCache });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    const loggingOut = store.logoutAll();
    const request = await vi.waitFor(() => http.expectOne('/bff/logout-all'));
    expect(request.request.method).toBe('POST');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await loggingOut;

    expect(stop).toHaveBeenCalledTimes(1);
    expect(clearCache).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/');
  });

  describe('switchTenant', () => {
    const challenge = '/bff/switch-tenant/challenge?ticket=t1';
    const switchRequest = () => vi.waitFor(() => http.expectOne('/bff/switch-tenant'));
    const signedIn = async () => {
      const loading = store.load();
      (await userRequest()).flush({ ...tom, tenants: [{ id: 'acme', name: 'Acme Corp' }, { id: 'globex', name: 'Globex' }] });
      await loading;
    };

    it('switch_posts_then_navigates', async () => {
      const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
      const clearCache = vi.fn();
      TestBed.inject(CacheRegistry).register({ clearCache });

      const switching = store.switchTenant('globex');
      const request = await switchRequest();

      // A POST with the tenant and return url in the body (the XSRF header is added by HttpClient for unsafe methods); nothing
      // is cleared or stopped before the gateway has said yes.
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ tenant: 'globex', returnUrl: '/app/dashboard' });
      // A refusal (403) is answered on this page, not by the forbidden page.
      expect(request.request.context.get(SKIP_FORBIDDEN_REDIRECT)).toBe(true);
      expect(store.switching()).toBe(true);
      expect(clearCache).not.toHaveBeenCalled();
      expect(stop).not.toHaveBeenCalled();
      request.flush({ navigateTo: challenge });
      await switching;

      expect(clearCache).toHaveBeenCalledTimes(1);
      expect(stop).toHaveBeenCalledTimes(1);
      expect(assign).toHaveBeenCalledWith(challenge);
      // Nothing of the old company may arrive or be shown while the browser leaves: the caches and the connection go first.
      expect(stop.mock.invocationCallOrder[0]).toBeLessThan(assign.mock.invocationCallOrder[0]);
      expect(clearCache.mock.invocationCallOrder[0]).toBeLessThan(assign.mock.invocationCallOrder[0]);
    });

    it.each([
      [403, 'You are not a member of that company any more.'],
      [503, 'Switching company is not available right now. Please try again.'],
      [400, 'Switching company is not available right now. Please try again.'],
      [0, 'Switching company is not available right now. Please try again.'],
    ])('switch_refused_shows_message_and_keeps_state (%i)', async (status, message) => {
      await signedIn();
      const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
      const clearCache = vi.fn();
      TestBed.inject(CacheRegistry).register({ clearCache });
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigateByUrl');

      const switching = store.switchTenant('globex');
      (await switchRequest()).flush(null, { status, statusText: 'Refused' });
      await switching;

      expect(store.switchError()).toBe(message);
      expect(store.switching()).toBe(false);
      expect(store.user()?.tenantId).toBe('acme');
      expect(clearCache).not.toHaveBeenCalled();
      expect(stop).not.toHaveBeenCalled();
      expect(assign).not.toHaveBeenCalled();
      // A refused switch is not the forbidden page: the user stays where they are.
      expect(navigate).not.toHaveBeenCalledWith('/forbidden');

      store.dismissSwitchError();
      expect(store.switchError()).toBeNull();
    });

    it('switchTenant navigates even when stopping the live connection fails', async () => {
      vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockRejectedValue(new Error('hub gone'));

      const switching = store.switchTenant('globex').catch(() => undefined);
      (await switchRequest()).flush({ navigateTo: '/app/dashboard' });
      await switching;

      expect(assign).toHaveBeenCalledWith('/app/dashboard');
    });

    it('switchTenant sends the return url and goes only to a path on this site', async () => {
      vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();

      const switching = store.switchTenant('a&b c', '/app/requests?status=open');
      const request = await switchRequest();
      expect(request.request.body).toEqual({ tenant: 'a&b c', returnUrl: '/app/requests?status=open' });
      request.flush({ navigateTo: 'https://evil.test/' });
      await switching;

      expect(assign).toHaveBeenCalledWith('/');
    });

    it('a second click while a switch is under way sends nothing', async () => {
      vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();

      const first = store.switchTenant('globex');
      await store.switchTenant('globex');
      const request = await switchRequest();
      request.flush({ navigateTo: '/app/dashboard' });
      await first;

      expect(assign).toHaveBeenCalledTimes(1);
    });
  });

  it('load() keeps the tenants and tenant name the gateway sends', async () => {
    const loading = store.load();
    (await userRequest()).flush({ ...tom, tenantName: 'Acme Corp', tenants: [{ id: 'acme', name: 'Acme Corp' }, { id: 'globex', name: 'Globex' }] });
    await loading;

    expect(store.tenants()).toEqual([{ id: 'acme', name: 'Acme Corp' }, { id: 'globex', name: 'Globex' }]);
    expect(store.tenantName()).toBe('Acme Corp');
  });

  it('tenants falls back to none and tenantName to the tenant id for an older gateway answer', async () => {
    const loading = store.load();
    (await userRequest()).flush(tom);
    await loading;

    expect(store.tenants()).toEqual([]);
    expect(store.tenantName()).toBe('acme');
  });

  it('endSession() stops the connection', () => {
    const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
    store.endSession();
    expect(stop).toHaveBeenCalledTimes(1);
  });
});
