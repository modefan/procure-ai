import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Screen 21: no such page (or, later, a record that is missing or belongs to another tenant). */
@Component({
  selector: 'app-not-found-page',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-xl px-4 py-16 text-center sm:py-24">
      <p class="text-sm font-semibold text-indigo-700">404</p>
      <h1 class="mt-2 text-3xl font-bold tracking-tight text-slate-900">Page not found</h1>
      <p class="mt-4 text-slate-600">The page you asked for does not exist, or it has moved.</p>
      <div class="mt-8 flex justify-center">
        <a routerLink="/" class="btn-primary">Back to welcome</a>
      </div>
    </section>
  `,
})
export class NotFoundPage {}
