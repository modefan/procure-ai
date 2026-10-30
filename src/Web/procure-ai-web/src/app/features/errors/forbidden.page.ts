import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';

/** A permission name such as `admin.authorization`: letters, digits, dots, dashes. Anything else is not shown. */
const PERMISSION_NAME = /^[a-z][a-z0-9.-]{0,63}$/i;

/** Screen 20: the signed-in user may not see this page. */
@Component({
  selector: 'app-forbidden-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-xl px-4 py-16 text-center sm:py-24">
      <svg viewBox="0 0 24 24" class="mx-auto size-10 text-indigo-700" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
        <rect x="5" y="11" width="14" height="9" rx="2" /><path d="M8 11V8a4 4 0 0 1 8 0v3" />
      </svg>
      <p class="mt-3 text-sm font-semibold text-indigo-700">403</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900">You don't have access</h1>
      <p class="mt-4 text-slate-600">
        @if (permission; as name) {
          This page needs the permission <code class="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-sm text-slate-900">{{ name }}</code>. Ask an admin if you need it.
        } @else {
          Your role does not include the permission this page needs. Ask an admin if you need it.
        }
      </p>
      <div class="mt-8 flex flex-wrap justify-center gap-3">
        @if (auth.isLoggedIn()) {
          <a routerLink="/app/dashboard" class="btn-primary">Back to dashboard</a>
        } @else {
          <a routerLink="/" class="btn-primary">Back to welcome</a>
        }
      </div>
    </section>
  `,
})
export class ForbiddenPage {
  protected readonly auth = inject(AuthStore);
  /** What `permissionGuard` was missing: from the navigation state, or from the history entry after a page reload. */
  protected readonly permission = this.missingPermission();

  private missingPermission(): string | null {
    const fromNavigation = inject(Router).currentNavigation()?.extras.state?.['permission'];
    const value: unknown = fromNavigation ?? (history.state as { permission?: unknown } | null)?.permission;
    return typeof value === 'string' && PERMISSION_NAME.test(value) ? value : null;
  }
}
