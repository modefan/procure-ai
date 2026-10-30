import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, provideRouter, RedirectCommand, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
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

  const redirect = (result: unknown) => result as RedirectCommand;
  const serialize = (result: unknown) => TestBed.inject(Router).serializeUrl(redirect(result).redirectTo as UrlTree);

  it('sends a user without requests.read to /forbidden', async () => {
    await signIn(['requests.create']);

    expect(serialize(run('requests.read'))).toBe('/forbidden');
  });

  it('forbidden_names_permission_from_guard: the redirect carries the permission as navigation state', async () => {
    await signIn(['requests.create']);

    expect(redirect(run('requests.read')).navigationBehaviorOptions?.state).toEqual({ permission: 'requests.read' });
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
