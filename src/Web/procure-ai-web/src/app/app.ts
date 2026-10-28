import { Component } from '@angular/core';
import { SessionExpiredDialog } from './core/auth/session-expired';
import { ConfirmDialogHost } from './core/ui/confirm-dialog';
import { ConflictDialogHost } from './core/ui/conflict-dialog';
import { Shell } from './features/shell/shell';

@Component({
  selector: 'app-root',
  imports: [Shell, SessionExpiredDialog, ConflictDialogHost, ConfirmDialogHost],
  template: `
    <app-shell />
    <app-session-expired-dialog />
    <app-conflict-dialog />
    <app-confirm-dialog />
  `,
})
export class App {}
