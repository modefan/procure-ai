import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, OnInit, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { DataChangeWho, SERVICE_LABELS, serviceLabel } from '../../../core/ui/data-change-who';
import { FieldDiff } from '../../../core/ui/field-diff';
import { Pager } from '../../../core/ui/pager';
import { DataChangesFilter, DataChangesStore, NO_FILTER } from './data-changes.store';

const TRUE = new Set(['true', '1', 'yes']);

/**
 * The data-change log (needs `admin.system`): every captured database change, newest first, with who made it. The filters live
 * in the URL (`service`, `table`, `user`, `outside`, `from`, `to`, which the router binds to the inputs below), so a filtered
 * view can be linked to, e.g. the system status page's "changed outside the app" link.
 */
@Component({
  selector: 'app-data-changes-page',
  imports: [DatePipe, Pager, RouterLink, FieldDiff, DataChangeWho],
  providers: [DataChangesStore],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/admin/system" class="text-sm text-indigo-700 hover:underline">&lsaquo; System status</a>
      <h1 class="mt-2 text-2xl font-bold tracking-tight text-slate-900">Data changes</h1>
      <p class="mt-1 text-slate-600">
        Every change to the services' databases, with the old and new value of each column. A change nobody made through the app
        shows as &ldquo;outside the app&rdquo;.
      </p>

      <div class="card mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4" role="group" aria-label="Filters">
        <div>
          <label for="dc-service" class="field-label">Service</label>
          <select id="dc-service" class="field-input" [value]="filter().service" (change)="pick('service', $event)">
            <option value="">All services</option>
            @for (option of serviceOptions(); track option.value) {
              <option [value]="option.value" [selected]="option.value === filter().service">{{ option.label }}</option>
            }
          </select>
        </div>
        <div>
          <label for="dc-table" class="field-label">Table</label>
          <input id="dc-table" type="text" class="field-input" placeholder="e.g. purchase_requests" autocomplete="off" [value]="filter().table" (change)="pick('table', $event)" />
        </div>
        <div>
          <label for="dc-user" class="field-label">Changed by</label>
          <select id="dc-user" class="field-input" (change)="pick('user', $event)">
            <option value="" [selected]="filter().user === ''">Anyone</option>
            @for (user of userOptions(); track user.id) {
              <option [value]="user.id" [selected]="user.id === filter().user">{{ user.name }}</option>
            }
          </select>
        </div>
        <div class="flex items-end">
          <label class="flex cursor-pointer items-center gap-2 pb-2 text-sm font-medium text-slate-800">
            <input id="dc-outside" type="checkbox" class="size-4 accent-indigo-700" [checked]="filter().outside" (change)="toggleOutside($event)" />
            Outside the app only
          </label>
        </div>
        <div>
          <label for="dc-from" class="field-label">From</label>
          <input id="dc-from" type="date" class="field-input" [value]="filter().from" (change)="pick('from', $event)" />
        </div>
        <div>
          <label for="dc-to" class="field-label">To</label>
          <input id="dc-to" type="date" class="field-input" [value]="filter().to" (change)="pick('to', $event)" />
        </div>
        <div class="flex items-end sm:col-span-2">
          @if (filtered()) {
            <button type="button" class="btn-secondary" (click)="clear()">Clear filters</button>
          }
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.reload()">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the data changes</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (!store.error()) {
        <div class="mt-6 rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="data-changes">
          <div class="hidden border-b border-slate-200 px-4 py-2 text-xs font-semibold tracking-wide text-slate-600 uppercase md:grid md:grid-cols-[9.5rem_8rem_minmax(0,1fr)_5.5rem_9rem_5.5rem] md:gap-x-4" aria-hidden="true">
            <span>When</span><span>Service</span><span>Table &middot; key</span><span>Change</span><span>Who</span><span></span>
          </div>
          <ul class="divide-y divide-slate-100">
            @for (change of store.entities(); track change.id) {
              <li [attr.data-change]="change.id" class="px-4 py-3">
                <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm md:grid md:grid-cols-[9.5rem_8rem_minmax(0,1fr)_5.5rem_9rem_5.5rem]">
                  <time class="whitespace-nowrap text-slate-600 tabular-nums" [attr.datetime]="change.committedAt">{{ change.committedAt | date: 'd MMM y, HH:mm:ss' }}</time>
                  <span class="font-medium whitespace-nowrap text-slate-900">{{ serviceLabel(change.database) }}</span>
                  <span class="min-w-0 basis-full truncate md:basis-auto" [title]="change.table + ' ' + change.primaryKey">
                    <span class="font-mono text-xs sm:text-sm">{{ change.table }}</span>
                    <span class="font-mono text-xs text-slate-500"> {{ change.primaryKey }}</span>
                  </span>
                  <span class="text-slate-700">{{ change.operation }}</span>
                  <span class="min-w-0 text-slate-800"><app-data-change-who [change]="change" [names]="store.names()" /></span>
                  <button
                    type="button"
                    class="btn-secondary ml-auto px-2 py-1 text-xs md:ml-0"
                    [attr.aria-expanded]="isOpen(change.id)"
                    [attr.aria-controls]="'diff-' + change.id"
                    (click)="toggle(change.id)"
                  >
                    {{ isOpen(change.id) ? 'Hide' : 'Details' }}
                  </button>
                </div>
                @if (isOpen(change.id)) {
                  <div [id]="'diff-' + change.id" class="mt-3 rounded-lg bg-slate-50 px-3 py-3 ring-1 ring-slate-200 ring-inset">
                    <app-field-diff [fields]="change.fields" [operation]="change.operation" />
                  </div>
                }
              </li>
            } @empty {
              <li class="px-4 py-8 text-center text-slate-600" data-testid="no-data-changes">
                {{ filtered() ? 'No data changes match these filters.' : 'No data changes have been recorded yet.' }}
              </li>
            }
          </ul>
        </div>
        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="change" nounPlural="changes" (pageChange)="store.setPage($event)" />
      }
    </section>
  `,
})
export class DataChangesPage implements OnInit {
  // Bound from the URL's query string (withComponentInputBinding); absent = not filtered.
  readonly service = input<string | undefined>();
  readonly table = input<string | undefined>();
  readonly user = input<string | undefined>();
  readonly outside = input<string | undefined>();
  readonly from = input<string | undefined>();
  readonly to = input<string | undefined>();

  protected readonly store = inject(DataChangesStore);
  private readonly router = inject(Router);
  protected readonly serviceLabel = serviceLabel;
  protected readonly skeletonRows = [1, 2, 3, 4];
  private readonly open = signal<ReadonlySet<string>>(new Set());

  /** The filter the URL asks for. */
  protected readonly filter = computed<DataChangesFilter>(() => ({
    service: this.service()?.trim() ?? '',
    table: this.table()?.trim() ?? '',
    user: this.user()?.trim() ?? '',
    outside: TRUE.has((this.outside() ?? '').toLowerCase()),
    from: this.from()?.trim() ?? '',
    to: this.to()?.trim() ?? '',
  }));
  protected readonly filtered = computed(() => JSON.stringify(this.filter()) !== JSON.stringify(NO_FILTER));
  protected readonly serviceOptions = computed(() => {
    const options = Object.entries(SERVICE_LABELS).map(([value, label]) => ({ value, label }));
    const current = this.filter().service;
    return current && !(current in SERVICE_LABELS) ? [...options, { value: current, label: current }] : options;
  });
  /** Every user Access knows, plus the filtered id when it is not among them. */
  protected readonly userOptions = computed(() => {
    const names = this.store.names();
    const list = [...names].map(([id, name]) => ({ id, name }));
    const current = this.filter().user;
    if (current && !names.has(current)) {
      list.push({ id: current, name: current });
    }
    return list.sort((a, b) => a.name.localeCompare(b.name));
  });

  constructor() {
    // The URL is the source of truth: every change of the query string shows page 1 of the new filter (also the first one).
    effect(() => {
      const filter = this.filter();
      untracked(() => void this.store.setFilter(filter));
    });
  }

  ngOnInit(): void {
    void this.store.loadUsers();
  }

  protected isOpen(id: string): boolean {
    return this.open().has(id);
  }

  protected toggle(id: string): void {
    this.open.update((open) => {
      const next = new Set(open);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }

  protected pick(key: 'service' | 'table' | 'user' | 'from' | 'to', event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value.trim();
    void this.navigate({ [key]: value || null });
  }

  protected toggleOutside(event: Event): void {
    void this.navigate({ outside: (event.target as HTMLInputElement).checked ? 'true' : null });
  }

  protected clear(): void {
    void this.navigate({ service: null, table: null, user: null, outside: null, from: null, to: null });
  }

  private navigate(queryParams: Record<string, string | null>): Promise<boolean> {
    return this.router.navigate([], { queryParams, queryParamsHandling: 'merge' });
  }
}
