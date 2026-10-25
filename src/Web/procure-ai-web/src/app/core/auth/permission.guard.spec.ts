import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { permissionGuard } from './permission.guard';
import { AuthStore } from './auth.store';

describe('permissionGuard', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  async function signIn(permissions: string[]) {
    const load = TestBed.inject(AuthStore).load();
    (await vi.waitFor(() => http.expectOne('/bff/user'))).flush({
      userId: 'u1', name: 'Tom', tenantId: 'acme', roles: ['requester'], permissions,
    });
    await load;
  }

  function run(permission: string | undefined) {
    const route = { data: permission === undefined ? {} : { permission } } as unknown as ActivatedRouteSnapshot;
    return TestBed.runInInjectionContext(() => permissionGuard(route, { url: '/app/requests' } as RouterStateSnapshot));
  }

  const serialize = (tree: unknown) => TestBed.inject(Router).serializeUrl(tree as UrlTree);

  it('sends a user without requests.read to /forbidden', async () => {
    await signIn(['requests.create']);

    expect(serialize(run('requests.read'))).toBe('/forbidden');
  });

  it('lets a user with requests.read through', async () => {
    await signIn(['requests.read']);

    expect(run('requests.read')).toBe(true);
  });

  it('sends the user to /forbidden when the route names no permission (a route without one is a bug)', async () => {
    await signIn(['requests.read']);

    expect(serialize(run(undefined))).toBe('/forbidden');
  });
});
