import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { ApprovalsStore } from '../approvals/approvals.store';
import { LiveConnection, LiveState } from '../../core/live/live-connection';
import { NotificationsStore } from '../../core/live/notifications.store';
import { NotificationsService } from '../../api/notifications/services/notifications.service';
import { PresenceStore } from '../../core/live/presence.store';
import { Shell } from './shell';

describe('Shell header', () => {
  const user = signal<{ userId: string; name: string; tenantId: string; roles: string[]; permissions: string[] } | null>(null);
  const state = signal<LiveState>('connected');
  const items = signal<{ id: string; text: string; link: string; read: boolean; createdAt: string; requestId: string | null }[]>([]);
  const unread = signal(0);
  const count = signal(0);
  const online = signal<{ userId: string; name: string }[]>([]);
  const markRead = vi.fn().mockResolvedValue(undefined);
  const markAllRead = vi.fn().mockResolvedValue(undefined);
  const logout = vi.fn();
  const logoutAll = vi.fn();

  const signIn = (permissions: string[], roles = ['requester']) =>
    user.set({ userId: 'u1', name: 'Tom Requester', tenantId: 'acme', roles, permissions });

  beforeEach(() => {
    user.set(null);
    state.set('connected');
    items.set([]);
    unread.set(0);
    count.set(0);
    online.set([]);
    markRead.mockClear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthStore,
          useValue: {
            user,
            isLoggedIn: () => user() !== null,
            hasPermission: (p: string) => user()?.permissions.includes(p) ?? false,
            logout,
            logoutAll,
          },
        },
        { provide: LiveConnection, useValue: { state } },
        { provide: NotificationsStore, useValue: { items, unread, load: vi.fn().mockResolvedValue(undefined), markRead, markAllRead } },
        { provide: PresenceStore, useValue: { online } },
        { provide: ApprovalsStore, useValue: { total: count, refresh: vi.fn().mockResolvedValue(undefined) } },
      ],
    });
  });

  const render = async () => {
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  };
  const navLinks = (el: HTMLElement) => [...el.querySelectorAll('nav a')].map((a) => a.textContent?.replace(/\s+/g, ' ').trim());
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();

  it('links_follow_permissions', async () => {
    signIn(['requests.read', 'requests.create']);
    expect(navLinks(await render())).toEqual(['Dashboard', 'Requests']);
  });

  it('finance_sees_orders_invoices_and_budgets', async () => {
    signIn(['requests.read', 'orders.read', 'invoices.read', 'budgets.read']);
    expect(navLinks(await render())).toEqual(['Dashboard', 'Requests', 'Orders', 'Invoices', 'Budgets']);
  });

  it('assistant_link_needs_assistant_use', async () => {
    signIn(['requests.read', 'budgets.read', 'assistant.use']);
    expect(navLinks(await render())).toEqual(['Dashboard', 'Requests', 'Budgets', 'Assistant']);
  });

  it('a_manager_sees_the_approvals_link', async () => {
    signIn(['requests.read', 'requests.approve', 'budgets.read'], ['manager']);
    expect(navLinks(await render())).toEqual(['Dashboard', 'Requests', expect.stringContaining('Approvals'), 'Budgets']);
  });

  it('approvals_link_shows_count', async () => {
    signIn(['requests.read', 'requests.approve']);
    count.set(3);
    const el = await render();

    const link = [...el.querySelectorAll('nav a')].find((a) => text(a)?.startsWith('Approvals'))!;
    expect(text(link)).toBe('Approvals 3');
    expect(link.getAttribute('aria-label')).toBe('Approvals, 3 waiting');
  });

  it('no count badge when nothing waits', async () => {
    signIn(['requests.read', 'requests.approve']);
    const el = await render();
    const link = [...el.querySelectorAll('nav a')].find((a) => text(a)?.startsWith('Approvals'))!;
    expect(text(link)).toBe('Approvals');
  });


  it('scrolls the active nav link into view after a navigation (the phone nav scrolls sideways)', async () => {
    signIn(['requests.read', 'orders.read', 'invoices.read', 'budgets.read']);
    const scrolled: string[] = [];
    const original = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = function (this: Element, options?: boolean | ScrollIntoViewOptions) {
      scrolled.push(this.textContent?.trim() + '|' + JSON.stringify(options));
    };
    try {
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      TestBed.inject(Router).resetConfig([{ path: 'app/budgets', children: [] }]);
      await TestBed.inject(Router).navigateByUrl('/app/budgets');
      fixture.detectChanges();
      for (let i = 0; i < 40 && scrolled.length === 0; i++) {
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
      expect(scrolled).toContain('Budgets|{"inline":"center","block":"nearest"}');
    } finally {
      HTMLElement.prototype.scrollIntoView = original;
    }
  });

  it('the nav fades at its right edge only while it overflows', async () => {
    signIn(['requests.read']);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    const nav = (fixture.nativeElement as HTMLElement).querySelector('nav')!;
    expect(nav.classList.contains('nav-fade-end')).toBe(false);
    Object.defineProperty(nav, 'scrollWidth', { configurable: true, value: 600 });
    Object.defineProperty(nav, 'clientWidth', { configurable: true, value: 358 });
    nav.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(nav.classList.contains('nav-fade-end')).toBe(true);
    Object.defineProperty(nav, 'scrollLeft', { configurable: true, value: 242 });
    nav.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(nav.classList.contains('nav-fade-end')).toBe(false);
  });

  it('scrolls the active nav link into view after a navigation (the phone nav scrolls sideways)', async () => {
    signIn(['requests.read', 'orders.read', 'invoices.read', 'budgets.read']);
    const scrolled: string[] = [];
    const original = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = function (this: Element, options?: boolean | ScrollIntoViewOptions) {
      scrolled.push(this.textContent?.trim() + '|' + JSON.stringify(options));
    };
    try {
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      const router = TestBed.inject(Router);
      router.resetConfig([{ path: 'app/budgets', children: [] }]);
      await router.navigateByUrl('/app/budgets');
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 30));
      expect(scrolled).toContain('Budgets|{"inline":"center","block":"nearest"}');
    } finally {
      HTMLElement.prototype.scrollIntoView = original;
    }
  });

  it('the nav fades at its right edge only while it overflows', async () => {
    signIn(['requests.read']);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    const nav = (fixture.nativeElement as HTMLElement).querySelector('nav')!;
    expect(nav.classList.contains('nav-fade-end')).toBe(false);
    Object.defineProperty(nav, 'scrollWidth', { configurable: true, value: 600 });
    Object.defineProperty(nav, 'clientWidth', { configurable: true, value: 358 });
    nav.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(nav.classList.contains('nav-fade-end')).toBe(true);
    Object.defineProperty(nav, 'scrollLeft', { configurable: true, value: 242 });
    nav.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(nav.classList.contains('nav-fade-end')).toBe(false);
  });

  it('signed out shows only Log in and Register and no live widgets', async () => {
    const el = await render();
    expect(text(el.querySelector('header'))).toContain('Log in');
    expect(el.querySelector('[data-testid=live-indicator]')).toBeNull();
    expect(el.querySelector('[data-testid=bell]')).toBeNull();
  });

  it.each([
    ['connected', 'Live'],
    ['reconnecting', 'Reconnecting…'],
    ['offline', 'Offline'],
  ] as const)('the live indicator says %s -> %s', async (live, label) => {
    signIn(['requests.read']);
    state.set(live);
    const el = await render();
    expect(text(el.querySelector('[data-testid=live-indicator]'))).toBe(label);
  });

  it('shows the reconnecting banner only while reconnecting', async () => {
    signIn(['requests.read']);
    state.set('reconnecting');
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el.querySelector('[data-testid=live-banner]'))).toBe('Reconnecting… Live updates paused. Your work is safe.');

    state.set('connected');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid=live-banner]')).toBeNull();
  });

  it('the bell shows the unread count and caps it at 9+', async () => {
    signIn(['requests.read']);
    unread.set(4);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const bell = el.querySelector('[data-testid=bell]')!;
    expect(text(bell)).toBe('4');
    expect(bell.getAttribute('aria-label')).toBe('Notifications, 4 unread');

    unread.set(23);
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid=bell]'))).toBe('9+');
  });

  it('online users: names in the label, none when I am alone', async () => {
    signIn(['requests.read']);
    online.set([{ userId: 'b', name: 'Ben Builder' }, { userId: 'm', name: 'Maria Manager' }]);
    const el = await render();
    expect(el.querySelector('[data-testid=online-users]')?.getAttribute('aria-label')).toBe('Online now: Ben Builder, Maria Manager');

    online.set([]);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid=online-users]')).toBeNull();
  });

  it('the user menu shows name, role and tenant', async () => {
    signIn(['requests.read'], ['manager']);
    const el = await render();
    const trigger = text(el.querySelector('[data-testid=user-menu]'));
    expect(trigger).toContain('Tom Requester');
    expect(trigger).toContain('Manager');
    expect(trigger).toContain('Acme');
  });

  it('marking_read_or_a_push_does_not_trigger_another_list_call', async () => {
    const answer = { unread: 1, items: [{ id: 'n1', text: 't', link: '/requests/r', read: false, createdAt: '2026-10-28T10:00:00Z', requestId: null }] };
    // Every call after the 5th never answers, so a re-running effect (the bug) ends in a failed count, not a loop.
    const list = vi.fn().mockImplementation(() => (list.mock.calls.length > 5 ? new Promise(() => undefined) : Promise.resolve(structuredClone(answer))));
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: NotificationsService, useValue: { listNotifications: list, markNotificationRead: vi.fn().mockResolvedValue(undefined), markAllNotificationsRead: vi.fn() } },
        { provide: AuthStore, useValue: { user, isLoggedIn: () => user() !== null, hasPermission: () => false, logout, logoutAll } },
        { provide: LiveConnection, useValue: { state, on: () => () => undefined, onReconnected: () => () => undefined, onEntityChanged: () => () => undefined, onViewersChanged: () => () => undefined } },
        { provide: PresenceStore, useValue: { online } },
        { provide: ApprovalsStore, useValue: { total: count, refresh: vi.fn() } },
      ],
    });
    // A clock that jumps 31 s at every reading: the list is always stale, so any extra load() call reaches the API.
    let clock = Date.now();
    vi.spyOn(Date, 'now').mockImplementation(() => (clock += 31_000));
    signIn(['requests.read']);
    const fixture = TestBed.createComponent(Shell);
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
