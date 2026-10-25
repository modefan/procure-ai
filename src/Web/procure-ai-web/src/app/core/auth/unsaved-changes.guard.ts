import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { UnsavedChangesPrompt } from './unsaved-changes-prompt';

/** Implemented by form pages that can hold edits the user has not saved. */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

/** Asks before leaving a page with unsaved edits; says nothing when there are none. */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (component) =>
  !component.hasUnsavedChanges() || inject(UnsavedChangesPrompt).confirm();
