import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

/** Screen 20: the signed-in user may not see this page. */
@Component({
  selector: 'app-forbidden-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-xl px-4 py-16 text-center sm:py-24">
      <p class="text-sm font-semibold text-indigo-700">403</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900">You do not have access to this page</h1>
      <p class="mt-4 text-slate-600">
        Your role does not include the permission this page needs. If you think it should, ask an administrator.
      </p>
      <div class="mt-8 flex flex-wrap justify-center gap-3">
        @if (auth.isLoggedIn()) {
          <a routerLink="/app/dashboard" class="btn-primary">Go to dashboard</a>
        }
        <a routerLink="/" class="btn-secondary">Back to welcome</a>
      </div>
    </section>
  `,
})
export class ForbiddenPage {
  protected readonly auth = inject(AuthStore);
}
