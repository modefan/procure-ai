import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AccessService } from '../../../api/access/services/access.service';
import { DataChangesService } from '../../../api/audit/services/data-changes.service';
import { permissionGuard } from '../../../core/auth/permission.guard';
import { APP_ROUTES } from '../../app.routes';
import { userDto } from '../admin.testing';
import { dataChangeDto } from './data-changes.testing';
import { DataChangesPage } from './data-changes.page';

describe('DataChangesPage', () => {
  let api: { searchDataChanges: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  const outsideChange = dataChangeDto('c2', { attribution: 'OutsideApp', userId: null, actorId: null, table: 'budgets', database: 'budgetsdb' });

  async function setup(query: Record<string, string> = {}, items = [dataChangeDto('c1'), outsideChange]) {
    api = { searchDataChanges: vi.fn().mockResolvedValue({ items, page: 1, pageSize: 25, total: items.length }) };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: DataChangesService, useValue: api },
        { provide: AccessService, useValue: { listUsers: vi.fn().mockResolvedValue([userDto('tom', { displayName: 'Maria Naidoo' }), userDto('ben', { displayName: 'Ben Buyer' })]) } },
      ],
    });
    const fixture = TestBed.createComponent(DataChangesPage);
    for (const [key, value] of Object.entries(query)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('outside_only_filter_sent: ?outside=true&table=... reaches the API', async () => {
    await setup({ outside: 'true', table: 'purchase_requests' });
    expect(api.searchDataChanges).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, table: 'purchase_requests', outsideOnly: true });
  });

  it('filters_round_trip_through_query: the controls write the query, the query fills the controls and the call', async () => {
    const { fixture, el } = await setup({ service: 'budgetsdb', user: 'ben', from: '2026-10-01', to: '2026-10-30' });
    const call = api.searchDataChanges.mock.lastCall![0] as Record<string, unknown>;
    expect(call).toMatchObject({ database: 'budgetsdb', userId: 'ben' });
    expect(new Date(call['from'] as string).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(new Date(call['to'] as string).getTime()).toBe(new Date(2026, 9, 31).getTime()); // the last day is included
    expect(el.querySelector<HTMLSelectElement>('#dc-service')!.value).toBe('budgetsdb');
    expect(el.querySelector<HTMLSelectElement>('#dc-user')!.value).toBe('ben');
    expect(el.querySelector<HTMLInputElement>('#dc-from')!.value).toBe('2026-10-01');

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const service = el.querySelector<HTMLSelectElement>('#dc-service')!;
    service.value = 'requestsdb';
    service.dispatchEvent(new Event('change'));
    expect(navigate).toHaveBeenLastCalledWith([], { queryParams: { service: 'requestsdb' }, queryParamsHandling: 'merge' });

    const outside = el.querySelector<HTMLInputElement>('#dc-outside')!;
    outside.checked = true;
    outside.dispatchEvent(new Event('change'));
    expect(navigate).toHaveBeenLastCalledWith([], { queryParams: { outside: 'true' }, queryParamsHandling: 'merge' });

    const table = el.querySelector<HTMLInputElement>('#dc-table')!;
    table.value = 'budgets';
    table.dispatchEvent(new Event('change'));
    expect(navigate).toHaveBeenLastCalledWith([], { queryParams: { table: 'budgets' }, queryParamsHandling: 'merge' });

    // The router writes the new query string back into the inputs.
    fixture.componentRef.setInput('service', 'requestsdb');
    fixture.componentRef.setInput('user', undefined);
    await settle(fixture);
    expect(api.searchDataChanges.mock.lastCall![0]).toMatchObject({ database: 'requestsdb' });
    expect(api.searchDataChanges.mock.lastCall![0]).not.toHaveProperty('userId');
  });

  it('shows who changed it: a name or "outside the app"', async () => {
    const { el } = await setup();
    expect(text(el.querySelector('[data-change="c1"]'))).toContain('Maria Naidoo');
    expect(text(el.querySelector('[data-change="c1"]'))).toContain('Requests');
    expect(text(el.querySelector('[data-change="c2"]'))).toContain('Budgets');
    expect(text(el.querySelector('[data-change="c2"] [data-testid="who-outside"]'))).toBe('outside the app');
    expect(el.querySelector('[data-change="c2"] [data-testid="who-outside"]')!.className).toContain('badge-error');
  });

  it('pending_attribution_shown', async () => {
    const { el } = await setup({}, [dataChangeDto('c3', { attribution: 'Pending', userId: null, actorId: null })]);
    expect(text(el.querySelector('[data-change="c3"] [data-testid="who-pending"]'))).toBe('pending');
    expect(el.querySelector('[data-change="c3"] [data-testid="who-outside"]')).toBeNull();
  });

  it('Details expands the old to new diff of a row', async () => {
    const { fixture, el } = await setup();
    expect(el.querySelector('[data-change="c1"] [data-testid="field-diff"]')).toBeNull();
    const button = el.querySelector<HTMLButtonElement>('[data-change="c1"] button')!;
    button.click();
    await settle(fixture);
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(text(el.querySelector('[data-change="c1"] [data-value="title"]'))).toBe('3 laptops → 4 laptops');
  });

  it('says so when nothing matches', async () => {
    const { el } = await setup({ table: 'nope' }, []);
    expect(text(el.querySelector('[data-testid="no-data-changes"]'))).toBe('No data changes match these filters.');
  });

  it('log_hidden_without_admin_system: the route needs admin.system behind the permission guard', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'admin/data-changes');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('admin.system');
    expect(route?.canActivate).toContain(permissionGuard);
    expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
  });
});
