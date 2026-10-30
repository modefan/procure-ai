import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../../api/access/services/access.service';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';
import { costCenterAdminDto, userDto } from '../admin.testing';
import { UsersStore } from './users.store';

describe('UsersStore', () => {
  let api: Record<'listUsers' | 'listCostCenters' | 'setUserCostCenters' | 'setApprovalLimit', ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  const problem = (status: number, detail: string) => new HttpErrorResponse({ status, error: { detail, title: 'x' } });

  beforeEach(() => {
    api = {
      listUsers: vi.fn().mockResolvedValue([userDto('maria'), userDto('ben', { displayName: 'Ben Requester', roles: ['requester'], version: 1 })]),
      listCostCenters: vi.fn().mockResolvedValue([costCenterAdminDto('IT-200'), costCenterAdminDto('MK-110')]),
      setUserCostCenters: vi.fn().mockImplementation(async (p) => userDto('maria', { costCenterCodes: p.body.codes, version: 4 })),
      setApprovalLimit: vi.fn().mockImplementation(async (p) => userDto('maria', { approvalLimit: p.body.amount, version: 5 })),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: api },
        { provide: ConflictDialog, useValue: conflict },
      ],
    });
  });
  const store = () => TestBed.inject(UsersStore);

  it('loads users and cost centers', async () => {
    await store().load();
    expect(store().users().map((u) => u.userId)).toEqual(['maria', 'ben']);
    expect(store().costCenters().length).toBe(2);
  });

  it('edit_saves_cost_centers_with_if_match', async () => {
    await store().load();
    const outcome = await store().save(store().users()[0], ['IT-200', 'MK-110'], 200000);
    expect(outcome).toBe('saved');
    expect(api.setUserCostCenters).toHaveBeenCalledWith({ userId: 'maria', 'If-Match': '"3"', body: { codes: ['IT-200', 'MK-110'] } });
    expect(api.setApprovalLimit).not.toHaveBeenCalled();
  });

  it('edit_saves_limit_with_if_match: after the cost centers, with the version they answered', async () => {
    await store().load();
    const outcome = await store().save(store().users()[0], ['IT-200', 'MK-110'], 350000);
    expect(outcome).toBe('saved');
    expect(api.setApprovalLimit).toHaveBeenCalledWith({ userId: 'maria', 'If-Match': '"4"', body: { amount: 350000 } });
    expect(store().users().find((u) => u.userId === 'maria')?.version).toBe(5);
    expect(store().users().find((u) => u.userId === 'maria')?.approvalLimit).toBe(350000);
  });

  it('a limit-only change sends only the limit, with the user version', async () => {
    await store().load();
    await store().save(store().users()[0], ['IT-200'], 1000);
    expect(api.setUserCostCenters).not.toHaveBeenCalled();
    expect(api.setApprovalLimit.mock.calls[0][0]['If-Match']).toBe('"3"');
  });

  it('conflict_reloads_user: 412 opens the conflict dialog and reads the users again', async () => {
    await store().load();
    api.setApprovalLimit.mockRejectedValue(problem(412, 'old'));
    api.listUsers.mockResolvedValue([userDto('maria', { approvalLimit: 777, version: 9 })]);
    const outcome = await store().save(store().users()[0], ['IT-200'], 1000);
    expect(outcome).toBe('conflict');
    expect(conflict.show).toHaveBeenCalledOnce();
    expect(store().users()[0].version).toBe(9);
    expect(store().users()[0].approvalLimit).toBe(777);
  });

  it('unknown_cost_center_422_shows_field_error: the dialog stays open with the server message', async () => {
    await store().load();
    api.setUserCostCenters.mockRejectedValue(problem(422, 'Unknown cost center: XX-999.'));
    const outcome = await store().save(store().users()[0], ['XX-999'], 200000);
    expect(outcome).toBe('error');
    expect(store().codesError()).toBe('Unknown cost center: XX-999.');
    expect(store().limitError()).toBeNull();
    expect(api.setApprovalLimit).not.toHaveBeenCalled();
    store().clearErrors();
    expect(store().codesError()).toBeNull();
  });

  it('a refused limit after saved cost centers shows the limit error and keeps the saved codes', async () => {
    await store().load();
    api.setApprovalLimit.mockRejectedValue(problem(422, 'Limit too high.'));
    const outcome = await store().save(store().users()[0], ['MK-110'], 5);
    expect(outcome).toBe('error');
    expect(store().limitError()).toBe('Limit too high.');
    expect(store().users().find((u) => u.userId === 'maria')?.costCenterCodes).toEqual(['MK-110']);
  });
});
