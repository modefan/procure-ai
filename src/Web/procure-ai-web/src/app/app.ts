import { Component, inject } from '@angular/core';
import { SessionExpiredDialog } from './core/auth/session-expired';
import { Frame } from './core/layout/frame';
import { ConfirmDialogHost } from './core/ui/confirm-dialog';
import { ConflictDialogHost } from './core/ui/conflict-dialog';
import { SystemAvailability } from './core/system/system-availability';

@Component({
  selector: 'app-root',
  imports: [Frame, SessionExpiredDialog, ConflictDialogHost, ConfirmDialogHost],
  template: `
    <!-- One frame around every page. While the demo is switched off it shows only the public menu and makes no session or live calls. -->
    <app-frame />
    @if (!availability.disabled()) {
      <app-session-expired-dialog />
      <app-conflict-dialog />
    }
    <!-- Outside the switch: a question being asked (e.g. "Leave without saving?") must never lose its dialog half-way. -->
    <app-confirm-dialog />
  `,
})
export class App {
  protected readonly availability = inject(SystemAvailability);
}
