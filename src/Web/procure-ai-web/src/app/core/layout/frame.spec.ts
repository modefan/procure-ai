import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { ApprovalsStore } from '../../features/approvals/approvals.store';
import { NotificationsService } from '../../api/notifications/services/notifications.service';
import { AuthStore } from '../auth/auth.store';
import { AppConfigService } from '../config/app-config';
import { DevPanelStore } from '../dev-panel/dev-panel.store';
import { LiveConnection, LiveState } from '../live/live-connection';
import { NotificationsStore } from '../live/notifications.store';
import { PresenceStore } from '../live/presence.store';
import { CacheRegistry } from '../state/cache-registry';
import { SystemAvailability } from '../system/system-availability';
import { Frame } from './frame';

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();

describe('Frame', () => {
  const user = signal<{
    userId: string;
    name: string;
    tenantId: string;
    tenantName?: string | null;
    tenants?: { id: string; name: string }[];
    roles: string[];
    permissions: string[];
  } | null>(null);
  const state = signal<LiveState>('connected');
  const switchError = signal<string | null>(null);
  const dismissSwitchError = vi.fn(() => switchError.set(null));
  const authStore = {
    user,
    isLoggedIn: () => user() !== null,
    hasPermission: (p: string) => user()?.permissions.includes(p) ?? false,
    tenants: () => user()?.tenants ?? [],
    logout: vi.fn(),
    logoutAll: vi.fn(),
    switchTenant: vi.fn(),
    switchError,
    dismissSwitchError,
  };
  const liveStub = {
    state,
    on: () => () => undefined,
    onReconnected: () => () => undefined,
    onEntityChanged: () => () => undefined,
    onViewersChanged: () => () => undefined,
  };
  const signIn = (permissions: string[], roles = ['requester']) =>
    user.set({ userId: 'u1', name: 'Tom Mokoena', tenantId: 'acme', roles, permissions });

  afterEach(() => vi.restoreAllMocks());

  beforeEach(() => {
    user.set(null);
    state.set('connected');
    switchError.set(null);
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  describe('session calls (real stores, HttpTestingController)', () => {
    beforeEach(() =>
      TestBed.configureTestingModule({
        providers: [
          provideRouter([]),
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: AuthStore, useValue: authStore },
          { provide: LiveConnection, useValue: liveStub },
        ],
      }),
    );

    const sessionCalls = async () => {
      const fixture = TestBed.createComponent(Frame);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 20));
      const urls = TestBed.inject(HttpTestingController)
        .match(() => true)
        .map((request) => request.request.url);
      return { el: fixture.nativeElement as HTMLElement, calls: urls.filter((url) => /\/(bff|api|hubs)\b/.test(url)) };
    };

    it('the skip link focuses the page without navigating (no /#main reload of Welcome)', async () => {
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
      const { el } = await sessionCalls();
      const skip = el.querySelector<HTMLAnchorElement>('[data-testid=skip-link]')!;

      const click = new MouseEvent('click', { bubbles: true, cancelable: true });
      skip.dispatchEvent(click);

      expect(click.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(el.querySelector('#main'));
      expect(navigate).not.toHaveBeenCalled();
    });

    it('frame_makes_no_session_calls_while_disabled', async () => {
      signIn(['requests.read', 'requests.approve']);
      TestBed.inject(SystemAvailability).markDisabled(null);

      const { el, calls } = await sessionCalls();

      expect(calls).toEqual([]);
      expect(el.querySelector('[data-testid=user-menu]')).toBeNull();
      expect(el.querySelector('[data-testid=live-indicator]')).toBeNull();
      // The public menu is still there.
      expect(text(el.querySelector('nav[aria-label=Main]'))).toContain('Welcome');
    });

    it('loads the bell and the approvals count while the demo is on (control for the test above)', async () => {
      signIn(['requests.read', 'requests.approve']);

      const { el, calls } = await sessionCalls();

      expect(calls.some((url) => url.includes('/api/'))).toBe(true);
      await vi.waitFor(() => {
        TestBed.tick();
        expect(el.querySelector('[data-testid=user-menu]')).not.toBeNull();
      });
    });
  });

  describe('with stub stores', () => {
    const count = signal(0);
    const unread = signal(0);

    beforeEach(() => {
      count.set(0);
      unread.set(0);
      TestBed.configureTestingModule({
        providers: [
          provideRouter([
            { path: '', children: [] },
            { path: 'app/requests', children: [] },
            { path: 'app/orders', children: [] },
          ]),
          { provide: AuthStore, useValue: authStore },
          { provide: LiveConnection, useValue: liveStub },
          { provide: NotificationsStore, useValue: { items: signal([]), unread, load: vi.fn().mockResolvedValue(undefined), markRead: vi.fn(), markAllRead: vi.fn() } },
          { provide: PresenceStore, useValue: { online: signal([]) } },
          { provide: ApprovalsStore, useValue: { total: count, refresh: vi.fn().mockResolvedValue(undefined) } },
        ],
      });
    });

    const render = async () => {
      const fixture = TestBed.createComponent(Frame);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, el: fixture.nativeElement as HTMLElement };
    };
    const settle = async (fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };

    it('current_page_has_aria_current', async () => {
      signIn(['requests.read', 'orders.read']);
      const { fixture, el } = await render();
      const nav = () => el.querySelector('nav[aria-label=Main]')!;

      await TestBed.inject(Router).navigateByUrl('/');
      await settle(fixture);
      expect([...nav().querySelectorAll('[aria-current=page]')].map(text)).toEqual(['Welcome']);

      await TestBed.inject(Router).navigateByUrl('/app/requests');
      await settle(fixture);
      expect([...nav().querySelectorAll('[aria-current=page]')].map(text)).toEqual(['Requests']);
    });

    it('the approvals link shows its count and says it in its name', async () => {
      signIn(['requests.read', 'requests.approve']);
      count.set(3);
      const { el } = await render();
      const link = [...el.querySelectorAll('nav[aria-label=Main] a')].find((a) => text(a)?.startsWith('Approvals'))!;
      expect(text(link)).toMatch(/^Approvals\s*3$/);
      expect(link.getAttribute('aria-label')).toBe('Approvals, 3 waiting');
    });

    it('the Aspire dashboard link opens in a new tab and says so; Swagger is a plain full page load', async () => {
      TestBed.inject(AppConfigService).config.update((c) => ({ ...c, observability: { dashboardUrl: 'https://dashboard.example.org' } }));
      const { el } = await render();
      const links = [...el.querySelectorAll('nav[aria-label=Main] a')];
      const dashboard = links.find((a) => a.getAttribute('href') === 'https://dashboard.example.org')!;
      expect(dashboard.getAttribute('target')).toBe('_blank');
      expect(dashboard.getAttribute('rel')).toBe('noopener noreferrer');
      expect(text(dashboard)).toBe('Aspire dashboard(opens in a new tab)');
      const swagger = links.find((a) => a.getAttribute('href') === '/swagger')!;
      expect(text(swagger)).toBe('API (Swagger)');
      expect(swagger.getAttribute('target')).toBeNull();
    });

    it('the phone menu button opens the drawer with the same menu, and a navigation closes it', async () => {
      signIn(['requests.read', 'orders.read']);
      const { fixture, el } = await render();
      const button = el.querySelector<HTMLButtonElement>('[data-testid=phone-menu]')!;
      const dialog = () => el.querySelector('dialog[data-testid=menu-drawer]') as HTMLDialogElement;
      expect(button.getAttribute('aria-expanded')).toBe('false');

      button.click();
      await settle(fixture);
      expect(dialog().hasAttribute('open')).toBe(true);
      expect(button.getAttribute('aria-expanded')).toBe('true');
      expect(text(dialog().querySelector('nav'))).toContain('Orders');

      await TestBed.inject(Router).navigateByUrl('/app/orders');
      await settle(fixture);
      expect(dialog().hasAttribute('open')).toBe(false);
      expect(button.getAttribute('aria-expanded')).toBe('false');
    });

    it('shows the reconnecting banner only while reconnecting', async () => {
      signIn(['requests.read']);
      state.set('reconnecting');
      const { fixture, el } = await render();
      expect(text(el.querySelector('[data-testid=live-banner]'))).toBe('Reconnecting… Live updates paused. Your work is safe.');

      state.set('connected');
      fixture.detectChanges();
      expect(el.querySelector('[data-testid=live-banner]')).toBeNull();
    });

    it('refresh_failure_banner_shows_and_retries', async () => {
      signIn(['requests.read']);
      const failing = signal(false);
      const revalidateAll = vi.fn(() => failing.set(false));
      TestBed.inject(CacheRegistry).register({ clearCache: vi.fn(), revalidateAll, revalidateError: () => failing() });
      const { fixture, el } = await render();
      expect(el.querySelector('[data-testid=refresh-banner]')).toBeNull();

      failing.set(true);
      fixture.detectChanges();
      const banner = el.querySelector('[data-testid=refresh-banner]')!;
      expect(text(banner)).toContain("Couldn't refresh. Showing what we had.");

      (banner.querySelector('button') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(revalidateAll).toHaveBeenCalledWith({ force: true });
      expect(el.querySelector('[data-testid=refresh-banner]')).toBeNull();
    });

    it('the refresh banner is not shown to a signed-out visitor', async () => {
      const failing = signal(true);
      TestBed.inject(CacheRegistry).register({ clearCache: vi.fn(), revalidateError: () => failing() });
      expect((await render()).el.querySelector('[data-testid=refresh-banner]')).toBeNull();
    });

    it('switch_refused_shows_a_toast_that_can_be_closed', async () => {
      signIn(['requests.read'], ['finance']);
      const { el } = await render();
      expect(el.querySelector('[data-testid=switch-error]')).toBeNull();

      switchError.set('You are not a member of that company any more.');
      TestBed.tick();
      const toast = el.querySelector('[data-testid=switch-error]');
      expect(toast?.getAttribute('role')).toBe('alert');
      expect(text(toast)).toContain('You are not a member of that company any more.');

      toast!.querySelector<HTMLButtonElement>('button')!.click();
      TestBed.tick();
      expect(dismissSwitchError).toHaveBeenCalled();
      expect(el.querySelector('[data-testid=switch-error]')).toBeNull();
    });

    describe('dev panel', () => {
      const keydown = (init: KeyboardEventInit) => document.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, ...init }));
      const enable = (enabled: boolean) => TestBed.inject(AppConfigService).config.update((c) => ({ ...c, devPanel: { enabled } }));

      beforeEach(() => sessionStorage.clear());

      it('alt_shift_d_toggles', async () => {
        signIn(['requests.read']);
        enable(true);
        const { el } = await render();
        const store = TestBed.inject(DevPanelStore);
        expect(store.open()).toBe(false);

        keydown({ altKey: true, shiftKey: true, code: 'KeyD', key: 'D' });
        expect(store.open()).toBe(true);
        TestBed.tick();
        await vi.waitFor(() => expect(el.querySelector('app-dev-panel')).not.toBeNull());

        keydown({ altKey: true, code: 'KeyD', key: 'd' }); // without Shift: not ours
        expect(store.open()).toBe(true);
        keydown({ altKey: true, shiftKey: true, code: 'KeyD', key: 'D' });
        expect(store.open()).toBe(false);
      });

      it('shortcut_does_nothing_when_disabled', async () => {
        signIn(['requests.read']);
        enable(false);
        await render();
        keydown({ altKey: true, shiftKey: true, code: 'KeyD', key: 'D' });
        expect(TestBed.inject(DevPanelStore).open()).toBe(false);
      });
    });
  });

  it('marking_read_or_a_push_does_not_trigger_another_list_call', async () => {
    const answer = { unread: 1, items: [{ id: 'n1', text: 't', link: '/requests/r', read: false, createdAt: '2026-10-28T10:00:00Z', requestId: null }] };
    // Every call after the 5th never answers, so a re-running effect (the bug) ends in a failed count, not a loop.
    const list = vi.fn().mockImplementation(() => (list.mock.calls.length > 5 ? new Promise(() => undefined) : Promise.resolve(structuredClone(answer))));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: NotificationsService, useValue: { listNotifications: list, markNotificationRead: vi.fn().mockResolvedValue(undefined), markAllNotificationsRead: vi.fn() } },
        { provide: AuthStore, useValue: { ...authStore, hasPermission: () => false } },
        { provide: LiveConnection, useValue: liveStub },
        { provide: PresenceStore, useValue: { online: signal([]) } },
        { provide: ApprovalsStore, useValue: { total: signal(0), refresh: vi.fn() } },
      ],
    });
    // A clock that jumps 31 s at every reading: the list is always stale, so any extra load() call reaches the API.
    let clock = Date.now();
    vi.spyOn(Date, 'now').mockImplementation(() => (clock += 31_000));
    signIn(['requests.read']);
    const fixture = TestBed.createComponent(Frame);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    const before = list.mock.calls.length;

    const store = TestBed.inject(NotificationsStore);
    await store.markRead('n1');
    store.add({ id: 'n2', text: 'x', link: '/requests/q', read: false, createdAt: '2026-10-28T10:01:00Z', requestId: null });
    fixture.detectChanges();
    await fixture.whenStable();

    expect(before).toBe(1);
    expect(list).toHaveBeenCalledTimes(1);
  });
});
