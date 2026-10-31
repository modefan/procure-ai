import { Component, computed, effect, ElementRef, inject, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Menu, MenuContent, MenuItem, MenuTrigger } from '@angular/aria/menu';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { ApprovalsStore } from '../approvals/approvals.store';
import { AuthStore } from '../../core/auth/auth.store';
import { LiveConnection } from '../../core/live/live-connection';
import { NotificationsStore } from '../../core/live/notifications.store';
import { CacheRegistry } from '../../core/state/cache-registry';
import { DevPanel } from '../../core/dev-panel/dev-panel';
import { DevPanelStore } from '../../core/dev-panel/dev-panel.store';
import { BellMenu } from './bell-menu';
import { LiveIndicator } from './live-indicator';
import { OnlineUsers } from './online-users';

interface NavLink {
  path: string;
  label: string;
  permission: string;
  count?: number;
}

/** The admin pages, in menu order; each shows only for a user who has its permission. Later admin screens are added here. */
const ADMIN_LINKS: NavLink[] = [
  { path: 'admin/authorization', label: 'Authorization', permission: 'admin.authorization' },
  { path: 'admin/access-check', label: 'Access check', permission: 'admin.authorization' },
  { path: 'admin/users', label: 'Users', permission: 'admin.users' },
  { path: 'admin/policies', label: 'Policies', permission: 'admin.policies' },
  { path: 'admin/system', label: 'System status', permission: 'admin.system' },
  { path: 'admin/conversations', label: 'Conversations', permission: 'admin.system' },
  { path: 'admin/data-changes', label: 'Data changes', permission: 'admin.system' },
];

/**
 * The frame around every page: skip link, header with the menu for what this user may open, and the page
 * outlet. Signed out, the header shows only Welcome, Log in and Register; the app links appear after sign-in.
 */
@Component({
  selector: 'app-shell',
  host: { '(window:resize)': 'onResize()', '(document:keydown.escape)': 'adminOpen.set(false)', '(document:click)': 'onDocumentClick($event)', '(document:keydown)': 'onKeydown($event)' },
  imports: [RouterLink, RouterLinkActive, RouterOutlet, Menu, MenuContent, MenuItem, MenuTrigger, BellMenu, DevPanel, LiveIndicator, OnlineUsers],
  template: `
    <a
      href="#main"
      class="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-white focus:px-3 focus:py-2 focus:text-sm focus:font-semibold focus:shadow"
    >
      Skip to content
    </a>

    <header class="border-b border-slate-200 bg-white">
      <div class="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 py-3">
        <div class="flex items-center gap-2">
          @if (auth.isLoggedIn()) {
            <button
              type="button"
              data-testid="phone-menu"
              class="btn-ghost -ml-2 !px-2 !py-1 md:hidden"
              aria-controls="phone-drawer"
              [attr.aria-expanded]="menuOpen()"
              aria-label="Menu"
              (click)="menuOpen.set(true)"
            >
              <svg viewBox="0 0 24 24" class="size-6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16" /></svg>
            </button>
          }
          <a routerLink="/" class="text-lg font-bold tracking-tight text-slate-900" aria-label="ProcureAI home">
            <span aria-hidden="true" class="text-indigo-700">&#9670;</span><span class="max-[440px]:sr-only"> Procure<span class="text-indigo-700">AI</span></span>
          </a>
        </div>

        <nav #nav aria-label="Main" [class.nav-fade-end]="fadeEnd()" (scroll)="updateFade()" class="order-3 -mx-4 flex w-[calc(100%+2rem)] items-center gap-1 overflow-x-auto px-4 py-1 lg:order-none lg:mx-0 lg:w-auto lg:flex-wrap lg:overflow-visible lg:px-0" [class]="auth.isLoggedIn() ? 'max-md:hidden' : ''">
          @if (!auth.isLoggedIn()) {
            <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }" class="nav-link">Welcome</a>
          }
          @if (auth.isLoggedIn()) {
            <a routerLink="/app/dashboard" routerLinkActive="active" (isActiveChange)="$event && revealActive()" class="nav-link">Dashboard</a>
            @for (link of links(); track link.path) {
              <a
                [routerLink]="'/app/' + link.path"
                routerLinkActive="active"
                (isActiveChange)="$event && revealActive()"
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

        <div class="ml-auto flex items-center gap-2">
          @if (auth.isLoggedIn()) {
            @if (adminLinks().length > 0) {
              <div class="relative max-md:hidden" data-admin-menu>
                <button
                  type="button"
                  data-testid="admin-menu"
                  class="btn-ghost !px-3 !py-1"
                  [class.active]="adminActive()"
                  aria-controls="admin-links"
                  [attr.aria-expanded]="adminOpen()"
                  (click)="adminOpen.set(!adminOpen())"
                >
                  Admin <span aria-hidden="true" class="text-xs">&#9662;</span>
                </button>
                @if (adminOpen()) {
                  <div id="admin-links" data-testid="admin-links" class="absolute right-0 z-40 mt-1 flex min-w-48 flex-col rounded-lg border border-slate-200 bg-white py-1 shadow-lg">
                    @for (link of adminLinks(); track link.path) {
                      <a [routerLink]="'/app/' + link.path" routerLinkActive="active" class="px-4 py-2 text-sm whitespace-nowrap text-slate-700 hover:bg-indigo-50 focus-visible:outline-offset-[-2px] [&.active]:font-semibold [&.active]:text-indigo-800">{{ link.label }}</a>
                    }
                  </div>
                }
              </div>
            }
            <app-online-users />
            <app-live-indicator />
            <app-bell-menu />
            <div class="relative">
              <button ngMenuTrigger #trigger="ngMenuTrigger" [menu]="userMenu" type="button" data-testid="user-menu" class="btn-ghost max-w-40 !px-2 !py-1 sm:max-w-52 sm:!px-4">
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
                  @if (switchTenants().length > 0) {
                    <div role="group" aria-labelledby="switch-company-heading" class="border-b border-slate-100 pb-1">
                      <div id="switch-company-heading" data-testid="switch-tenant-heading" class="px-4 pt-2 pb-1 text-xs font-semibold tracking-wide whitespace-nowrap text-slate-600 uppercase">Switch company</div>
                      @for (tenant of switchTenants(); track tenant.id) {
                        <div ngMenuItem [value]="'tenant:' + tenant.id" data-testid="switch-tenant" class="cursor-pointer truncate px-4 py-2 text-sm text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">{{ tenant.name }}</div>
                      }
                    </div>
                  }
                  <div ngMenuItem value="logout" class="cursor-pointer px-4 py-2 text-sm text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">Log out</div>
                  <div ngMenuItem value="logout-all" class="cursor-pointer px-4 py-2 text-sm whitespace-nowrap text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">Log out everywhere</div>
                  @if (devPanel.enabled()) {
                    <div ngMenuItem value="dev-panel" class="cursor-pointer border-t border-slate-100 px-4 py-2 text-sm whitespace-nowrap text-slate-700 hover:bg-indigo-50 focus:bg-indigo-50 focus:text-indigo-900 focus-visible:outline-offset-[-2px]">
                      Behind the scenes <span class="ml-2 text-xs text-slate-500">Alt+Shift+D</span>
                    </div>
                  }
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

    <dialog
      #drawer
      id="phone-drawer"
      data-testid="phone-drawer"
      aria-label="Menu"
      class="m-0 mr-auto h-dvh max-h-dvh w-72 max-w-[85vw] bg-white p-0 shadow-xl backdrop:bg-slate-900/50 md:hidden"
      (close)="menuOpen.set(false)"
      (click)="onDrawerClick($event)"
    >
      @if (menuOpen()) {
        <div class="flex items-center justify-between border-b border-slate-200 px-4 py-3">
          <span class="text-lg font-bold tracking-tight text-slate-900"><span aria-hidden="true" class="text-indigo-700">&#9670;</span> Procure<span class="text-indigo-700">AI</span></span>
          <button type="button" data-testid="phone-menu-close" class="btn-ghost !px-2 !py-1" aria-label="Close menu" (click)="menuOpen.set(false)">
            <svg viewBox="0 0 24 24" class="size-6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
          </button>
        </div>
        <nav aria-label="Menu" class="flex flex-col gap-1 p-3">
          <a routerLink="/app/dashboard" routerLinkActive="active" class="nav-link !py-3">Dashboard</a>
          @for (link of links(); track link.path) {
            <a [routerLink]="'/app/' + link.path" routerLinkActive="active" class="nav-link flex items-center justify-between !py-3">
              <span>{{ link.label }}</span>
              @if (link.count) {
                <span class="ml-1 inline-block min-w-5 rounded-full bg-indigo-600 px-1.5 text-center text-xs leading-5 font-semibold text-white">{{ link.count }}</span>
              }
            </a>
          }
          @if (adminLinks().length > 0) {
            <div data-testid="drawer-admin" class="mt-3 border-t border-slate-200 pt-3">
              <h2 class="px-3 pb-1 text-xs font-semibold tracking-wide text-slate-600 uppercase">Admin</h2>
              @for (link of adminLinks(); track link.path) {
                <a [routerLink]="'/app/' + link.path" routerLinkActive="active" class="nav-link block !py-3">{{ link.label }}</a>
              }
            </div>
          }
        </nav>
      }
    </dialog>

    @if (auth.isLoggedIn() && caches.revalidateErrors()) {
      <div data-testid="refresh-banner" role="status" class="flex flex-wrap items-center justify-center gap-x-4 gap-y-2 border-b border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900">
        <span><strong class="font-semibold">Couldn't refresh.</strong> Showing what we had.</span>
        <button type="button" class="btn-secondary !px-3 !py-1" (click)="caches.retryRevalidation()">Retry</button>
      </div>
    }

    @if (auth.isLoggedIn() && (live.state() === 'reconnecting' || live.state() === 'offline')) {
      <div data-testid="live-banner" role="status" class="border-b border-amber-200 bg-amber-50 px-4 py-2 text-center text-sm text-amber-900">
        @if (live.state() === 'reconnecting') {
          <strong class="font-semibold">Reconnecting…</strong> Live updates paused. Your work is safe.
        } @else {
          <strong class="font-semibold">Offline.</strong> Live updates paused. Trying again every 30 seconds. Your work is safe.
        }
      </div>
    }

    <main id="main" tabindex="-1" class="outline-none" [class]="mainPadding()">
      <router-outlet />
    </main>

    @if (devPanelShown()) {
      <!-- Loaded on first use: most visits never open it, and it would push the initial bundle over its budget. -->
      @defer {
        <app-dev-panel />
      }
    }
  `,
})
export class Shell {
  private readonly nav = viewChild<ElementRef<HTMLElement>>('nav');
  private readonly drawer = viewChild.required<ElementRef<HTMLDialogElement>>('drawer');
  /** The phone menu (a native modal dialog, like the confirm dialog: the browser gives the focus trap and Esc). */
  protected readonly menuOpen = signal(false);
  protected readonly caches = inject(CacheRegistry);
  private readonly router = inject(Router);
  /** True while more of the phone nav lies off to the right: the nav then fades out at its right edge. */
  protected readonly fadeEnd = signal(false);
  protected readonly auth = inject(AuthStore);
  protected readonly devPanel = inject(DevPanelStore);
  /** Room at the bottom of the page so the open sheet does not cover its end. */
  protected readonly mainPadding = computed(() => (!this.devPanelShown() ? '' : this.devPanel.collapsed() ? 'pb-14' : 'pb-[42vh]'));
  /** The "Behind the scenes" sheet is on screen: enabled in the config, opened by the user, and somebody is signed in. */
  protected readonly devPanelShown = computed(() => this.auth.isLoggedIn() && this.devPanel.active());
  protected readonly live = inject(LiveConnection);
  private readonly notifications = inject(NotificationsStore);
  private readonly approvals = inject(ApprovalsStore);

  /** The links this user may open, in header order. Approvals carries the number of steps waiting. */
  protected readonly links = computed<NavLink[]>(() => {
    const all: NavLink[] = [
      { path: 'requests', label: 'Requests', permission: 'requests.read' },
      { path: 'approvals', label: 'Approvals', permission: 'requests.approve', count: this.approvals.total() },
      { path: 'orders', label: 'Orders', permission: 'orders.read' },
      { path: 'invoices', label: 'Invoices', permission: 'invoices.read' },
      { path: 'budgets', label: 'Budgets', permission: 'budgets.read' },
      { path: 'assistant', label: 'Assistant', permission: 'assistant.use' },
    ];
    return all.filter((link) => this.auth.hasPermission(link.permission));
  });

  /** The admin pages this user may open (the "Admin" menu; the group of the phone drawer). */
  protected readonly adminLinks = computed(() => ADMIN_LINKS.filter((link) => this.auth.hasPermission(link.permission)));
  protected readonly adminOpen = signal(false);
  /** True while an admin page is open (the Admin button then looks active). */
  protected readonly adminActive = signal(false);

  /** The other companies this user belongs to (the "Switch company" group of the user menu); none unless there is more than one. */
  protected readonly switchTenants = computed(() => {
    const tenants = this.auth.tenants();
    const current = this.auth.user()?.tenantId;
    return tenants.length > 1 ? tenants.filter((tenant) => tenant.id !== current) : [];
  });

  protected readonly roleAndTenant = computed(() => {
    const user = this.auth.user();
    const role = user?.roles[0] ? capitalise(user.roles[0]) : '';
    const tenant = user?.tenantName?.trim() || (user?.tenantId ? capitalise(user.tenantId) : '');
    return [role, tenant].filter(Boolean).join(' · ');
  });

  constructor() {
    // On phones the nav scrolls sideways: the active link is brought into view when it becomes active (isActiveChange on each link) and when the links appear.
    toObservable(this.links).pipe(takeUntilDestroyed()).subscribe(() => this.revealActive());

    effect(() => {
      const element = this.drawer().nativeElement;
      const open = this.menuOpen();
      if (open && !element.open) {
        element.showModal();
      } else if (!open && element.open) {
        element.close();
      }
    });
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed()).subscribe((e) => {
      this.menuOpen.set(false);
      this.adminOpen.set(false);
      this.adminActive.set(e.urlAfterRedirects.startsWith('/app/admin/'));
    });

    // Fill the bell and the approvals count once somebody is signed in (the connection itself is started by the app initializer).
    effect(() => {
      if (this.auth.isLoggedIn()) {
        // The loaders read store signals synchronously; untracked, so a push or markRead does not re-run this effect.
        untracked(() => {
          void this.notifications.load();
          if (this.auth.hasPermission('requests.approve')) {
            void this.approvals.refresh();
          }
        });
      }
    });
  }

  /** Scrolls the active nav link into view (after the router has marked it) and refreshes the fade. */
  protected revealActive(): void {
    setTimeout(() => {
      this.nav()?.nativeElement.querySelector('a.active')?.scrollIntoView?.({ inline: 'center', block: 'nearest' });
      this.updateFade();
    });
  }

  /** A click on the dark area around the drawer (the dialog element itself, not its content) or on a link closes it. */
  protected onDrawerClick(event: Event): void {
    const target = event.target as HTMLElement;
    if (target === this.drawer().nativeElement || target.closest('nav a')) {
      this.menuOpen.set(false);
    }
  }

  /** A click anywhere outside the Admin menu closes its list. */
  protected onDocumentClick(event: Event): void {
    if (this.adminOpen() && !(event.target as HTMLElement | null)?.closest?.('[data-admin-menu]')) {
      this.adminOpen.set(false);
    }
  }

  protected onResize(): void {
    this.updateFade();
    if (window.innerWidth >= 768) {
      this.menuOpen.set(false); // the drawer is for phones; with room for the nav it goes away
    }
  }

  protected updateFade(): void {
    const el = this.nav()?.nativeElement;
    this.fadeEnd.set(!!el && el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
  }

  protected onMenuItem(value: string | undefined): void {
    if (value === 'logout') {
      void this.auth.logout();
    } else if (value === 'logout-all') {
      void this.auth.logoutAll();
    } else if (value?.startsWith('tenant:')) {
      void this.auth.switchTenant(value.slice('tenant:'.length));
    } else if (value === 'dev-panel') {
      this.devPanel.toggle();
    }
  }

  /** Alt+Shift+D shows or hides "Behind the scenes" (when the config enables it). */
  protected onKeydown(event: KeyboardEvent): void {
    if (event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && event.code === 'KeyD' && this.auth.isLoggedIn() && this.devPanel.enabled()) {
      event.preventDefault();
      this.devPanel.toggle();
    }
  }
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}
