import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { ApprovalsStore } from '../approvals/approvals.store';
import { LiveConnection, LiveState } from '../../core/live/live-connection';
import { NotificationsStore } from '../../core/live/notifications.store';
import { NotificationsService } from '../../api/notifications/services/notifications.service';
import { PresenceStore } from '../../core/live/presence.store';
import { CacheRegistry } from '../../core/state/cache-registry';
import { AppConfigService } from '../../core/config/app-config';
import { DevPanelStore } from '../../core/dev-panel/dev-panel.store';
import { Shell } from './shell';

describe('Shell header', () => {
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
  const items = signal<{ id: string; text: string; link: string; read: boolean; createdAt: string; requestId: string | null }[]>([]);
  const unread = signal(0);
  const count = signal(0);
  const online = signal<{ userId: string; name: string }[]>([]);
  const markRead = vi.fn().mockResolvedValue(undefined);
  const markAllRead = vi.fn().mockResolvedValue(undefined);
  const logout = vi.fn();
  const logoutAll = vi.fn();
  const switchTenant = vi.fn().mockResolvedValue(undefined);
  const switchError = signal<string | null>(null);
  const dismissSwitchError = vi.fn(() => switchError.set(null));

  const signIn = (permissions: string[], roles = ['requester']) =>
    user.set({ userId: 'u1', name: 'Tom Mokoena', tenantId: 'acme', roles, permissions });

  beforeEach(() => {
    user.set(null);
    state.set('connected');
    items.set([]);
    unread.set(0);
    count.set(0);
    online.set([]);
    markRead.mockClear();
    switchError.set(null);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthStore,
          useValue: {
            user,
            isLoggedIn: () => user() !== null,
            hasPermission: (p: string) => user()?.permissions.includes(p) ?? false,
            tenants: () => user()?.tenants ?? [],
            logout,
            logoutAll,
            switchTenant,
            switchError,
            dismissSwitchError,
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
    online.set([{ userId: 'b', name: 'Ben Builder' }, { userId: 'm', name: 'Maria Naidoo' }]);
    const el = await render();
    expect(el.querySelector('[data-testid=online-users]')?.getAttribute('aria-label')).toBe('Online now: Ben Builder, Maria Naidoo');

    online.set([]);
    const fixture = TestBed.createComponent(Shell);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid=online-users]')).toBeNull();
  });

  it('the user menu shows name, role and tenant', async () => {
    signIn(['requests.read'], ['manager']);
    const el = await render();
    const trigger = text(el.querySelector('[data-testid=user-menu]'));
    expect(trigger).toContain('Tom Mokoena');
    expect(trigger).toContain('Manager');
    expect(trigger).toContain('Acme');
  });

  it('user_menu_shows_tenant_name', async () => {
    signIn(['requests.read'], ['manager']);
    user.update((u) => ({ ...u!, tenantName: 'Acme Corp' }));
    const named = text((await render()).querySelector('[data-testid=user-menu]'));
    expect(named).toContain('Manager · Acme Corp');
    TestBed.resetTestingModule();
  });

  it('user_menu_falls_back_to_the_tenant_id_when_the_name_is_unknown', async () => {
    signIn(['requests.read'], ['manager']);
    user.update((u) => ({ ...u!, tenantName: null }));
    expect(text((await render()).querySelector('[data-testid=user-menu]'))).toContain('Manager · Acme');
  });

  describe('switch company', () => {
    const openUserMenu = async () => {
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      el.querySelector<HTMLButtonElement>('[data-testid=user-menu]')!.click();
      // The menu renders its items on first open (deferred content).
      await vi.waitFor(() => {
        TestBed.tick();
        expect(text(el.querySelector('[role=menu][aria-label=Account]'))).toContain('Log out');
      });
      return { el, fixture };
    };
    const switchItems = (el: HTMLElement) => [...el.querySelectorAll('[data-testid=switch-tenant]')];

    beforeEach(() => switchTenant.mockClear());

    it('menu_lists_tenants_when_more_than_one', async () => {
      signIn(['requests.read'], ['finance']);
      user.update((u) => ({ ...u!, tenantName: 'Acme Corp', tenants: [{ id: 'acme', name: 'Acme Corp' }, { id: 'globex', name: 'Globex' }] }));
      const { el, fixture } = await openUserMenu();

      expect(text(el.querySelector('[data-testid=switch-tenant-heading]'))).toBe('Switch company');
      // Only the other companies are offered; the current one is the one shown under the name.
      expect(switchItems(el).map(text)).toEqual(['Globex']);

      (switchItems(el)[0] as HTMLElement).click();
      fixture.detectChanges();
      expect(switchTenant).toHaveBeenCalledWith('globex');
    });

    it('switch_refused_shows_a_toast_that_can_be_closed', async () => {
      signIn(['requests.read'], ['finance']);
      const el = await render();
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

    it('no switch entry with a single tenant', async () => {
      signIn(['requests.read'], ['requester']);
      user.update((u) => ({ ...u!, tenants: [{ id: 'acme', name: 'Acme Corp' }] }));
      const { el } = await openUserMenu();

      expect(el.querySelector('[data-testid=switch-tenant-heading]')).toBeNull();
      expect(switchItems(el)).toEqual([]);
      expect(text(el.querySelector('[role=menu][aria-label=Account]'))).toContain('Log out');
    });
  });

  describe('admin group', () => {
    const adminTrigger = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('[data-testid=admin-menu]');
    const adminLinks = (el: HTMLElement) => [...el.querySelectorAll('[data-testid=admin-links] a')].map((a) => text(a));

    it('page_hidden_without_admin_authorization: no Admin menu for a user without an admin permission', async () => {
      signIn(['requests.read', 'budgets.manage']);
      expect(adminTrigger(await render())).toBeNull();
    });

    it('lists only the admin pages the user may open', async () => {
      signIn(['requests.read', 'admin.policies']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('[data-testid=admin-links]')).toBeNull();

      adminTrigger(el)!.click();
      fixture.detectChanges();

      expect(adminTrigger(el)!.getAttribute('aria-expanded')).toBe('true');
      expect(adminLinks(el)).toEqual(['Policies']);
    });

    it('an admin sees Authorization before Policies, and Escape closes the list', async () => {
      signIn(['admin.authorization', 'admin.policies'], ['admin']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;

      adminTrigger(el)!.click();
      fixture.detectChanges();
      expect(adminLinks(el)).toEqual(['Authorization', 'Access check', 'Policies']);
      expect(el.querySelector('[data-testid=admin-links] a[href="/app/admin/authorization"]')).not.toBeNull();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      expect(el.querySelector('[data-testid=admin-links]')).toBeNull();
      expect(adminTrigger(el)!.getAttribute('aria-expanded')).toBe('false');
    });
  });

  describe('admin menu accessibility (final review F7)', () => {
    const adminTrigger = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('[data-testid=admin-menu]');

    it('points aria-controls only at a list that exists, and Escape returns focus to the trigger', async () => {
      signIn(['admin.authorization', 'admin.policies'], ['admin']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      const trigger = adminTrigger(el)!;
      expect(trigger.getAttribute('aria-controls')).toBeNull();

      trigger.click();
      fixture.detectChanges();
      const controls = trigger.getAttribute('aria-controls');
      expect(controls).toBe('admin-links');
      expect(el.querySelector(`#${controls}`)).not.toBeNull();

      el.querySelector<HTMLAnchorElement>('[data-testid=admin-links] a')!.focus();
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      expect(el.querySelector('[data-testid=admin-links]')).toBeNull();
      expect(document.activeElement).toBe(trigger);
    });

    it('Escape with the list closed leaves focus where it is', async () => {
      signIn(['admin.authorization'], ['admin']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      const other = el.querySelector<HTMLButtonElement>('[data-testid=user-menu]')!;
      other.focus();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      expect(document.activeElement).toBe(other);
    });
  });

  describe('phone menu', () => {
    beforeEach(() => {
      HTMLDialogElement.prototype.showModal = function () {
        this.setAttribute('open', '');
      };
      HTMLDialogElement.prototype.close = function () {
        this.removeAttribute('open');
        this.dispatchEvent(new Event('close'));
      };
    });

    const setupPhone = async () => {
      signIn(['requests.read', 'requests.approve', 'orders.read']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      const settle = async () => {
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
      };
      return { fixture, el, settle, dialog: () => el.querySelector('dialog[data-testid=phone-drawer]') as HTMLDialogElement, button: () => el.querySelector('[data-testid=phone-menu]') as HTMLButtonElement };
    };

    it('phone_menu_opens_drawer_and_closes_on_navigation', async () => {
      const { el, settle, dialog, button } = await setupPhone();
      TestBed.inject(Router).resetConfig([{ path: 'app/orders', children: [] }]);
      expect(dialog().hasAttribute('open')).toBe(false);
      expect(button().getAttribute('aria-expanded')).toBe('false');

      button().click();
      await settle();

      expect(dialog().hasAttribute('open')).toBe(true);
      expect(button().getAttribute('aria-expanded')).toBe('true');
      const links = [...dialog().querySelectorAll('nav a')].map((a) => text(a));
      expect(links).toEqual(['Dashboard', 'Requests', expect.stringContaining('Approvals'), 'Orders']);
      expect(dialog().querySelector('nav')?.getAttribute('aria-label')).toBe('Menu');

      (dialog().querySelector('nav a[href="/app/orders"]') as HTMLAnchorElement).click();
      await new Promise((r) => setTimeout(r, 30));
      await settle();

      expect(dialog().hasAttribute('open')).toBe(false);
      expect(el.querySelector('dialog[data-testid=phone-drawer] nav a')).toBeNull(); // the links are only built while it is open
      expect(button().getAttribute('aria-expanded')).toBe('false');
    });

    it('the drawer lists the admin pages under an Admin heading', async () => {
      signIn(['requests.read', 'admin.authorization', 'admin.policies'], ['admin']);
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      el.querySelector<HTMLButtonElement>('[data-testid=phone-menu]')!.click();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();

      const group = el.querySelector('dialog[data-testid=phone-drawer] [data-testid=drawer-admin]')!;
      expect(text(group.querySelector('h2'))).toBe('Admin');
      expect([...group.querySelectorAll('a')].map((a) => text(a))).toEqual(['Authorization', 'Access check', 'Policies']);
    });

    it('closes with its close button and with Esc (the native close event)', async () => {
      const { settle, dialog, button } = await setupPhone();
      button().click();
      await settle();
      (dialog().querySelector('[data-testid=phone-menu-close]') as HTMLButtonElement).click();
      await settle();
      expect(dialog().hasAttribute('open')).toBe(false);

      button().click();
      await settle();
      dialog().close(); // what Esc does
      await settle();
      expect(button().getAttribute('aria-expanded')).toBe('false');
    });

    it('a click on the backdrop closes it', async () => {
      const { settle, dialog, button } = await setupPhone();
      button().click();
      await settle();
      dialog().click(); // the target is the dialog itself, not its content
      await settle();
      expect(dialog().hasAttribute('open')).toBe(false);
    });
  });

  describe('refresh failure banner', () => {
    it('refresh_failure_banner_shows_and_retries', async () => {
      signIn(['requests.read']);
      const failing = signal(false);
      const revalidateAll = vi.fn(() => failing.set(false));
      TestBed.inject(CacheRegistry).register({ clearCache: vi.fn(), revalidateAll, revalidateError: () => failing() });
      const fixture = TestBed.createComponent(Shell);
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
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

    it('is not shown to a signed-out visitor', async () => {
      const failing = signal(true);
      TestBed.inject(CacheRegistry).register({ clearCache: vi.fn(), revalidateError: () => failing() });
      expect((await render()).querySelector('[data-testid=refresh-banner]')).toBeNull();
    });
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
        { provide: AuthStore, useValue: { user, isLoggedIn: () => user() !== null, hasPermission: () => false, logout, logoutAll, switchError: () => null, dismissSwitchError: vi.fn() } },
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

  describe('dev panel', () => {
    const keydown = (init: KeyboardEventInit) => document.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, ...init }));
    const enable = (enabled: boolean) => TestBed.inject(AppConfigService).config.update((c) => ({ ...c, devPanel: { enabled } }));

    beforeEach(() => sessionStorage.clear());

    it('alt_shift_d_toggles', async () => {
      signIn(['requests.read']);
      enable(true);
      const el = await render();
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
