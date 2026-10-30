import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { SystemStatusService } from '../../../api/notifications/services/system-status.service';
import { SupplierChaosService } from '../../../api/purchasing/services/supplier-chaos.service';
import { permissionGuard } from '../../../core/auth/permission.guard';
import { APP_ROUTES } from '../../app.routes';
import { serviceStatus, statusDto } from './system-status.testing';
import { SystemStatusPage } from './system-status.page';

describe('SystemStatusPage', () => {
  let status: { getSystemStatus: ReturnType<typeof vi.fn> };
  let chaos: { setSupplierChaos: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const NOW = Date.parse('2026-10-30T10:00:00Z');

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 4; i++) {
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
    }
  }

  async function setup(dto = statusDto()) {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout', 'Date'] });
    vi.setSystemTime(NOW);
    status = { getSystemStatus: vi.fn().mockResolvedValue(dto) };
    chaos = { setSupplierChaos: vi.fn().mockImplementation(async (p) => ({ mode: p.body.mode, restoresAt: p.body.mode === 'Normal' ? null : '2026-10-30T10:04:12Z' })) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: SystemStatusService, useValue: status },
        { provide: SupplierChaosService, useValue: chaos },
      ],
    });
    const fixture = TestBed.createComponent(SystemStatusPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  afterEach(() => vi.useRealTimers());

  it('status_table_shows_breaker_open_with_retry', async () => {
    const { el } = await setup(
      statusDto({ services: [serviceStatus('Purchasing', { health: 'Degraded', breakers: [{ name: 'Supplier API', state: 'Open', retryAt: '2026-10-30T10:00:20Z' }] })] }),
    );
    const row = text(el.querySelector('tr[data-service="Purchasing"]'));
    expect(row).toContain('Degraded');
    expect(row).toContain('Supplier API: OPEN (retry in 20 s)');
  });

  it('down_service_shows_down_badge', async () => {
    const { el } = await setup(statusDto({ services: [serviceStatus('Invoicing', { health: 'Down', instances: 0 })] }));
    const badge = el.querySelector('tr[data-service="Invoicing"] [data-testid="health"]')!;
    expect(text(badge)).toBe('Down');
    expect(badge.className).toContain('badge-error');
  });

  it('shows the stat line with zero defaults and dead letters once', async () => {
    const { el } = await setup(statusDto({ services: [serviceStatus('A')] }));
    const stats = text(el.querySelector('[data-testid="stats"]'));
    expect(stats).toContain('Sagas running: 14');
    expect(stats).toContain('Timeouts due next hour: 2');
    expect(stats).toContain('Outbox backlog: 0');
    expect(stats).toContain('Dead letters: 0 (broker 0)');
    expect(stats).toContain('Duplicate requests coalesced today: 37');
    expect(stats).toContain('Guard blocks today: 3');
  });

  it('chaos_shows_countdown: Failing with restoresAt shows "restores in 4:12", ticking each second, and Restore now', async () => {
    const { fixture, el } = await setup(statusDto({ supplierChaos: { mode: 'Failing', restoresAt: '2026-10-30T10:04:12Z' } }));
    expect(text(el.querySelector('[data-testid="chaos"]'))).toContain('restores in 4:12');
    await vi.advanceTimersByTimeAsync(1000);
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid="chaos"]'))).toContain('restores in 4:11');
    expect([...el.querySelectorAll('button')].some((b) => text(b) === 'Restore now')).toBe(true);
  });

  it('pressing the chaos button sends Failing, then shows the countdown', async () => {
    const { fixture, el } = await setup();
    [...el.querySelectorAll('button')].find((b) => text(b).startsWith('Break the supplier API'))!.click();
    await settle(fixture);
    expect(chaos.setSupplierChaos.mock.calls[0][0].body).toEqual({ mode: 'Failing' });
    expect(text(el.querySelector('[data-testid="chaos"]'))).toContain('restores in 4:12');
  });

  it('restore_now_sets_normal', async () => {
    const { fixture, el } = await setup(statusDto({ supplierChaos: { mode: 'Failing', restoresAt: '2026-10-30T10:04:12Z' } }));
    [...el.querySelectorAll('button')].find((b) => text(b) === 'Restore now')!.click();
    await settle(fixture);
    expect(chaos.setSupplierChaos.mock.calls[0][0].body).toEqual({ mode: 'Normal' });
    expect(text(el.querySelector('[data-testid="chaos"]'))).not.toContain('restores in');
  });

  it('refresh_pauses_when_hidden: reads every 5 s while visible and not while hidden', async () => {
    let visibility: DocumentVisibilityState = 'visible';
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);
    await setup();
    expect(status.getSystemStatus).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(5000);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(2);
    visibility = 'hidden';
    await vi.advanceTimersByTimeAsync(10000);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(2);
  });

  it('outside_app_alert_links_to_filtered_log', async () => {
    const { el } = await setup(
      statusDto({
        alerts: [
          { kind: 'outside-app-change', text: 'orders (purchasing) changed outside the app, row 7', tenantId: 'acme', at: '2026-10-30T09:59:00Z', link: '/app/admin/data-changes?outside=true&table=orders' },
          { kind: 'finance-overdue', text: 'PR-1043: finance step overdue since 2026-10-29 14:00', tenantId: 'acme', at: '2026-10-29T14:00:00Z', link: '/app/requests/1043' },
        ],
      }),
    );
    const link = el.querySelector<HTMLAnchorElement>('[data-testid="alerts"] a[href*="data-changes"]')!;
    expect(link.getAttribute('href')).toBe('/app/admin/data-changes?outside=true&table=orders');
    expect(el.querySelector('[data-testid="alerts"] a[href="/app/requests/1043"]')).not.toBeNull();
  });

  it('shows the open alert counters and readable kind labels', async () => {
    const { el } = await setup(
      statusDto({
        counters: { finance_alerts_open: 1, outside_app_alerts_open: 2 },
        alerts: [
          { kind: 'finance-overdue', text: 'PR-1043: finance step overdue since 2026-10-29 14:00', tenantId: 'acme', at: '2026-10-29T14:00:00Z', link: '/app/requests/1043' },
          { kind: 'outside-app-change', text: 'orders changed', tenantId: 'acme', at: '2026-10-30T09:00:00Z', link: null },
        ],
      }),
    );
    const stats = text(el.querySelector('[data-testid="stats"]'));
    expect(stats).toContain('Open finance alerts: 1');
    expect(stats).toContain('Changed outside the app: 2');
    const alerts = text(el.querySelector('[data-testid="alerts"]'));
    expect(alerts).toContain('Finance step overdue');
    expect(alerts).toContain('Changed outside the app');
    expect(alerts).not.toContain('finance-overdue');
  });

  it('shows the pgaudit block and the Aspire dashboard link', async () => {
    const { el } = await setup(
      statusDto({
        counters: { pgaudit_ddl: 2, pgaudit_write: 7 },
        alerts: [{ kind: 'pgaudit', text: 'postgres in requests: CREATE TABLE x (a int)', tenantId: null, at: '2026-10-30T09:59:00Z', link: null }],
      }),
    );
    const block = text(el.querySelector('[data-testid="pgaudit"]'));
    expect(block).toContain('ddl: 2');
    expect(block).toContain('write: 7');
    expect(block).toContain('CREATE TABLE x (a int)');
    expect(el.querySelector('[data-testid="alerts"]')?.textContent ?? '').not.toContain('CREATE TABLE');
    const aspire = el.querySelector<HTMLAnchorElement>('a[data-testid="aspire"]')!;
    expect(aspire.getAttribute('href')).toBe('https://localhost:17100');
    expect(aspire.getAttribute('rel')).toContain('noopener');
  });

  it('pages_hidden_without_admin_system: the routes need admin.system behind the permission guard', async () => {
    for (const path of ['admin/system', 'admin/conversations', 'admin/conversations/:id']) {
      const route = APP_ROUTES.find((r) => r.path === path);
      expect(route, path).toBeDefined();
      expect(route?.data?.['permission']).toBe('admin.system');
      expect(route?.canActivate).toContain(permissionGuard);
      expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
    }
  });
});
