import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SessionExpiredDialog } from './core/auth/session-expired';
import { ConfirmDialogHost } from './core/ui/confirm-dialog';
import { ConflictDialogHost } from './core/ui/conflict-dialog';
import { Shell } from './features/shell/shell';
import { SystemAvailability } from './core/system/system-availability';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Shell, SessionExpiredDialog, ConflictDialogHost, ConfirmDialogHost],
  template: `
    @if (availability.disabled()) {
      <!-- The demo is switched off: only the maintenance page, without the shell (no menu, session or live calls). -->
      <router-outlet />
    } @else {
      <app-shell />
      <app-session-expired-dialog />
      <app-conflict-dialog />
      <app-confirm-dialog />
    }
  `,
})
export class App {
  protected readonly availability = inject(SystemAvailability);
}
