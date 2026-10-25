import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { HasUnsavedChanges, unsavedChangesGuard } from './unsaved-changes.guard';
import { UnsavedChangesPrompt } from './unsaved-changes-prompt';

describe('unsavedChangesGuard', () => {
  let confirm: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    confirm = vi.fn().mockReturnValue(false);
    TestBed.configureTestingModule({ providers: [{ provide: UnsavedChangesPrompt, useValue: { confirm } }] });
  });

  function run(component: HasUnsavedChanges) {
    return TestBed.runInInjectionContext(() =>
      unsavedChangesGuard(component, {} as ActivatedRouteSnapshot, {} as RouterStateSnapshot, {} as RouterStateSnapshot),
    );
  }

  it('does not ask when nothing changed', () => {
    expect(run({ hasUnsavedChanges: () => false })).toBe(true);
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks when there are unsaved changes, and blocks when the user says no', () => {
    expect(run({ hasUnsavedChanges: () => true })).toBe(false);
    expect(confirm).toHaveBeenCalledOnce();
  });

  it('lets the user leave when they confirm', () => {
    confirm.mockReturnValue(true);

    expect(run({ hasUnsavedChanges: () => true })).toBe(true);
  });
});
