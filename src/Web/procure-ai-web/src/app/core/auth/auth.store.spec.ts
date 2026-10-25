import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from './auth.store';
import { BrowserLocation } from '../browser/browser-location';

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
});
