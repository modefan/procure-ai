import { Component, input, output } from '@angular/core';

/**
 * Previous / next and "Page 2 of 5 (41 requests)" for a paged list. Pass `loading` to disable both buttons while a
 * page is on its way. The page is 1-based; `pageChange` carries the page to go to.
 */
@Component({
  selector: 'app-pager',
  template: `
    <nav class="mt-4 flex flex-wrap items-center justify-between gap-3" aria-label="Pagination">
      <p class="text-sm text-slate-600" aria-live="polite">
        Page {{ page() }} of {{ pageCount() }} ({{ total() }} {{ total() === 1 ? noun() : nounPlural() }})
      </p>
      <div class="flex gap-2">
        <button type="button" class="btn-secondary disabled:cursor-not-allowed disabled:opacity-50" [disabled]="page() <= 1 || loading()" (click)="pageChange.emit(page() - 1)">
          <span aria-hidden="true">&lsaquo;</span> Previous page
        </button>
        <button type="button" class="btn-secondary disabled:cursor-not-allowed disabled:opacity-50" [disabled]="page() >= pageCount() || loading()" (click)="pageChange.emit(page() + 1)">
          Next page <span aria-hidden="true">&rsaquo;</span>
        </button>
      </div>
    </nav>
  `,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageCount = input.required<number>();
  readonly total = input.required<number>();
  readonly loading = input(false);
  readonly noun = input('item');
  readonly nounPlural = input('items');
  readonly pageChange = output<number>();
}
