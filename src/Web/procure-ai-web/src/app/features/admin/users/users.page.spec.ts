import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../../api/access/services/access.service';
import { AuthStore } from '../../../core/auth/auth.store';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';
import { APP_ROUTES } from '../../app.routes';
import { costCenterAdminDto, userDto } from '../admin.testing';
import { UsersPage } from './users.page';

describe('UsersPage', () => {
  let api: Record<'listUsers' | 'listCostCenters' | 'setUserCostCenters' | 'setApprovalLimit', ReturnType<typeof vi.fn>>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  async function setup() {
    api = {
      listUsers: vi.fn().mockResolvedValue([
        userDto('maria'),
        userDto('fiona', { displayName: 'Fiona Finance', email: 'fiona@acme.test', roles: ['finance'], costCenterCodes: [], approvalLimit: 1000000, isFinanceApprover: true }),
      ]),
      listCostCenters: vi.fn().mockResolvedValue([
        costCenterAdminDto('IT-200'),
        costCenterAdminDto('MK-110', { name: 'Marketing', departmentCode: 'MK', departmentName: 'Marketing', managerName: null, departmentHeadName: null }),
      ]),
      setUserCostCenters: vi.fn().mockImplementation(async (p) => userDto('maria', { costCenterCodes: p.body.codes, version: 4 })),
      setApprovalLimit: vi.fn().mockImplementation(async (p) => userDto('maria', { approvalLimit: p.body.amount, version: 5 })),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: api },
        { provide: AuthStore, useValue: { hasPermission: () => true, user: signal({ userId: 'admin', name: 'Admin' }) } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
      ],
    });
    const fixture = TestBed.createComponent(UsersPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  type Fixture = Awaited<ReturnType<typeof setup>>['fixture'];
  const row = (el: HTMLElement, id: string) => el.querySelector(`tr[data-user="${id}"]`)!;
  async function openEdit(fixture: Fixture, id = 'maria') {
    row(fixture.nativeElement, id).querySelector<HTMLButtonElement>('button')!.click();
    await settle(fixture);
  }
  async function typeLimit(fixture: Fixture, value: string) {
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#user-limit')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  const save = (el: HTMLElement) => [...el.querySelectorAll('dialog button')].find((b) => text(b).startsWith('Save')) as HTMLButtonElement;

  it('users_table_shows_limit_in_rand: users with roles, cost centers, limit and the Finance badge; cost centers below', async () => {
    const { el } = await setup();
    const maria = text(row(el, 'maria'));
    expect(maria).toContain('Maria Manager');
    expect(maria).toContain('maria@acme.test');
    expect(maria).toContain('Manager');
    expect(maria).toContain('IT-200');
    expect(maria).toContain('R 200 000,00');
    expect(text(row(el, 'fiona'))).toContain('R 1 000 000,00');
    expect(text(row(el, 'fiona'))).toContain('Finance approver');
    expect(text(row(el, 'maria'))).not.toContain('Finance approver');
    expect(text(el)).toContain('managed in Keycloak');
    expect(text(el.querySelector('[data-testid="cost-centers"]'))).toContain('Marketing');
    expect(text(el.querySelector('[data-testid="cost-centers"]'))).toContain('Maria Manager');
  });

  it('edit_saves_cost_centers_with_if_match: ticking a cost center and saving', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    el.querySelector<HTMLElement>('dialog [role="option"][data-code="MK-110"]')!.click();
    await settle(fixture);
    save(el).click();
    await settle(fixture);
    expect(api.setUserCostCenters).toHaveBeenCalledWith({ userId: 'maria', 'If-Match': '"3"', body: { codes: ['IT-200', 'MK-110'] } });
    expect(el.querySelector('dialog')).toBeNull();
  });

  it('edit_saves_limit_with_if_match: accepts 350 000,00 as typed in en-ZA', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    await typeLimit(fixture, '350 000,00');
    save(el).click();
    await settle(fixture);
    expect(api.setApprovalLimit).toHaveBeenCalledWith({ userId: 'maria', 'If-Match': '"3"', body: { amount: 350000 } });
    expect(api.setUserCostCenters).not.toHaveBeenCalled();
  });

  it('accepts a plain number like 250000', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    await typeLimit(fixture, '250000');
    save(el).click();
    await settle(fixture);
    expect(api.setApprovalLimit.mock.calls[0][0].body.amount).toBe(250000);
  });

  it('limit_validation_rejects_negative: and text, too large and empty; nothing is sent', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    for (const bad of ['-5', 'abc', '1 000 000 001', '']) {
      await typeLimit(fixture, bad);
      save(el).click();
      await settle(fixture);
      expect(el.querySelector('#user-limit-error'), bad).not.toBeNull();
    }
    expect(api.setApprovalLimit).not.toHaveBeenCalled();
    expect(el.querySelector('dialog')).not.toBeNull();
  });

  it('conflict_reloads_user: the dialog closes and the list shows the latest', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    await typeLimit(fixture, '1000');
    api.setApprovalLimit.mockRejectedValue(new HttpErrorResponse({ status: 412, error: { detail: 'old' } }));
    api.listUsers.mockResolvedValue([userDto('maria', { approvalLimit: 777, version: 9 })]);
    save(el).click();
    await settle(fixture);
    expect(el.querySelector('dialog')).toBeNull();
    expect(text(row(el, 'maria'))).toContain('R 777,00');
  });

  it('unknown_cost_center_422_shows_field_error: next to the cost center list, dialog stays open', async () => {
    const { fixture, el } = await setup();
    await openEdit(fixture);
    el.querySelector<HTMLElement>('dialog [role="option"][data-code="MK-110"]')!.click();
    await settle(fixture);
    api.setUserCostCenters.mockRejectedValue(new HttpErrorResponse({ status: 422, error: { detail: 'Unknown cost center: MK-110.' } }));
    save(el).click();
    await settle(fixture);
    expect(text(el.querySelector('#user-codes-error'))).toBe('Unknown cost center: MK-110.');
    expect(el.querySelector('dialog')).not.toBeNull();
  });
});

describe('APP_ROUTES users', () => {
  it('page_hidden_without_admin_users: the route needs admin.users', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'admin/users');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('admin.users');
    expect(route?.canActivate?.length).toBe(1);
    expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
  });
});
