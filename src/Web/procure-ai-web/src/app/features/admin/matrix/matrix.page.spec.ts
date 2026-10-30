import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../../api/access/services/access.service';
import { unsavedChangesGuard } from '../../../core/auth/unsaved-changes.guard';
import { fakeHubFactory } from '../../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../../core/live/hub-connection';
import { ConfirmDialog } from '../../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';
import { APP_ROUTES } from '../../app.routes';
import { matrixDto } from '../admin.testing';
import { MatrixPage } from './matrix.page';

describe('MatrixPage', () => {
  let api: Record<'getMatrix' | 'setRolePermissions', ReturnType<typeof vi.fn>>;
  let confirm: { ask: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  async function setup() {
    api = { getMatrix: vi.fn().mockResolvedValue(matrixDto()), setRolePermissions: vi.fn() };
    confirm = { ask: vi.fn().mockResolvedValue(true) };
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: api },
        { provide: ConfirmDialog, useValue: confirm },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(MatrixPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  const box = (el: HTMLElement, role: string, permission: string) =>
    el.querySelector<HTMLInputElement>(`input[type="checkbox"][aria-label="${role}: ${permission}"]`)!;
  const saveButton = (el: HTMLElement) => [...el.querySelectorAll('button')].find((b) => text(b).startsWith('Save changes'))!;

  it('shows all five roles and the permissions grouped by area, with a description each', async () => {
    const { el } = await setup();
    const headers = [...el.querySelectorAll('thead th')].map((th) => text(th));
    expect(headers).toEqual(['Permission', 'Requester', 'Manager', 'Department head', 'Finance', 'Admin']);
    expect([...el.querySelectorAll('tbody th[scope="colgroup"]')].map((th) => text(th))).toEqual([
      'Requests',
      'Orders',
      'Invoices',
      'Budgets',
      'Assistant',
      'Admin',
    ]);
    expect(el.querySelectorAll('tbody input[type="checkbox"]').length).toBe(15 * 5);
    const approve = el.querySelector('tr[data-permission="requests.approve"]')!;
    expect(approve.querySelector('code')?.textContent).toBe('requests.approve');
    expect(text(approve)).toContain('Approve or reject');
    expect(text(el)).toContain('Permissions are defined in code');
    expect(text(el)).toContain('Audited. Services pick up changes within seconds.');
  });

  it('checkboxes reflect the loaded matrix and are labelled with role and permission', async () => {
    const { el } = await setup();
    expect(box(el, 'Manager', 'requests.approve').checked).toBe(true);
    expect(box(el, 'Requester', 'requests.approve').checked).toBe(false);
    expect(box(el, 'Department head', 'requests.approve').checked).toBe(true);
  });

  it('Save changes is disabled until something changes, then saves one PUT per changed role', async () => {
    const { fixture, el } = await setup();
    expect(saveButton(el).disabled).toBe(true);

    box(el, 'Requester', 'requests.approve').click();
    fixture.detectChanges();
    expect(saveButton(el).disabled).toBe(false);

    const base = matrixDto();
    api.setRolePermissions.mockResolvedValue(
      matrixDto({ version: 5, roles: base.roles.map((r) => (r.role === 'requester' ? { ...r, permissions: [...r.permissions, 'requests.approve'] } : r)) }),
    );
    saveButton(el).click();
    await settle(fixture);

    expect(api.setRolePermissions).toHaveBeenCalledOnce();
    expect(api.setRolePermissions.mock.calls[0][0]).toMatchObject({ role: 'requester', 'If-Match': '"4"' });
    expect(saveButton(el).disabled).toBe(true);
    expect(text(el.querySelector('[data-testid="saved"]'))).toContain('Requester');
  });

  it('shows the server text when the save is refused (lockout)', async () => {
    const { fixture, el } = await setup();
    box(el, 'Admin', 'admin.authorization').click();
    fixture.detectChanges();
    api.setRolePermissions.mockRejectedValue(
      new HttpErrorResponse({ status: 422, error: { detail: 'The admin role must keep admin.authorization.', title: 'x' } }),
    );

    saveButton(el).click();
    await settle(fixture);

    expect(text(el.querySelector('[data-testid="action-error"]'))).toContain('must keep admin.authorization');
  });

  it('Discard puts every box back', async () => {
    const { fixture, el } = await setup();
    box(el, 'Finance', 'assistant.use').click();
    fixture.detectChanges();
    expect(box(el, 'Finance', 'assistant.use').checked).toBe(false);

    [...el.querySelectorAll('button')].find((b) => text(b) === 'Discard')!.click();
    fixture.detectChanges();

    expect(box(el, 'Finance', 'assistant.use').checked).toBe(true);
  });

  it('leaving_dirty_asks_unsaved_changes', async () => {
    const { fixture, el } = await setup();
    const page = fixture.componentInstance;
    const leave = () => TestBed.runInInjectionContext(() => unsavedChangesGuard(page, null as never, null as never, null as never));

    expect(page.hasUnsavedChanges()).toBe(false);
    expect(await leave()).toBe(true);
    expect(confirm.ask).not.toHaveBeenCalled();

    box(el, 'Finance', 'assistant.use').click();
    fixture.detectChanges();
    expect(page.hasUnsavedChanges()).toBe(true);
    confirm.ask.mockResolvedValue(false);
    expect(await leave()).toBe(false);
    expect(confirm.ask).toHaveBeenCalledOnce();
  });

  it('loading error offers Try again', async () => {
    api = { getMatrix: vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 500 })), setRolePermissions: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: api },
        { provide: ConfirmDialog, useValue: { ask: vi.fn() } },
        { provide: ConflictDialog, useValue: { show: vi.fn() } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(MatrixPage);
    fixture.detectChanges();
    await settle(fixture);
    expect(text(fixture.nativeElement.querySelector('[role="alert"]'))).toContain('could not be loaded');
  });
});

describe('APP_ROUTES authorization', () => {
  it('page_hidden_without_admin_authorization: the route needs admin.authorization and guards leaving', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'admin/authorization');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('admin.authorization');
    expect(route?.canActivate?.length).toBe(1);
    expect(route?.canDeactivate).toEqual([unsavedChangesGuard]);
    expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
  });
});
