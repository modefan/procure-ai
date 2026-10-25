import { Service } from '@angular/core';

/** The "leave this page?" question. The browser's own confirm box for now; a themed dialog can replace it here. */
@Service()
export class UnsavedChangesPrompt {
  confirm(): boolean {
    return window.confirm('You have unsaved changes. Leave this page and lose them?');
  }
}
