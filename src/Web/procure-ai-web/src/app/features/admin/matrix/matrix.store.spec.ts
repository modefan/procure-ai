import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../../api/access/services/access.service';
import { fakeHubFactory } from '../../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../../core/live/hub-connection';
import { LiveConnection } from '../../../core/live/live-connection';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';
import { matrixDto } from '../admin.testing';
import { MatrixStore } from './matrix.store';

describe('MatrixStore', () => {
  let api: Record<'getMatrix' | 'setRolePermissions', ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;
  type Put = { role: string; body: { permissions: string[] } };

  /** What the server answers to a PUT: the role's permissions replaced, the version one higher. */
  const applying = (start = matrixDto()) => {
    let current = start;
    return vi.fn().mockImplementation(async (p: Put) => {
      current = { ...current, version: current.version + 1, roles: current.roles.map((r) => (r.role === p.role ? { ...r, permissions: p.body.permissions } : r)) };
      return current;
    });
  };
  const problem = (status: number, detail: string) => new HttpErrorResponse({ status, error: { detail, title: 'x' } });
  const pushed = { entity: 'access-matrix', id: 'acme', requestId: null, eventType: 'AccessChanged', occurredAt: '2026-10-30T10:00:00Z' };

  beforeEach(() => {
    api = { getMatrix: vi.fn().mockResolvedValue(matrixDto()), setRolePermissions: applying() };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: api },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
  });
  const store = () => TestBed.inject(MatrixStore);

  it('loads the matrix and starts clean', async () => {
    await store().load();
    expect(store().matrix()?.version).toBe(4);
    expect(store().dirty()).toBe(false);
    expect(store().draft()['manager'].has('requests.approve')).toBe(true);
  });

  it('toggle_marks_dirty_and_reset_clears', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    expect(store().draft()['requester'].has('requests.approve')).toBe(true);
    expect(store().dirty()).toBe(true);
    // Toggling back is clean again.
    store().toggle('requester', 'requests.approve');
    expect(store().dirty()).toBe(false);
    store().toggle('finance', 'assistant.use');
    expect(store().dirty()).toBe(true);
    store().discardChanges();
    expect(store().dirty()).toBe(false);
    expect(store().draft()['finance'].has('assistant.use')).toBe(true);
  });

  it('save_sends_one_put_per_changed_role_in_order', async () => {
    await store().load();
    // Changed out of order on purpose: the PUTs follow the role order of the matrix, each with the version of the answer before.
    store().toggle('finance', 'assistant.use');
    store().toggle('requester', 'requests.approve');
    store().toggle('manager', 'orders.receive');

    expect(await store().save()).toBe(true);

    const calls = api.setRolePermissions.mock.calls.map((c) => c[0]);
    expect(calls.map((c) => c.role)).toEqual(['requester', 'manager', 'finance']);
    expect(calls.map((c) => c['If-Match'])).toEqual(['"4"', '"5"', '"6"']);
    expect(calls[0].body.permissions).toContain('requests.approve');
    expect(calls[2].body.permissions).not.toContain('assistant.use');
    expect(store().matrix()?.version).toBe(7);
    expect(store().dirty()).toBe(false);
    expect(store().savedRoles()).toEqual(['requester', 'manager', 'finance']);
    expect(store().saving()).toBe(false);
  });

  it('save with nothing changed sends nothing', async () => {
    await store().load();
    expect(await store().save()).toBe(false);
    expect(api.setRolePermissions).not.toHaveBeenCalled();
  });

  it('concurrent_save_shows_conflict_and_reloads: conflict dialog keeps typed values on top of the latest matrix', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    store().toggle('admin', 'admin.policies');
    api.setRolePermissions.mockRejectedValueOnce(problem(412, 'The version is old.'));
    // Someone else gave the requester budgets.read meanwhile.
    const latest = matrixDto({
      version: 9,
      roles: matrixDto().roles.map((r) => (r.role === 'requester' ? { ...r, permissions: [...r.permissions, 'budgets.read'] } : r)),
    });
    api.getMatrix.mockResolvedValue(latest);

    expect(await store().save()).toBe(false);

    expect(conflict.show).toHaveBeenCalledOnce();
    expect(conflict.show.mock.calls[0][0]).toContain('Someone else');
    expect(conflict.show.mock.calls[0][0]).toContain('kept');
    expect(store().matrix()?.version).toBe(9);
    // The latest matrix with this admin's own ticks on top: theirs (budgets.read) and ours (requests.approve, admin.policies removed).
    expect(store().dirty()).toBe(true);
    expect([...store().draft()['requester']]).toEqual(expect.arrayContaining(['budgets.read', 'requests.approve']));
    expect(store().draft()['admin'].has('admin.policies')).toBe(false);
    expect(store().changedRoles()).toEqual(['requester', 'admin']);
    expect(store().actionError()).toBeNull();

    // Save again goes out on the latest version.
    api.setRolePermissions.mockImplementation(applying(latest));
    expect(await store().save()).toBe(true);
    expect(api.setRolePermissions.mock.calls.at(-2)![0]['If-Match']).toBe('"9"');
  });

  it('lockout_422_shows_message', async () => {
    await store().load();
    store().toggle('admin', 'admin.authorization');
    api.setRolePermissions.mockRejectedValue(problem(422, 'The admin role must keep admin.authorization, or nobody could edit this matrix.'));

    expect(await store().save()).toBe(false);

    expect(store().actionError()).toContain('must keep admin.authorization');
    expect(conflict.show).not.toHaveBeenCalled();
    // The edit stays so it can be corrected; nothing was saved.
    expect(store().dirty()).toBe(true);
    expect(store().saving()).toBe(false);
  });

  it('partial_failure_names_saved_roles', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    store().toggle('manager', 'orders.receive');
    store().toggle('admin', 'admin.authorization');
    const ok = applying();
    api.setRolePermissions.mockImplementation((p: Put) =>
      p.role === 'admin' ? Promise.reject(problem(422, 'The admin role must keep admin.authorization.')) : ok(p),
    );
    api.getMatrix.mockResolvedValue(matrixDto({ version: 6 }));

    expect(await store().save()).toBe(false);

    expect(store().savedRoles()).toEqual(['requester', 'manager']);
    expect(store().actionError()).toContain('Requester');
    expect(store().actionError()).toContain('Manager');
    expect(store().actionError()).toContain('must keep admin.authorization');
    expect(store().matrix()?.version).toBe(6);
    // Only the edit that was refused stays on screen.
    expect(store().draft()['admin'].has('admin.authorization')).toBe(false);
    expect(store().dirty()).toBe(true);
  });

  it('a_412_after_some_roles_were_saved_names_them_in_the_dialog', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    store().toggle('manager', 'orders.receive');
    const ok = applying();
    api.setRolePermissions.mockImplementation((p: Put) => (p.role === 'manager' ? Promise.reject(problem(412, 'old')) : ok(p)));

    await store().save();

    expect(conflict.show.mock.calls[0][0]).toContain('Requester');
    expect(conflict.show.mock.calls[0][0]).not.toContain('Manager');
  });

  it('push_while_clean_reloads', async () => {
    await store().load();
    api.getMatrix.mockResolvedValue(
      matrixDto({ version: 5, roles: matrixDto().roles.map((r) => (r.role === 'requester' ? { ...r, permissions: ['requests.read'] } : r)) }),
    );
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));

    fake.hubs[0].emit('EntityChanged', pushed);

    await vi.waitFor(() => expect(store().matrix()?.version).toBe(5));
    expect(store().draft()['requester'].size).toBe(1);
    expect(store().changedElsewhere()).toBe(false);
    live.stop();
  });

  it('push_while_dirty_offers_reload_and_keeps_edits', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    api.getMatrix.mockResolvedValue(matrixDto({ version: 5 }));
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));

    fake.hubs[0].emit('EntityChanged', pushed);

    await vi.waitFor(() => expect(store().changedElsewhere()).toBe(true));
    expect(store().draft()['requester'].has('requests.approve')).toBe(true);
    await store().reload();
    expect(store().changedElsewhere()).toBe(false);
    expect(store().dirty()).toBe(false);
    expect(store().matrix()?.version).toBe(5);
    live.stop();
  });

  it('a push for the same version while dirty shows no banner (our own save echoing back)', async () => {
    await store().load();
    store().toggle('requester', 'requests.approve');
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    api.getMatrix.mockClear();

    fake.hubs[0].emit('EntityChanged', pushed);

    await vi.waitFor(() => expect(api.getMatrix).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r));
    expect(store().changedElsewhere()).toBe(false);
    live.stop();
  });
});
