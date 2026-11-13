import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { SystemAvailability } from '../../../core/system/system-availability';
import { ProcurementPage } from './procurement.page';
import { AuthStore } from '../../../core/auth/auth.store';
import { AppConfigService } from '../../../core/config/app-config';
import { BrowserLocation } from '../../../core/browser/browser-location';

describe('ProcurementPage', () => {
  let http: HttpTestingController;
  let assign: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    assign = vi.fn();
    TestBed.configureTestingModule({
      imports: [ProcurementPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: BrowserLocation, useValue: { assign } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  const demoUsers = [
    { key: 'tom', label: 'Requester (Tom)', tenant: 'Acme Corp' },
    { key: 'maria', label: 'Manager (Maria)', tenant: 'Acme Corp' },
    { key: 'dept', label: 'Department head', tenant: 'Acme Corp' },
    { key: 'thandi', label: 'Finance (Thandi)', tenant: 'Acme Corp' },
    { key: 'admin', label: 'Admin', tenant: 'Acme Corp' },
    { key: 'ben', label: 'Globex manager (Ben)', tenant: 'Globex' },
  ];

  const notFound = (request: TestRequest) => request.flush(null, { status: 404, statusText: 'Not Found' });

  /**
   * Renders the page for a user (null: signed out). Signed out, the page asks the gateway for the demo users; `demo` answers
   * that call (by default: 404, demo login off).
   */
  async function render(user: object | null, demo: (request: TestRequest) => void = notFound) {
    const load = TestBed.inject(AuthStore).load();
    const request = await vi.waitFor(() => http.expectOne('/bff/user'));
    if (user) {
      request.flush(user);
    } else {
      request.flush(null, { status: 401, statusText: 'Unauthorized' });
    }
    await load;
    const fixture = TestBed.createComponent(ProcurementPage);
    if (!user) {
      demo(await vi.waitFor(() => http.expectOne('/bff/demo-login/users')));
    }
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  const actions = (element: HTMLElement) =>
    Array.from(element.querySelectorAll('a, button')).map((e) => e.textContent?.trim());

  const demoButtons = (element: HTMLElement) =>
    Array.from(element.querySelectorAll<HTMLButtonElement>('[data-testid="demo-login"] button'));

  it('shows Log in but no Register when logged out (self-registration is off by default)', async () => {
    const { element } = await render(null);

    expect(actions(element)).toContain('Log in');
    expect(actions(element)).not.toContain('Register');
    expect(actions(element)).not.toContain('Go to dashboard');
  });

  it('procurement_page_keeps_demo_buttons_and_log_in_link', async () => {
    const { element } = await render(null, (request) => request.flush(demoUsers));

    expect(demoButtons(element)).toHaveLength(6);
    const login = element.querySelector<HTMLAnchorElement>('[data-testid="log-in-link"]');
    expect(login?.parentElement?.textContent?.replace(/\s+/g, ' ').trim()).toBe('Have a Keycloak account? Log in');
    expect(login?.getAttribute('href')).toBe('/login');
    // The demo buttons come before the secondary link.
    const section = element.querySelector('[data-testid="demo-login"]')!;
    expect(section.compareDocumentPosition(login!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const about = Array.from(element.querySelectorAll('a')).find((a) => a.textContent?.trim() === 'How it is built');
    expect(about?.getAttribute('href')).toBe('/demos/procurement/about');
  });

  it('signed_in_procurement_page_offers_go_to_dashboard', async () => {
    const { element } = await render({ userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] });

    const go = Array.from(element.querySelectorAll('a')).find((a) => a.textContent?.trim() === 'Go to dashboard');
    expect(go?.getAttribute('href')).toBe('/app/dashboard');
    expect(element.querySelector('[data-testid="log-in-link"]')).toBeNull();
    expect(element.textContent).toContain('Signed in as Tom');
  });

  it('shows Register when the runtime config turns registration on', async () => {
    const config = TestBed.inject(AppConfigService).config;
    config.set({ ...config(), registration: { enabled: true } });
    const { element } = await render(null);

    expect(actions(element)).toContain('Log in');
    expect(actions(element)).toContain('Register');
  });

  it('shows Go to dashboard when logged in', async () => {
    const { element } = await render({ userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] });

    expect(actions(element)).toContain('Go to dashboard');
    expect(actions(element)).not.toContain('Log in');
    expect(actions(element)).not.toContain('Register');
  });

  it('shows the four feature tiles', async () => {
    const { element } = await render(null);

    expect(element.querySelectorAll('[data-testid="feature-tile"]')).toHaveLength(4);
  });

  it('welcome_shows_demo_buttons_when_enabled', async () => {
    const { element } = await render(null, (request) => request.flush(demoUsers));

    expect(element.querySelector('[data-testid="demo-login"] h2')?.textContent).toContain('Try a demo role');
    expect(demoButtons(element).map((b) => b.querySelector('[data-testid="demo-label"]')?.textContent?.trim())).toEqual([
      'Requester (Tom)',
      'Manager (Maria)',
      'Department head',
      'Finance (Thandi)',
      'Admin',
      'Globex manager (Ben)',
    ]);
    expect(demoButtons(element)[5].textContent).toContain('Globex');
    // The normal login stays as it is.
    expect(actions(element)).toContain('Log in');
  });

  it('welcome_hides_demo_buttons_on_404', async () => {
    const { element } = await render(null, notFound);

    expect(element.querySelector('[data-testid="demo-login"]')).toBeNull();
    expect(actions(element)).toContain('Log in');
  });

  it('hides the demo buttons when the list is empty', async () => {
    const { element } = await render(null, (request) => request.flush([]));

    expect(element.querySelector('[data-testid="demo-login"]')).toBeNull();
  });

  it('does not ask for demo users when signed in', async () => {
    await render({ userId: 'u1', name: 'Tom', tenantId: 'acme', roles: [], permissions: [] });

    http.expectNone('/bff/demo-login/users');
  });

  it('procurement_page_while_switched_off_asks_nothing_and_shows_the_paused_note', async () => {
    // While off the app initializer skips the session check, so nobody counts as signed in.
    TestBed.inject(SystemAvailability).markDisabled(null);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');

    const fixture = TestBed.createComponent(ProcurementPage);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    http.expectNone('/bff/demo-login/users');
    http.expectNone('/bff/user');
    http.verify();
    expect(navigate).not.toHaveBeenCalled();
    const note = element.querySelector('[data-testid="demo-paused"]');
    expect(note?.textContent).toContain('The demo is paused');
    expect(note?.querySelector('a')?.getAttribute('href')).toBe('/maintenance');
    expect(element.querySelector('[data-testid="demo-login"]')).toBeNull();
    expect(element.querySelector('[data-testid="log-in-link"]')).toBeNull();
    expect(actions(element)).toContain('How it is built');
  });

  it('a demo button posts only the key, then opens the dashboard with a full page load', async () => {
    const { element } = await render(null, (request) => request.flush(demoUsers));

    demoButtons(element)[0].click();
    const login = await vi.waitFor(() => http.expectOne('/bff/demo-login'));
    expect(login.request.method).toBe('POST');
    expect(login.request.body).toEqual({ user: 'tom' });
    login.flush(null, { status: 204, statusText: 'No Content' });

    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith('/app/dashboard'));
  });

  it('a failed demo login shows a message and lets the user try again', async () => {
    const { fixture, element } = await render(null, (request) => request.flush(demoUsers));

    demoButtons(element)[1].click();
    const login = await vi.waitFor(() => http.expectOne('/bff/demo-login'));
    fixture.detectChanges();
    expect(demoButtons(element).every((b) => b.disabled)).toBe(true);
    login.flush(null, { status: 502, statusText: 'Bad Gateway' });

    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(element.querySelector('[role="alert"]')?.textContent).toContain('could not sign you in');
    });
    expect(demoButtons(element).some((b) => b.disabled)).toBe(false);
    expect(assign).not.toHaveBeenCalled();
  });
});
