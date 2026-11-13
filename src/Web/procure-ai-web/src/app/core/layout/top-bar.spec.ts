import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthStore } from '../auth/auth.store';
import { LiveConnection, LiveState } from '../live/live-connection';
import { NotificationsStore } from '../live/notifications.store';
import { PresenceStore } from '../live/presence.store';
import { SystemAvailability } from '../system/system-availability';
import { TopBar } from './top-bar';

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim();

describe('TopBar', () => {
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
  const unread = signal(0);
  const online = signal<{ userId: string; name: string }[]>([]);
  const switchTenant = vi.fn().mockResolvedValue(undefined);

  const signIn = (permissions: string[], roles = ['requester']) =>
    user.set({ userId: 'u1', name: 'Tom Mokoena', tenantId: 'acme', roles, permissions });

  beforeEach(() => {
    user.set(null);
    state.set('connected');
    unread.set(0);
    online.set([]);
    switchTenant.mockClear();
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
            logout: vi.fn(),
            logoutAll: vi.fn(),
            switchTenant,
          },
        },
        { provide: LiveConnection, useValue: { state } },
        { provide: NotificationsStore, useValue: { items: signal([]), unread, load: vi.fn(), markRead: vi.fn(), markAllRead: vi.fn() } },
        { provide: PresenceStore, useValue: { online } },
      ],
    });
  });

  /** Renders the bar; with a session, waits for the session menu (a deferred chunk) to be there. */
  const render = async () => {
    const fixture = TestBed.createComponent(TopBar);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    if (user() !== null && !TestBed.inject(SystemAvailability).disabled()) {
      await vi.waitFor(() => {
        fixture.detectChanges();
        expect(el.querySelector('[data-testid=user-menu]')).not.toBeNull();
      });
    }
    return { fixture, el };
  };

  it('signed out: no user menu and no live widgets (and no Log in: it lives on the demo page)', async () => {
    const { el } = await render();
    expect(el.querySelector('[data-testid=user-menu]')).toBeNull();
    expect(el.querySelector('[data-testid=live-indicator]')).toBeNull();
    expect(el.querySelector('[data-testid=bell]')).toBeNull();
  });

  it('signed in but the demo is switched off: no session widgets either', async () => {
    signIn(['requests.read']);
    TestBed.inject(SystemAvailability).markDisabled(null);
    const { el } = await render();
    expect(el.querySelector('[data-testid=user-menu]')).toBeNull();
    expect(el.querySelector('[data-testid=bell]')).toBeNull();
  });

  it.each([
    ['connected', 'Live'],
    ['reconnecting', 'Reconnecting…'],
    ['offline', 'Offline'],
  ] as const)('the live indicator says %s -> %s', async (live, label) => {
    signIn(['requests.read']);
    state.set(live);
    const { el } = await render();
    expect(text(el.querySelector('[data-testid=live-indicator]'))).toBe(label);
  });

  it('the bell shows the unread count and caps it at 9+', async () => {
    signIn(['requests.read']);
    unread.set(4);
    const { fixture, el } = await render();
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
    const { fixture, el } = await render();
    expect(el.querySelector('[data-testid=online-users]')?.getAttribute('aria-label')).toBe('Online now: Ben Builder, Maria Naidoo');

    online.set([]);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid=online-users]')).toBeNull();
  });

  it('the user menu shows name, role and tenant', async () => {
    signIn(['requests.read'], ['manager']);
    const trigger = text((await render()).el.querySelector('[data-testid=user-menu]'));
    expect(trigger).toContain('Tom Mokoena');
    expect(trigger).toContain('Manager · Acme');
  });

  it('user_menu_shows_tenant_name and a hyphenated role as words', async () => {
    signIn(['requests.read'], ['department-head']);
    user.update((u) => ({ ...u!, tenantName: 'Acme Corp' }));
    expect(text((await render()).el.querySelector('[data-testid=user-menu]'))).toContain('Department head · Acme Corp');
  });

  describe('switch company', () => {
    const openUserMenu = async () => {
      const { fixture, el } = await render();
      el.querySelector<HTMLButtonElement>('[data-testid=user-menu]')!.click();
      // The menu renders its items on first open (deferred content).
      await vi.waitFor(() => {
        TestBed.tick();
        expect(text(el.querySelector('[role=menu][aria-label=Account]'))).toContain('Log out');
      });
      return { el, fixture };
    };
    const switchItems = (el: HTMLElement) => [...el.querySelectorAll('[data-testid=switch-tenant]')];

    it('menu_lists_tenants_when_more_than_one', async () => {
      signIn(['requests.read'], ['finance']);
      user.update((u) => ({ ...u!, tenantName: 'Acme Corp', tenants: [{ id: 'acme', name: 'Acme Corp' }, { id: 'globex', name: 'Globex' }] }));
      const { el, fixture } = await openUserMenu();

      expect(text(el.querySelector('[data-testid=switch-tenant-heading]'))).toBe('Switch company');
      expect(switchItems(el).map(text)).toEqual(['Globex']);

      (switchItems(el)[0] as HTMLElement).click();
      fixture.detectChanges();
      expect(switchTenant).toHaveBeenCalledWith('globex');
    });

    it('no switch entry with a single tenant', async () => {
      signIn(['requests.read']);
      user.update((u) => ({ ...u!, tenants: [{ id: 'acme', name: 'Acme Corp' }] }));
      const { el } = await openUserMenu();
      expect(el.querySelector('[data-testid=switch-tenant-heading]')).toBeNull();
      expect(switchItems(el)).toEqual([]);
    });
  });
});
