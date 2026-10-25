import { Component, inject } from '@angular/core';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@angular/aria/menu';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

/**
 * The frame around every page: skip link, header with the menu for what this user may open, and the page
 * outlet. Signed out, the header shows only Welcome, Log in and Register; the app links appear after sign-in.
 */
@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, Menu, MenuContent, MenuItem, MenuTrigger],
  template: `
    <a
      href="#main"
      class="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-white focus:px-3 focus:py-2 focus:text-sm focus:font-semibold focus:shadow"
    >
      Skip to content
    </a>

    <header class="border-b border-slate-200 bg-white">
      <div class="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 py-3">
        <a routerLink="/" class="text-lg font-bold tracking-tight text-slate-900" aria-label="ProcureAI home">
          <span aria-hidden="true" class="text-indigo-700">&#9670;</span> Procure<span class="text-indigo-700">AI</span>
        </a>

        <nav aria-label="Main" class="order-3 flex w-full flex-wrap items-center gap-1 sm:order-none sm:w-auto">
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }" class="nav-link">Welcome</a>
          @if (auth.isLoggedIn()) {
            <a routerLink="/app/dashboard" routerLinkActive="active" class="nav-link">Dashboard</a>
            @if (auth.hasPermission('requests.read')) {
              <a routerLink="/app/requests" routerLinkActive="active" class="nav-link">Requests</a>
            }
          }
        </nav>

        <div class="flex items-center gap-2">
          @if (auth.isLoggedIn()) {
            <div class="relative">
              <button ngMenuTrigger #trigger="ngMenuTrigger" [menu]="userMenu" type="button" class="btn-ghost max-w-56">
                <span class="truncate">{{ auth.user()?.name ?? 'Account' }}</span>
                <span aria-hidden="true" class="text-xs">&#9662;</span>
              </button>
              <div
                ngMenu
                #userMenu="ngMenu"
                (itemSelected)="onMenuItem($event)"
                [class.hidden]="!trigger.expanded()"
                class="absolute right-0 z-40 mt-1 min-w-40 rounded-lg border border-slate-200 bg-white py-1 shadow-lg"
                aria-label="Account"
              >
                <ng-template ngMenuContent>
                  <div ngMenuItem value="logout" class="cursor-pointer px-4 py-2 text-sm text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">Log out</div>
                </ng-template>
              </div>
            </div>
          } @else {
            <a routerLink="/login" class="btn-primary">Log in</a>
            <a routerLink="/register" class="btn-secondary">Register</a>
          }
        </div>
      </div>
    </header>

    <main id="main" tabindex="-1" class="outline-none">
      <router-outlet />
    </main>
  `,
})
export class Shell {
  protected readonly auth = inject(AuthStore);

  protected onMenuItem(value: string | undefined): void {
    if (value === 'logout') {
      void this.auth.logout();
    }
  }
}
