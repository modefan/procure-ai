import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { SystemAvailability } from '../system/system-availability';
import { ConfirmDialog } from '../ui/confirm-dialog';

/** Implemented by form pages that can hold edits the user has not saved. */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

/**
 * Asks before leaving a page with unsaved edits (the themed confirm dialog); says nothing when there are none. While the demo is switched
 * off it lets the app leave without asking: nothing can be saved then, and the way out is the maintenance page.
 */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = async (component) =>
  inject(SystemAvailability).disabled() ||
  !component.hasUnsavedChanges() ||
  (await inject(ConfirmDialog).ask({
    title: 'Leave without saving?',
    body: 'You have unsaved changes. If you leave, they are lost.',
    confirm: 'Leave',
    cancel: 'Stay',
  }));
