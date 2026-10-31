import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from './auth.store';
import { BrowserLocation } from '../browser/browser-location';
import { CacheRegistry } from '../state/cache-registry';
import { LiveConnection } from '../live/live-connection';

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

  it('switch_clears_caches_and_stops_live', async () => {
    const stop = vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();
    const clearCache = vi.fn();
    TestBed.inject(CacheRegistry).register({ clearCache });

    await store.switchTenant('globex');

    expect(clearCache).toHaveBeenCalledTimes(1);
    expect(stop).toHaveBeenCalledTimes(1);
    expect(assign).toHaveBeenCalledWith('/bff/switch-tenant?tenant=globex&returnUrl=%2Fapp%2Fdashboard');
    // Nothing of the old company may arrive or be shown while the browser leaves: the caches and the connection go first.
    expect(stop.mock.invocationCallOrder[0]).toBeLessThan(assign.mock.invocationCallOrder[0]);
    expect(clearCache.mock.invocationCallOrder[0]).toBeLessThan(assign.mock.invocationCallOrder[0]);
  });

  it('switchTenant encodes the tenant and the return url', async () => {
    vi.spyOn(TestBed.inject(LiveConnection), 'stop').mockResolvedValue();

    await store.switchTenant('a&b c', '/app/requests?status=open');

    expect(assign).toHaveBeenCalledWith('/bff/switch-tenant?tenant=a%26b%20c&returnUrl=%2Fapp%2Frequests%3Fstatus%3Dopen');
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
