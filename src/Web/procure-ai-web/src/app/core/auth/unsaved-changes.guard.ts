import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { ConfirmDialog } from '../ui/confirm-dialog';

/** Implemented by form pages that can hold edits the user has not saved. */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

/** Asks before leaving a page with unsaved edits (the themed confirm dialog); says nothing when there are none. */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = async (component) =>
  !component.hasUnsavedChanges() ||
  (await inject(ConfirmDialog).ask({
    title: 'Leave without saving?',
    body: 'You have unsaved changes. If you leave, they are lost.',
    confirm: 'Leave',
    cancel: 'Stay',
  }));
