import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { SystemAvailability } from '../system/system-availability';
import { ConfirmDialog } from '../ui/confirm-dialog';
import { HasUnsavedChanges, unsavedChangesGuard } from './unsaved-changes.guard';

describe('unsavedChangesGuard', () => {
  let ask: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    ask = vi.fn().mockResolvedValue(false);
    TestBed.configureTestingModule({ providers: [{ provide: ConfirmDialog, useValue: { ask } }] });
  });

  function run(component: HasUnsavedChanges) {
    return TestBed.runInInjectionContext(() =>
      unsavedChangesGuard(component, {} as ActivatedRouteSnapshot, {} as RouterStateSnapshot, {} as RouterStateSnapshot),
    );
  }

  it('unsaved_guard_asks_only_when_dirty: does not ask when nothing changed', async () => {
    await expect(run({ hasUnsavedChanges: () => false })).resolves.toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });

  it('asks "Leave without saving?" with Stay and Leave, and blocks when the user stays', async () => {
    await expect(run({ hasUnsavedChanges: () => true })).resolves.toBe(false);

    expect(ask).toHaveBeenCalledOnce();
    expect(ask.mock.calls[0][0]).toMatchObject({ title: 'Leave without saving?', confirm: 'Leave', cancel: 'Stay' });
  });

  it('while the demo is switched off: lets the app leave a dirty form without asking (to the maintenance page)', async () => {
    TestBed.inject(SystemAvailability).markDisabled(null);

    await expect(run({ hasUnsavedChanges: () => true })).resolves.toBe(true);
    expect(ask).not.toHaveBeenCalled();
  });

  it('lets the user leave when they confirm', async () => {
    ask.mockResolvedValue(true);

    await expect(run({ hasUnsavedChanges: () => true })).resolves.toBe(true);
  });
});
