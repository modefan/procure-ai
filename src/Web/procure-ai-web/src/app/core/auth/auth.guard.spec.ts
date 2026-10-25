import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PartialMatchRouteSnapshot, provideRouter, Route, UrlSegment, UrlTree } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthStore } from './auth.store';
import { BrowserLocation } from '../browser/browser-location';

describe('authGuard', () => {
  let http: HttpTestingController;
  let assign: ReturnType<typeof vi.fn>;

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
  });

  function run() {
    const segments = [new UrlSegment('app', {}), new UrlSegment('requests', {})];
    return TestBed.runInInjectionContext(() =>
      authGuard({} as Route, segments, {} as PartialMatchRouteSnapshot),
    ) as Promise<boolean | UrlTree>;
  }

  const userRequest = () => vi.waitFor(() => http.expectOne('/bff/user'));

  it('returns false and calls login with the requested url when /bff/user is 401', async () => {
    const login = vi.spyOn(TestBed.inject(AuthStore), 'login');
    const result = run();
    (await userRequest()).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await result).toBe(false);
    expect(login).toHaveBeenCalledWith('/app/requests');
    expect(assign).toHaveBeenCalledWith('/bff/login?returnUrl=%2Fapp%2Frequests');
  });

  it('returns true when /bff/user is 200', async () => {
    const result = run();
    (await userRequest()).flush({
      userId: 'u1', name: 'Tom', tenantId: 'acme', roles: ['requester'], permissions: ['requests.read'],
    });

    expect(await result).toBe(true);
    expect(assign).not.toHaveBeenCalled();
  });
});
