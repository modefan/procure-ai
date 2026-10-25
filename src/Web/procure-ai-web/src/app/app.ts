import { Component } from '@angular/core';
import { SessionExpiredDialog } from './core/auth/session-expired';
import { Shell } from './features/shell/shell';

@Component({
  selector: 'app-root',
  imports: [Shell, SessionExpiredDialog],
  template: `
    <app-shell />
    <app-session-expired-dialog />
  `,
})
export class App {}
