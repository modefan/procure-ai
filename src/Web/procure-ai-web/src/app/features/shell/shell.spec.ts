import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { ApprovalsCountStore } from '../../core/live/approvals-count.store';
import { LiveConnection, LiveState } from '../../core/live/live-connection';
import { NotificationsStore } from '../../core/live/notifications.store';
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
        { provide: ApprovalsCountStore, useValue: { count, load: vi.fn().mockResolvedValue(undefined) } },
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
    expect(navLinks(await render())).toEqual(['Welcome', 'Dashboard', 'Requests']);
  });

  it('finance_sees_orders_invoices_and_budgets', async () => {
    signIn(['requests.read', 'orders.read', 'invoices.read', 'budgets.read']);
    expect(navLinks(await render())).toEqual(['Welcome', 'Dashboard', 'Requests', 'Orders', 'Invoices', 'Budgets']);
  });

  it('a_manager_sees_the_approvals_link', async () => {
    signIn(['requests.read', 'requests.approve', 'budgets.read'], ['manager']);
    expect(navLinks(await render())).toEqual(['Welcome', 'Dashboard', 'Requests', expect.stringContaining('Approvals'), 'Budgets']);
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
});
