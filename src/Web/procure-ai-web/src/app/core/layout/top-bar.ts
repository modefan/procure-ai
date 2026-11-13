import { Component, inject, input, output } from '@angular/core';
import { Brand } from './menu-nav';
import { SessionMenu } from './session-menu';
import { SessionState } from './session-state';

/**
 * The slim bar above the page. Below 1024 px it holds the menu button and the name; signed in (with the demo on) it holds the session
 * menu (who is online, the connection, the bell, the user menu). Nothing in it calls the server unless that session is there, so the
 * maintenance page stays call-free. On a desktop with no session there is nothing to show, and it goes away.
 */
@Component({
  selector: 'app-top-bar',
  imports: [Brand, SessionMenu],
  template: `
    <header class="flex h-14 items-center gap-2 border-b border-slate-200 bg-white/95 px-3 backdrop-blur sm:px-4" [class.lg:hidden]="!session()">
      <button
        type="button"
        data-testid="phone-menu"
        class="btn-ghost !px-2 !py-1 lg:hidden"
        aria-controls="menu-drawer"
        [attr.aria-expanded]="menuOpen()"
        aria-label="Menu"
        (click)="openMenu.emit()"
      >
        <svg viewBox="0 0 24 24" class="size-6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h10" /></svg>
      </button>
      <app-brand class="min-w-0 lg:hidden" [compact]="session()" />

      @if (session()) {
        <!-- Its own chunk (with Angular Aria's menu): loaded once a session is there. -->
        @defer {
          <app-session-menu />
        }
      }
    </header>
  `,
})
export class TopBar {
  /** The phone drawer is open (for the menu button's aria-expanded). */
  readonly menuOpen = input(false);
  /** The menu button was pressed. */
  readonly openMenu = output<void>();

  /** Somebody is signed in and the demo is on: only then the session widgets (and their calls) exist. */
  protected readonly session = inject(SessionState).active;
}
