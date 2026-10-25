import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

/** The gateway could not say who is signed in (server error or network). Not a login problem, so no login redirect. */
@Component({
  selector: 'app-unavailable-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-xl px-4 py-16 text-center sm:py-24">
      <p class="text-sm font-semibold text-indigo-700">Service unavailable</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900">We cannot reach the service right now</h1>
      <p class="mt-4 text-slate-600">
        Something on our side is not answering. Your session is not affected. Try again in a moment.
      </p>
      <div class="mt-8 flex flex-wrap justify-center gap-3">
        <button type="button" class="btn-primary" [disabled]="retrying()" (click)="retry()">
          {{ retrying() ? 'Trying...' : 'Try again' }}
        </button>
        <a routerLink="/" class="btn-secondary">Back to welcome</a>
      </div>
      @if (stillDown()) {
        <p role="alert" class="mt-4 text-sm text-red-700">Still not answering.</p>
      }
    </section>
  `,
})
export class UnavailablePage {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  protected readonly retrying = signal(false);
  protected readonly stillDown = signal(false);

  protected async retry(): Promise<void> {
    this.retrying.set(true);
    this.stillDown.set(false);
    await this.auth.load();
    this.retrying.set(false);
    if (this.auth.error()) {
      this.stillDown.set(true);
    } else {
      await this.router.navigateByUrl(this.auth.isLoggedIn() ? '/app/dashboard' : '/');
    }
  }
}
