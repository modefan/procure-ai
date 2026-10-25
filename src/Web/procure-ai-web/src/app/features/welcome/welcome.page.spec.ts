import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { WelcomePage } from './welcome.page';
import { AuthStore } from '../../core/auth/auth.store';

describe('WelcomePage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WelcomePage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  async function render(user: object | null) {
    const load = TestBed.inject(AuthStore).load();
    const request = await vi.waitFor(() => http.expectOne('/bff/user'));
    if (user) {
      request.flush(user);
    } else {
      request.flush(null, { status: 401, statusText: 'Unauthorized' });
    }
    await load;
    const fixture = TestBed.createComponent(WelcomePage);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const actions = (element: HTMLElement) =>
    Array.from(element.querySelectorAll('a, button')).map((e) => e.textContent?.trim());

  it('shows Log in and Register when logged out', async () => {
    const element = await render(null);

    expect(actions(element)).toContain('Log in');
    expect(actions(element)).toContain('Register');
    expect(actions(element)).not.toContain('Go to dashboard');
  });

  it('shows Go to dashboard when logged in', async () => {
    const element = await render({ userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] });

    expect(actions(element)).toContain('Go to dashboard');
    expect(actions(element)).not.toContain('Log in');
    expect(actions(element)).not.toContain('Register');
  });

  it('shows the four feature tiles', async () => {
    const element = await render(null);

    expect(element.querySelectorAll('[data-testid="feature-tile"]')).toHaveLength(4);
  });
});
