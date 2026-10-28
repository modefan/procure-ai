import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpContext, HttpErrorResponse, provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthStore } from '../auth/auth.store';
import { SessionExpired } from '../auth/session-expired';
import { AppConfigService } from '../config/app-config';
import { credentialsInterceptor } from './credentials.interceptor';
import { correlationInterceptor } from './correlation.interceptor';
import { errorsInterceptor, SKIP_FORBIDDEN_REDIRECT } from './errors.interceptor';

describe('http interceptors', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 202 })));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(
          withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
          withInterceptors([credentialsInterceptor, correlationInterceptor, errorsInterceptor]),
        ),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const fail = async (call: Promise<unknown>, url: string, status: number) => {
    const settled = call.catch(() => undefined);
    backend.expectOne(url).flush(null, { status, statusText: 'x' });
    await settled;
  };

  it('puts a correlation id on every call to the gateway', async () => {
    const call = firstValueFrom(http.get('/api/v1/requests'));
    const request = backend.expectOne('/api/v1/requests');
    expect(request.request.headers.get('X-Correlation-Id')).toMatch(/^[0-9a-f-]{36}$/);
    request.flush([]);
    await call;
  });

  it('keeps our headers away from other origins', async () => {
    const call = firstValueFrom(http.get('https://example.org/data'));
    const request = backend.expectOne('https://example.org/data');
    expect(request.request.headers.has('X-Correlation-Id')).toBe(false);
    expect(request.request.withCredentials).toBe(false);
    request.flush({});
    await call;
  });

  it('sets withCredentials only for the configured gateway origin', async () => {
    TestBed.inject(AppConfigService).config.update((c) => ({ ...c, apiBaseUrl: 'https://gateway.test' }));
    const call = firstValueFrom(http.get('https://gateway.test/api/x'));
    const request = backend.expectOne('https://gateway.test/api/x');
    expect(request.request.withCredentials).toBe(true);
    request.flush({});
    await call;
  });

  it('sends the XSRF cookie value in X-XSRF-TOKEN on unsafe methods only', async () => {
    document.cookie = 'XSRF-TOKEN=abc123; path=/';

    const post = firstValueFrom(http.post('/api/v1/requests', {}));
    const posted = backend.expectOne('/api/v1/requests');
    expect(posted.request.headers.get('X-XSRF-TOKEN')).toBe('abc123');
    posted.flush({});
    await post;

    const get = firstValueFrom(http.get('/api/v1/requests'));
    const read = backend.expectOne('/api/v1/requests');
    expect(read.request.headers.has('X-XSRF-TOKEN')).toBe(false);
    read.flush([]);
    await get;
  });

  it('403 goes to the forbidden page', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    await fail(firstValueFrom(http.get('/api/v1/requests')), '/api/v1/requests', 403);

    expect(navigate).toHaveBeenCalledWith('/forbidden');
  });

  it('a call marked SKIP_FORBIDDEN_REDIRECT keeps the page on a 403 and still fails for the caller', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const context = new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

    const call = firstValueFrom(http.get('/api/v1/purchasing/orders/1', { context }));
    const settled = call.then(() => 'ok', (error: HttpErrorResponse) => error.status);
    backend.expectOne('/api/v1/purchasing/orders/1').flush(null, { status: 403, statusText: 'x' });

    expect(await settled).toBe(403);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('401 while signed in opens the session-expired dialog', async () => {
    const auth = TestBed.inject(AuthStore);
    const loading = auth.load();
    backend.expectOne('/bff/user').flush({ userId: 'u', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] });
    await loading;

    await fail(firstValueFrom(http.get('/api/v1/requests')), '/api/v1/requests', 401);

    expect(TestBed.inject(SessionExpired).returnUrl()).not.toBeNull();
  });

  it('401 from /bff/user (nobody is signed in) opens nothing', async () => {
    await fail(firstValueFrom(http.get('/bff/user')), '/bff/user', 401);

    expect(TestBed.inject(SessionExpired).returnUrl()).toBeNull();
  });

  it('401 while signed out opens nothing', async () => {
    await fail(firstValueFrom(http.get('/api/v1/requests')), '/api/v1/requests', 401);

    expect(TestBed.inject(SessionExpired).returnUrl()).toBeNull();
  });
});
