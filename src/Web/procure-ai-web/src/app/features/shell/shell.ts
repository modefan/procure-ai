import { Component, computed, effect, inject } from '@angular/core';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@angular/aria/menu';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { ApprovalsCountStore } from '../../core/live/approvals-count.store';
import { LiveConnection } from '../../core/live/live-connection';
import { NotificationsStore } from '../../core/live/notifications.store';
import { BellMenu } from './bell-menu';
import { LiveIndicator } from './live-indicator';
import { OnlineUsers } from './online-users';

interface NavLink {
  path: string;
  label: string;
  permission: string;
  count?: number;
}

/**
 * The frame around every page: skip link, header with the menu for what this user may open, and the page
 * outlet. Signed out, the header shows only Welcome, Log in and Register; the app links appear after sign-in.
 */
@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, Menu, MenuContent, MenuItem, MenuTrigger, BellMenu, LiveIndicator, OnlineUsers],
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
            @for (link of links(); track link.path) {
              <a
                [routerLink]="'/app/' + link.path"
                routerLinkActive="active"
                class="nav-link"
                [attr.aria-label]="link.count ? link.label + ', ' + link.count + ' waiting' : null"
              >
                {{ link.label }}
                @if (link.count) {
                  <span class="ml-1 inline-block min-w-5 rounded-full bg-indigo-600 px-1.5 text-center text-xs leading-5 font-semibold text-white">{{ link.count }}</span>
                }
              </a>
            }
          }
        </nav>

        <div class="flex items-center gap-2">
          @if (auth.isLoggedIn()) {
            <app-online-users />
            <app-live-indicator />
            <app-bell-menu />
            <div class="relative">
              <button ngMenuTrigger #trigger="ngMenuTrigger" [menu]="userMenu" type="button" data-testid="user-menu" class="btn-ghost max-w-40 !px-2 !py-1 sm:max-w-64 sm:!px-4">
                <span class="min-w-0 text-left leading-tight">
                  <span class="block truncate">{{ auth.user()?.name ?? 'Account' }}</span>
                  <span class="block truncate text-xs font-normal text-slate-500">{{ roleAndTenant() }}</span>
                </span>
                <span aria-hidden="true" class="text-xs">&#9662;</span>
              </button>
              <div
                ngMenu
                #userMenu="ngMenu"
                (itemSelected)="onMenuItem($event)"
                [class.hidden]="!trigger.expanded()"
                class="absolute right-0 z-40 mt-1 min-w-48 rounded-lg border border-slate-200 bg-white py-1 shadow-lg"
                aria-label="Account"
              >
                <ng-template ngMenuContent>
                  <div ngMenuItem value="logout" class="cursor-pointer px-4 py-2 text-sm text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">Log out</div>
                  <div ngMenuItem value="logout-all" class="cursor-pointer px-4 py-2 text-sm whitespace-nowrap text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">Log out everywhere</div>
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

    @if (auth.isLoggedIn() && (live.state() === 'reconnecting' || live.state() === 'offline')) {
      <div data-testid="live-banner" role="status" class="border-b border-amber-200 bg-amber-50 px-4 py-2 text-center text-sm text-amber-900">
        @if (live.state() === 'reconnecting') {
          <strong class="font-semibold">Reconnecting…</strong> Live updates paused. Your work is safe.
        } @else {
          <strong class="font-semibold">Offline.</strong> Live updates paused. Trying again every 30 seconds. Your work is safe.
        }
      </div>
    }

    <main id="main" tabindex="-1" class="outline-none">
      <router-outlet />
    </main>
  `,
})
export class Shell {
  protected readonly auth = inject(AuthStore);
  protected readonly live = inject(LiveConnection);
  private readonly notifications = inject(NotificationsStore);
  private readonly approvals = inject(ApprovalsCountStore);

  /** The links this user may open, in header order. Approvals carries the number of steps waiting. */
  protected readonly links = computed<NavLink[]>(() => {
    const all: NavLink[] = [
      { path: 'requests', label: 'Requests', permission: 'requests.read' },
      { path: 'approvals', label: 'Approvals', permission: 'requests.approve', count: this.approvals.count() },
      { path: 'orders', label: 'Orders', permission: 'orders.read' },
      { path: 'invoices', label: 'Invoices', permission: 'invoices.read' },
      { path: 'budgets', label: 'Budgets', permission: 'budgets.read' },
    ];
    return all.filter((link) => this.auth.hasPermission(link.permission));
  });

  protected readonly roleAndTenant = computed(() => {
    const user = this.auth.user();
    const role = user?.roles[0] ? capitalise(user.roles[0]) : '';
    const tenant = user?.tenantId ? capitalise(user.tenantId) : '';
    return [role, tenant].filter(Boolean).join(' · ');
  });

  constructor() {
    // Fill the bell and the approvals count once somebody is signed in (the connection itself is started by the app initializer).
    effect(() => {
      if (this.auth.isLoggedIn()) {
        void this.notifications.load();
        if (this.auth.hasPermission('requests.approve')) {
          void this.approvals.load();
        }
      }
    });
  }

  protected onMenuItem(value: string | undefined): void {
    if (value === 'logout') {
      void this.auth.logout();
    } else if (value === 'logout-all') {
      void this.auth.logoutAll();
    }
  }
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
