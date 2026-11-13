import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ApprovalsStore } from '../../features/approvals/approvals.store';
import { AuthStore } from '../auth/auth.store';
import { AppConfigService, isPageUrl, merge } from '../config/app-config';
import { SystemAvailability } from '../system/system-availability';
import { Menu, MenuItem, MenuSection } from './menu';

/** Every label in menu order, children after their parent (depth first). */
function labels(sections: MenuSection[]): string[] {
  const walk = (items: MenuItem[]): string[] => items.flatMap((item) => [item.label, ...walk(item.children ?? [])]);
  return sections.flatMap((section) => walk(section.items));
}

const TOM = ['requests.read', 'requests.create', 'orders.read', 'invoices.read', 'budgets.read', 'assistant.use'];
const ADMIN = [...TOM, 'admin.authorization', 'admin.users', 'admin.policies', 'admin.system'];

describe('Menu', () => {
  const user = signal<{ permissions: string[] } | null>(null);
  const approvals = signal(0);

  beforeEach(() => {
    user.set(null);
    approvals.set(0);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: AuthStore,
          useValue: { user, isLoggedIn: () => user() !== null, hasPermission: (p: string) => user()?.permissions.includes(p) ?? false },
        },
        { provide: ApprovalsStore, useValue: { total: approvals } },
      ],
    });
  });

  const dashboardUrl = (url: string) =>
    TestBed.inject(AppConfigService).config.update((c) => ({ ...c, observability: { ...c.observability, dashboardUrl: url } }));
  const menu = () => TestBed.inject(Menu).sections();

  it('portfolio_defaults_when_config_has_none', () => {
    const expected = {
      demos: [
        {
          key: 'procurement',
          name: 'ProcureAI',
          pitch: 'Procurement in Rand with an AI policy assistant',
          startUrl: '/demos/procurement',
          infoUrl: '/demos/procurement/about',
          external: false,
        },
      ],
      links: [
        { label: 'LinkedIn', url: 'https://www.linkedin.com/in/dale-abels-95918154/' },
        { label: 'GitHub', url: 'https://github.com/modefan' },
      ],
      reposPublic: false,
      swaggerUrl: '/swagger',
    };
    expect(environment.portfolio).toEqual(expected);
    expect(merge(environment, {}).portfolio).toEqual(expected);
    expect(merge(environment, { portfolio: 'nonsense' }).portfolio).toEqual(expected);
    expect(TestBed.inject(AppConfigService).config().portfolio).toEqual(expected);
  });

  it('signed_out_menu_is_welcome_demos_under_the_hood', () => {
    dashboardUrl('');
    const sections = menu();

    expect(sections.map((s) => s.label)).toEqual([null, 'Demos', 'Under the hood']);
    expect(labels(sections)).toEqual(['Welcome', 'ProcureAI', 'How it is built', 'API (Swagger)']);
    expect(sections[0].items[0]).toMatchObject({ url: '/', external: false });
    expect(sections[1].items[0]).toMatchObject({ url: '/demos/procurement', external: false });
    expect(sections[1].items[0].children![0]).toMatchObject({ url: '/demos/procurement/about', external: false });
    // Swagger is served by the gateway, not the app: a full page load.
    expect(sections[2].items[0]).toMatchObject({ url: '/swagger', external: true });

    dashboardUrl('https://dashboard.example.org');
    const withDashboard = menu();
    expect(labels(withDashboard).at(-1)).toBe('Aspire dashboard');
    expect(withDashboard[2].items.at(-1)).toMatchObject({ url: 'https://dashboard.example.org', external: true });
  });

  it('signed_in_procurement_expands_by_permission', () => {
    dashboardUrl('');
    user.set({ permissions: TOM });
    const procurement = () => menu()[1].items[0];

    expect(procurement().children!.map((c) => c.label)).toEqual(['How it is built', 'Dashboard', 'Requests', 'Orders', 'Invoices', 'Budgets', 'Assistant']);
    expect(procurement().children!.find((c) => c.label === 'Requests')).toMatchObject({ url: '/app/requests', external: false });

    user.set({ permissions: ['requests.read', 'requests.approve'] });
    approvals.set(3);
    expect(procurement().children!.find((c) => c.label === 'Approvals')).toMatchObject({ url: '/app/approvals', badge: 3 });

    user.set({ permissions: ADMIN });
    const admin = procurement().children!.find((c) => c.label === 'Admin');
    expect(admin).toBeDefined();
    expect(admin!.children!.map((c) => c.label)).toEqual([
      'Authorization',
      'Access check',
      'Users',
      'Policies',
      'System status',
      'Conversations',
      'Data changes',
    ]);
    expect(admin!.children!.find((c) => c.label === 'System status')).toMatchObject({ url: '/app/admin/system' });
    // Under the hood stays last, whoever is signed in.
    expect(labels(menu()).at(-1)).toBe('API (Swagger)');
  });

  it('disabled_system_shows_public_items_only', () => {
    dashboardUrl('https://dashboard.example.org');
    user.set({ permissions: ADMIN });
    TestBed.inject(SystemAvailability).markDisabled(null);

    expect(labels(menu())).toEqual(['Welcome', 'ProcureAI', 'How it is built', 'API (Swagger)', 'Aspire dashboard']);
  });

  it('demos come from config: an external demo has no sub-items, unsafe urls are dropped', () => {
    dashboardUrl('');
    const merged = merge(environment, {
      portfolio: {
        demos: [
          environment.portfolio.demos[0],
          { key: 'desk', name: 'SupportDesk', pitch: 'Helpdesk', startUrl: 'https://desk.example.org', infoUrl: '', external: true },
          { key: 'bad', name: 'Bad', pitch: 'x', startUrl: 'javascript:alert(1)', infoUrl: '', external: true },
        ],
        links: [{ label: 'X', url: 'javascript:alert(1)' }, { label: 'GitHub', url: 'https://github.com/modefan' }],
        swaggerUrl: '//evil.example.org',
        reposPublic: true,
      },
    });
    expect(merged.portfolio.demos.map((d) => d.key)).toEqual(['procurement', 'desk']);
    expect(merged.portfolio.links).toEqual([{ label: 'GitHub', url: 'https://github.com/modefan' }]);
    expect(merged.portfolio.swaggerUrl).toBe('/swagger');
    expect(merged.portfolio.reposPublic).toBe(true);

    TestBed.inject(AppConfigService).config.set(merged);
    user.set({ permissions: TOM });
    const desk = menu()[1].items[1];
    expect(desk).toMatchObject({ label: 'SupportDesk', url: 'https://desk.example.org', external: true });
    expect(desk.children ?? []).toEqual([]);
  });

  it('a site path never starts like a protocol-relative address (// or /\\) and has no backslash', () => {
    expect(isPageUrl('/swagger')).toBe(true);
    expect(isPageUrl('/demos/procurement/about')).toBe(true);
    expect(isPageUrl('https://dashboard.example.org')).toBe(true);
    expect(isPageUrl('//evil.org')).toBe(false);
    expect(isPageUrl('/\\evil.org')).toBe(false);
    expect(isPageUrl('/a\\b')).toBe(false);
    expect(merge(environment, { portfolio: { swaggerUrl: '/\\evil.org' } }).portfolio.swaggerUrl).toBe('/swagger');
  });

  it('the Aspire dashboard is called "Aspire dashboard", so a signed-in user does not see two "Dashboard" items', () => {
    dashboardUrl('https://dashboard.example.org');
    user.set({ permissions: TOM });
    const all = labels(menu());
    expect(all.filter((label) => label === 'Dashboard')).toHaveLength(1);
    expect(all.at(-1)).toBe('Aspire dashboard');
  });
});
