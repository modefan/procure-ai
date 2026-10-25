import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

interface FeatureTile {
  title: string;
  text: string;
}

/** Screen 1: the public landing page. Part of the small public bundle; the app itself loads only after sign-in. */
@Component({
  selector: 'app-welcome-page',
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
        } @else {
          <a routerLink="/login" class="btn-primary">Log in</a>
          <a routerLink="/register" class="btn-secondary">Register</a>
        }
      </div>

      @if (auth.isLoggedIn()) {
        <p class="mt-4 text-sm text-slate-600">Signed in as {{ auth.user()?.name ?? 'you' }}.</p>
      }
    </section>

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
export class WelcomePage {
  protected readonly auth = inject(AuthStore);

  protected readonly tiles: readonly FeatureTile[] = [
    { title: 'Request', text: 'Raise a purchase request in Rand; see your budget live.' },
    { title: 'Approve', text: 'Rules-based approval chain with reminders and escalation.' },
    { title: 'Order & pay', text: 'Orders to suppliers, deliveries, three-way invoice match.' },
    { title: 'Ask the policy assistant', text: 'AI answers with the policy sections it used.' },
  ];
}
