import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { guestGuard } from './guest.guard';

describe('guestGuard', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  function run() {
    return TestBed.runInInjectionContext(() =>
      guestGuard({} as ActivatedRouteSnapshot, { url: '/login' } as RouterStateSnapshot),
    ) as Promise<boolean | UrlTree>;
  }

  it('redirects a logged-in user to /app/dashboard', async () => {
    const result = run();
    (await vi.waitFor(() => http.expectOne('/bff/user'))).flush({
      userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [],
    });

    const outcome = await result;
    expect(outcome).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(outcome as UrlTree)).toBe('/app/dashboard');
  });

  it('lets a visitor through (the route then starts the login)', async () => {
    const result = run();
    (await vi.waitFor(() => http.expectOne('/bff/user'))).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await result).toBe(true);
  });
});
