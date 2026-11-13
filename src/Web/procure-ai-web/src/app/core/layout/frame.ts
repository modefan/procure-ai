import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { ApprovalsStore } from '../../features/approvals/approvals.store';
import { AuthStore } from '../auth/auth.store';
import { AppConfigService } from '../config/app-config';
import { DevPanel } from '../dev-panel/dev-panel';
import { DevPanelStore } from '../dev-panel/dev-panel.store';
import { LiveConnection } from '../live/live-connection';
import { NotificationsStore } from '../live/notifications.store';
import { CacheRegistry } from '../state/cache-registry';
import { MenuDrawer } from './menu-drawer';
import { Brand, MenuNav } from './menu-nav';
import { SessionState } from './session-state';
import { TopBar } from './top-bar';

/** How long the toast of a refused company switch stays up. */
const SWITCH_ERROR_MS = 8000;

/** From this width the menu is a sidebar; below it, a drawer (Tailwind's `lg`). */
const SIDEBAR_MIN_WIDTH = 1024;

/**
 * The frame around every page, public, signed-in, error and maintenance pages alike: skip link, the left menu (a 16rem sidebar from
 * 1024 px, a drawer below), the slim top bar, the session banners and the page outlet. Everything that talks to the server for a session
 * (bell, approvals count, banners, dev panel) exists only while somebody is signed in and the demo is on.
 */
@Component({
  selector: 'app-frame',
  host: { '(window:resize)': 'onResize()', '(document:keydown)': 'onKeydown($event)' },
  imports: [RouterOutlet, Brand, DevPanel, MenuDrawer, MenuNav, TopBar],
  template: `
    <a
      href="#main"
      data-testid="skip-link"
      (click)="skipToMain($event)"
      class="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-white focus:px-3 focus:py-2 focus:text-sm focus:font-semibold focus:shadow"
    >
      Skip to content
    </a>

    <div class="min-h-dvh lg:grid lg:grid-cols-[16rem_minmax(0,1fr)]">
      <aside class="hidden border-r border-slate-200 bg-white lg:block">
        <div class="sticky top-0 flex h-dvh flex-col overflow-y-auto">
          <div class="flex h-14 shrink-0 items-center px-4">
            <app-brand />
          </div>
          <div class="px-3 pt-3 pb-6">
            <app-menu-nav label="Main" idPrefix="side" />
          </div>
        </div>
      </aside>

      <div class="flex min-w-0 flex-col">
        <app-top-bar class="sticky top-0 z-30 block" [menuOpen]="drawerOpen()" (openMenu)="drawerOpen.set(true)" />

        @if (session() && caches.revalidateErrors()) {
          <div data-testid="refresh-banner" role="status" class="flex flex-wrap items-center justify-center gap-x-4 gap-y-2 border-b border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900">
            <span><strong class="font-semibold">Couldn't refresh.</strong> Showing what we had.</span>
            <button type="button" class="btn-secondary !px-3 !py-1" (click)="caches.retryRevalidation()">Retry</button>
          </div>
        }

        @if (session() && (live.state() === 'reconnecting' || live.state() === 'offline')) {
          <div data-testid="live-banner" role="status" class="border-b border-amber-200 bg-amber-50 px-4 py-2 text-center text-sm text-amber-900">
            @if (live.state() === 'reconnecting') {
              <strong class="font-semibold">Reconnecting…</strong> Live updates paused. Your work is safe.
            } @else {
              <strong class="font-semibold">Offline.</strong> Live updates paused. Trying again every 30 seconds. Your work is safe.
            }
          </div>
        }

        <main id="main" tabindex="-1" class="flex-1 outline-none">
          <router-outlet />
        </main>

        <footer data-testid="version-footer" class="border-t border-slate-200 px-6 py-3 text-xs text-slate-500" [class]="mainPadding()">
          © {{ year }} Dale Abels ·
          @if (commitUrl(); as url) {
            Version <a class="font-mono underline hover:text-slate-700" [href]="url" target="_blank" rel="noopener">{{ shortVersion() }}</a>
          } @else {
            Version {{ shortVersion() }}
          }
        </footer>
      </div>
    </div>

    <app-menu-drawer [(open)]="drawerOpen" />

    @if (session() && auth.switchError(); as message) {
      <!-- A refused company switch: the page stays as it was; this says why and goes away by itself. -->
      <div
        data-testid="switch-error"
        role="alert"
        class="fixed inset-x-4 bottom-4 z-50 mx-auto flex max-w-md items-start gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900 shadow-lg"
      >
        <span class="min-w-0 flex-1">{{ message }}</span>
        <button type="button" class="-my-1 -mr-2 shrink-0 rounded-md p-1 text-red-900 hover:bg-red-100" aria-label="Dismiss" (click)="auth.dismissSwitchError()">
          <svg viewBox="0 0 24 24" class="size-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
        </button>
      </div>
    }

    @if (devPanelShown()) {
      <!-- Loaded on first use: most visits never open it, and it would push the initial bundle over its budget. -->
      @defer {
        <app-dev-panel />
      }
    }
  `,
})
export class Frame {
  protected readonly auth = inject(AuthStore);
  private readonly appConfig = inject(AppConfigService);
  protected readonly caches = inject(CacheRegistry);
  protected readonly live = inject(LiveConnection);
  private readonly devPanel = inject(DevPanelStore);
  private readonly notifications = inject(NotificationsStore);
  private readonly approvals = inject(ApprovalsStore);

  /** The footer's year and deployed version (config.json `version`: the commit SHA on a cluster, empty when run locally). */
  protected readonly year = new Date().getFullYear();
  private readonly version = computed(() => this.appConfig.config().version ?? '');
  protected readonly shortVersion = computed(() => {
    const v = this.version();
    return /^[0-9a-f]{40}$/.test(v) ? v.slice(0, 7) : v || 'development';
  });
  /** A full commit SHA links to that commit on GitHub; anything else (local-<sha7>, empty) is plain text. */
  protected readonly commitUrl = computed(() =>
    /^[0-9a-f]{40}$/.test(this.version()) ? `https://github.com/modefan/procure-ai/commit/${this.version()}` : null,
  );

  /** The phone drawer is open. */
  protected readonly drawerOpen = signal(false);
  /** Somebody is signed in and the demo is on: the only state in which the frame makes session calls. */
  protected readonly session = inject(SessionState).active;
  /** The "Behind the scenes" sheet is on screen: enabled in the config, opened by the user, and a session is there. */
  protected readonly devPanelShown = computed(() => this.session() && this.devPanel.active());
  /** Room at the bottom of the page so the open sheet does not cover its end. */
  protected readonly mainPadding = computed(() => (!this.devPanelShown() ? '' : this.devPanel.collapsed() ? 'pb-14' : 'pb-[42vh]'));

  constructor() {
    inject(Router)
      .events.pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.drawerOpen.set(false));

    // The toast of a refused company switch closes by itself after a while (or at once with its button).
    effect((onCleanup) => {
      if (this.auth.switchError()) {
        const timer = setTimeout(() => this.auth.dismissSwitchError(), SWITCH_ERROR_MS);
        onCleanup(() => clearTimeout(timer));
      }
    });

    // Fill the bell and the approvals count once a session is there (the live connection itself is started by LiveSession).
    effect(() => {
      if (this.session()) {
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

  /**
   * The skip link moves focus to the page without navigating: with `<base href="/">` its `#main` would resolve to `/#main` and load Welcome
   * from any other page. A link's click also fires for Enter, so keyboard users get the same.
   */
  protected skipToMain(event: Event): void {
    event.preventDefault();
    document.getElementById('main')?.focus();
  }

  protected onResize(): void {
    if (window.innerWidth >= SIDEBAR_MIN_WIDTH) {
      this.drawerOpen.set(false); // the drawer is for narrow screens; with room for the sidebar it goes away
    }
  }

  /** Alt+Shift+D shows or hides "Behind the scenes" (when the config enables it). */
  protected onKeydown(event: KeyboardEvent): void {
    if (event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && event.code === 'KeyD' && this.session() && this.devPanel.enabled()) {
      event.preventDefault();
      this.devPanel.toggle();
    }
  }
}
