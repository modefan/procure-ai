import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, untracked } from '@angular/core';
import { AuthStore } from '../../core/auth/auth.store';
import { DataChangeWho, serviceLabel } from '../../core/ui/data-change-who';
import { FieldDiff } from '../../core/ui/field-diff';
import { Pager } from '../../core/ui/pager';
import { DataChangesTabStore } from './data-changes-tab.store';

/**
 * The field-level change history of a request across services (request, lines, budget reservation): when, which table, who
 * made it ("outside the app" when no app transaction explains it) and each column's old and new value. Needs
 * {@link DataChangesTabStore} in a parent's `providers`, and is rendered only once the tab is opened, which is what loads it.
 */
@Component({
  selector: 'app-data-changes-tab',
  imports: [DatePipe, FieldDiff, DataChangeWho, Pager],
  template: `
    @if (store.error(); as message) {
      <div role="alert" class="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
        <span>{{ message }}</span>
        <button type="button" class="btn-secondary" (click)="store.retry()">Try again</button>
      </div>
    } @else if (store.isLoading() && store.items().length === 0) {
      <div class="space-y-3" role="status" aria-live="polite">
        <span class="sr-only">Loading the data changes</span>
        @for (row of skeletonRows; track row) {
          <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
        }
      </div>
    } @else if (store.items().length === 0) {
      <p class="text-sm text-slate-600" data-testid="no-data-changes">No data changes have been recorded for this request yet.</p>
    } @else {
      <ol class="divide-y divide-slate-100" data-testid="data-changes">
        @for (change of store.items(); track change.id) {
          <li class="py-3" [attr.data-change]="change.id">
            <div class="mb-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
              <time class="whitespace-nowrap text-slate-600 tabular-nums" [attr.datetime]="change.committedAt">{{ change.committedAt | date: 'd MMM y, HH:mm:ss' }}</time>
              <span class="font-medium text-slate-900">{{ service(change.database) }} &middot; <span class="font-mono text-xs sm:text-sm">{{ change.table }}</span></span>
              <span class="text-slate-700"><app-data-change-who [change]="change" [names]="names()" [me]="me()" unknown="a colleague" /></span>
            </div>
            <app-field-diff [fields]="change.fields" [operation]="change.operation" />
          </li>
        }
      </ol>
      <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="change" nounPlural="changes" (pageChange)="store.goToPage($event)" />
    }
  `,
})
export class DataChangesTab {
  readonly requestId = input.required<string>();
  /** Names the page already has (the history's actors), for people the user list is not readable for. */
  readonly knownNames = input<ReadonlyMap<string, string>>(new Map());
  private readonly auth = inject(AuthStore);
  protected readonly me = computed(() => this.auth.user()?.userId ?? null);
  /** The user list's names win; then the page's own; anyone else shows as "a colleague". */
  protected readonly names = computed(() => new Map([...this.knownNames(), ...this.store.names()]));
  protected readonly store = inject(DataChangesTabStore);
  protected readonly service = serviceLabel;
  protected readonly skeletonRows = [1, 2, 3];

  constructor() {
    effect(() => {
      const id = this.requestId();
      untracked(() => void this.store.open(id));
    });
  }
}
