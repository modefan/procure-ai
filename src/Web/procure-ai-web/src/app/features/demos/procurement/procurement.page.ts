import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../../core/auth/auth.store';
import { AppConfigService } from '../../../core/config/app-config';
import { DemoLogin, DemoLoginUser } from '../../../core/auth/demo-login';
import { MAINTENANCE_PATH, SystemAvailability } from '../../../core/system/system-availability';

interface FeatureTile {
  title: string;
  text: string;
}

/**
 * The Procurement demo's front page (lazy; the app itself loads only after sign-in). Signed out, it offers "Try a demo role" when the
 * gateway's demo login is on (its user list answers; a 404 hides the section) and, as a small secondary link, the normal Keycloak login.
 * While the demo is switched off it stays readable but asks the gateway nothing (it would answer 503 and open the maintenance page):
 * a short "paused" note links to the maintenance page (which has the contact link) instead of the demo roles and the login.
 */
@Component({
  selector: 'app-procurement-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-3xl px-4 pt-12 pb-10 text-center sm:pt-20">
      <h1 class="text-3xl font-bold tracking-tight text-slate-900 sm:text-5xl">Welcome to ProcureAI</h1>
      <p class="mx-auto mt-4 max-w-2xl text-base text-slate-600 sm:text-lg">
        Purchase requests, approvals, orders and invoices for South African companies, with an AI assistant that
        knows your procurement policy.
      </p>

      <div class="mt-8 flex flex-wrap items-center justify-center gap-3">
        @if (auth.isLoggedIn()) {
          <a routerLink="/app/dashboard" class="btn-primary">Go to dashboard</a>
        }
        <a routerLink="/demos/procurement/about" class="btn-secondary">How it is built</a>
      </div>

      @if (auth.isLoggedIn()) {
        <p class="mt-4 text-sm text-slate-600">Signed in as {{ auth.user()?.name ?? 'you' }}.</p>
      }
    </section>

    @if (paused()) {
      <p class="mx-auto max-w-4xl px-4 pb-10 text-center text-sm text-slate-600" role="status" data-testid="demo-paused">
        The demo is paused right now.
        <a [routerLink]="maintenancePath" class="font-semibold text-indigo-700 underline underline-offset-2">How to get it switched on</a>
      </p>
    } @else if (demoUsers().length > 0) {
      <section class="mx-auto max-w-4xl px-4 pb-10" aria-labelledby="demo-login-title" data-testid="demo-login">
        <div class="card">
          <h2 id="demo-login-title" class="text-lg font-semibold text-slate-900">Try a demo role</h2>
          <p class="mt-1 text-sm text-slate-600">No sign-up needed: one click signs you in as a demo user.</p>
          <ul class="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            @for (user of demoUsers(); track user.key) {
              <li>
                <button
                  type="button"
                  class="demo-role"
                  [disabled]="signingInAs() !== null"
                  [attr.aria-busy]="signingInAs() === user.key"
                  (click)="tryDemo(user)"
                >
                  <span class="truncate text-sm font-semibold text-slate-900" data-testid="demo-label">{{ user.label }}</span>
                  <span class="truncate text-xs text-slate-500">
                    @if (signingInAs() === user.key) {
                      Signing in…
                    } @else {
                      {{ user.tenant }}
                    }
                  </span>
                </button>
              </li>
            }
          </ul>
          @if (demoError()) {
            <p class="field-error mt-3" role="alert">Sorry, we could not sign you in as a demo user. Please try again.</p>
          }
        </div>
      </section>
    }

    @if (!auth.isLoggedIn() && !paused()) {
      <p class="mx-auto max-w-4xl px-4 pb-10 text-center text-sm text-slate-600">
        Have a Keycloak account?
        <a routerLink="/login" class="font-semibold text-indigo-700 underline underline-offset-2" data-testid="log-in-link">Log in</a>
        @if (registrationEnabled()) {
          <span aria-hidden="true"> · </span>
          <a routerLink="/register" class="font-semibold text-indigo-700 underline underline-offset-2">Register</a>
        }
      </p>
    }

    <section class="mx-auto max-w-6xl px-4 pb-16" aria-label="What you can do">
      <ul class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        @for (tile of tiles; track tile.title) {
          <li class="card" data-testid="feature-tile">
            <h2 class="text-base font-semibold text-slate-900">{{ tile.title }}</h2>
            <p class="mt-2 text-sm text-slate-600">{{ tile.text }}</p>
          </li>
        }
      </ul>
    </section>
  `,
})
export class ProcurementPage {
  protected readonly auth = inject(AuthStore);
  private readonly demoLogin = inject(DemoLogin);
  private readonly appConfig = inject(AppConfigService);
  private readonly availability = inject(SystemAvailability);

  /** The demo is switched off: no demo roles, no login, only the "paused" note. */
  protected readonly paused = this.availability.disabled;
  protected readonly maintenancePath = MAINTENANCE_PATH;

  /** "Register" only where the identity provider allows self-registration (runtime config, off by default). */
  protected readonly registrationEnabled = computed(() => this.appConfig.config().registration.enabled);

  /** Empty (no section) until the gateway lists demo users; stays empty when demo login is off. */
  protected readonly demoUsers = signal<readonly DemoLoginUser[]>([]);

  /** The key being signed in; every demo button waits meanwhile. */
  protected readonly signingInAs = signal<string | null>(null);
  protected readonly demoError = signal(false);

  protected readonly tiles: readonly FeatureTile[] = [
    { title: 'Request', text: 'Raise a purchase request in Rand; see your budget live.' },
    { title: 'Approve', text: 'Rules-based approval chain with reminders and escalation.' },
    { title: 'Order & pay', text: 'Orders to suppliers, deliveries, three-way invoice match.' },
    { title: 'Ask the policy assistant', text: 'AI answers with the policy sections it used.' },
  ];

  constructor() {
    // Who is signed in is known before the first page (app initializer); only a signed-out visitor is offered demo roles, and only while
    // the demo is on (switched off, the gateway refuses the call with a 503).
    if (!this.auth.isLoggedIn() && !this.availability.disabled()) {
      void this.demoLogin.users().then((users) => this.demoUsers.set(users));
    }
  }

  protected async tryDemo(user: DemoLoginUser): Promise<void> {
    this.signingInAs.set(user.key);
    this.demoError.set(false);
    try {
      // On success the browser leaves for the dashboard (a full page load); the busy state stays until then.
      await this.demoLogin.signIn(user.key);
    } catch {
      this.demoError.set(true);
      this.signingInAs.set(null);
    }
  }
}
