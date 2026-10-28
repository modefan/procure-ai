import { Component, inject, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { RequestsStore, StatusFilter } from './requests.store';
import { Pager } from '../../core/ui/pager';
import { StatusBadge } from './status-badge';

/**
 * Screen 5: the purchase requests of the signed-in user's tenant, a page at a time (needs `requests.read`).
 * A table from the md breakpoint up; below it, one card per request, so a phone never scrolls sideways.
 */
@Component({
  selector: 'app-requests-page',
  imports: [StatusBadge, Pager, RouterLink],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">Purchase requests</h1>
          <p class="mt-1 text-slate-600">Amounts include VAT.</p>
        </div>
        @if (canCreate()) {
          <a routerLink="/app/requests/new" class="btn-primary">+ New request</a>
        }
      </div>

      <div class="mt-5 flex flex-wrap items-center gap-x-6 gap-y-3">
        <div role="group" aria-label="Whose requests" class="segmented">
          <button type="button" [attr.aria-pressed]="!store.mine()" (click)="store.setFilters({ mine: false })">All</button>
          <button type="button" [attr.aria-pressed]="store.mine()" (click)="store.setFilters({ mine: true })">Mine</button>
        </div>
        <div role="group" aria-label="Status" class="segmented">
          @for (option of statusOptions; track option.value) {
            <button type="button" [attr.aria-pressed]="store.status() === option.value" (click)="store.setFilters({ status: option.value })">{{ option.label }}</button>
          }
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load(store.page())">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading requests</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          @if (store.mine() || store.status() !== 'all') {
            <p class="text-lg font-semibold text-slate-900">No requests match these filters</p>
            <button type="button" class="btn-secondary mt-4" (click)="store.setFilters({ mine: false, status: 'all' })">Show all requests</button>
          } @else {
            <p class="text-lg font-semibold text-slate-900">No purchase requests yet</p>
            <p class="mt-1 text-slate-600">Requests raised by your company will appear here.</p>
            @if (canCreate()) {
              <a routerLink="/app/requests/new" class="btn-primary mt-4">+ New request</a>
            }
          }
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [attr.aria-busy]="store.isLoading()" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm md:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Purchase requests, page {{ store.page() }} of {{ store.pageCount() }}</caption>
              <colgroup>
                <col class="w-28" />
                <col />
                <col class="w-28 lg:w-32" />
                <col class="w-44" />
                <col class="w-28 lg:w-32" />
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Number</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Title</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Cost center</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Amount</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Status</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (request of store.entities(); track request.id) {
                  <tr class="hover:bg-slate-50">
                    <td class="px-4 py-3 font-medium whitespace-nowrap"><a [routerLink]="['/app/requests', request.id]" class="text-indigo-800 hover:underline">{{ request.number }}</a></td>
                    <td class="truncate px-4 py-3 text-slate-800" [title]="request.title"><a [routerLink]="['/app/requests', request.id]" class="hover:underline">{{ request.title }}</a></td>
                    <td class="truncate px-4 py-3 whitespace-nowrap text-slate-700">{{ request.costCenterCode }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ request.totalInclVatDisplay }}</td>
                    <td class="px-4 py-3"><app-status-badge [status]="request.status" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 md:hidden" aria-label="Purchase requests">
            @for (request of store.entities(); track request.id) {
              <li>
                <a [routerLink]="['/app/requests', request.id]" class="card block p-4 hover:bg-slate-50">
                  <span class="flex items-center justify-between gap-3">
                    <span class="font-semibold whitespace-nowrap text-slate-900">{{ request.number }}</span>
                    <app-status-badge [status]="request.status" />
                  </span>
                  <span class="mt-1 block truncate text-slate-800" [title]="request.title">{{ request.title }}</span>
                  <span class="mt-2 flex items-baseline justify-between gap-3 text-sm">
                    <span class="truncate text-slate-600">{{ request.costCenterCode }}</span>
                    <span class="font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ request.totalInclVatDisplay }}</span>
                  </span>
                </a>
              </li>
            }
          </ul>
        </div>

        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="request" nounPlural="requests" (pageChange)="store.load($event)" />
      }
    </section>
  `,
})
export class RequestsPage implements OnInit {
  protected readonly store = inject(RequestsStore);
  private readonly auth = inject(AuthStore);
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  protected readonly statusOptions: { value: StatusFilter; label: string }[] = [
    { value: 'all', label: 'Any status' },
    { value: 'open', label: 'Open' },
    { value: 'closed', label: 'Closed' },
  ];

  protected canCreate(): boolean {
    return this.auth.hasPermission('requests.create');
  }

  ngOnInit(): void {
    void this.store.load(1);
  }
}
