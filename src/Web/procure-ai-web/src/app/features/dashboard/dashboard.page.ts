import { Component, inject } from '@angular/core';
import { AuthStore } from '../../core/auth/auth.store';

/** Placeholder for screen 4. Shows who the gateway says is signed in, which is what later screens build on. */
@Component({
  selector: 'app-dashboard-page',
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Dashboard</h1>
      <p class="mt-1 text-slate-600">The dashboard arrives with the request screens. For now this is your session.</p>

      @if (auth.user(); as user) {
        <dl class="card mt-6 grid gap-x-6 gap-y-3 text-sm sm:grid-cols-[10rem_1fr]">
          <dt class="font-medium text-slate-500">Name</dt>
          <dd class="text-slate-900">{{ user.name }}</dd>
          <dt class="font-medium text-slate-500">Tenant</dt>
          <dd class="text-slate-900">{{ user.tenantId ?? 'none yet' }}</dd>
          <dt class="font-medium text-slate-500">Roles</dt>
          <dd class="text-slate-900">{{ user.roles.join(', ') || 'none' }}</dd>
          <dt class="font-medium text-slate-500">Permissions</dt>
          <dd class="break-words text-slate-900">{{ user.permissions.join(', ') || 'none' }}</dd>
        </dl>
      }
    </section>
  `,
})
export class DashboardPage {
  protected readonly auth = inject(AuthStore);
}
