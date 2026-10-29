import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { AuthStore } from '../../core/auth/auth.store';
import { RandPipe } from '../../core/ui/money.pipe';
import { currentFiscalYear } from '../dashboard/dashboard.store';
import { BudgetEditDialog } from './budget-edit-dialog';
import { BudgetsStore } from './budgets.store';
import { fiscalYearLabel } from './fiscal-year';

/**
 * Screen 12: the budget of each cost center this user may see (needs `budgets.read`; the API shows a requester only their own
 * cost centers) for the current fiscal year, with what is spent, reserved and still available, exactly as the server worked
 * them out. Users with `budgets.manage` can change an amount. A table from the lg breakpoint up; below it, one card per cost center.
 */
@Component({
  selector: 'app-budgets-page',
  imports: [RandPipe, BudgetEditDialog],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">Budgets</h1>
          <p class="mt-1 text-slate-600">
            <span class="font-medium whitespace-nowrap text-slate-800" data-fiscal-year>{{ fiscalYear() }}</span>
            &middot; March to February, in Rand
          </p>
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading budgets</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          <p class="text-lg font-semibold text-slate-900">No budgets</p>
          <p class="mt-1 text-slate-600">There is no budget for you to see in {{ fiscalYear() }}.</p>
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Budgets per cost center, {{ fiscalYear() }}</caption>
              <colgroup>
                <col class="w-32" />
                <col />
                <col class="w-36" />
                <col class="w-36" />
                <col class="w-36" />
                <col class="w-36" />
                @if (mayManage()) {
                  <col class="w-24" />
                }
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Cost center</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Owner</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Budget</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Spent</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Reserved</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Available</th>
                  @if (mayManage()) {
                    <th scope="col" class="px-4 py-3 text-right whitespace-nowrap"><span class="sr-only">Actions</span></th>
                  }
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (budget of store.entities(); track budget.costCenterCode) {
                  <tr class="hover:bg-slate-50" [attr.data-budget]="budget.costCenterCode">
                    <th scope="row" class="truncate px-4 py-3 font-semibold text-slate-900" [title]="budget.costCenterCode">{{ budget.costCenterCode }}</th>
                    <td class="truncate px-4 py-3 text-slate-800" data-cell="owner" [title]="budget.ownerName ?? ''">{{ budget.ownerName ?? 'Not set' }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.amount | rand }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.spent | rand }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.reserved | rand }}</td>
                    <td class="px-4 py-3 text-right font-semibold whitespace-nowrap tabular-nums" [class]="budget.available < 0 ? 'text-red-700' : 'text-slate-900'" data-cell="available">{{ budget.available | rand }}</td>
                    @if (mayManage()) {
                      <td class="px-4 py-3 text-right">
                        <button type="button" class="btn-secondary px-3 py-1" data-action="edit" [attr.aria-label]="'Change budget of ' + budget.costCenterCode" (click)="edit(budget.costCenterCode)">Edit</button>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 lg:hidden" aria-label="Budgets">
            @for (budget of store.entities(); track budget.costCenterCode) {
              <li class="card p-4" [attr.data-budget-card]="budget.costCenterCode">
                <div class="flex items-start justify-between gap-3">
                  <div class="min-w-0">
                    <p class="truncate font-semibold text-slate-900" [title]="budget.costCenterCode">{{ budget.costCenterCode }}</p>
                    <p class="truncate text-sm text-slate-600" [title]="budget.ownerName ?? ''">{{ budget.ownerName ?? 'No owner set' }}</p>
                  </div>
                  @if (mayManage()) {
                    <button type="button" class="btn-secondary px-3 py-1" data-action="edit" [attr.aria-label]="'Change budget of ' + budget.costCenterCode" (click)="edit(budget.costCenterCode)">Edit</button>
                  }
                </div>
                <dl class="mt-3 grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
                  <dt class="text-slate-600">Budget</dt>
                  <dd class="text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.amount | rand }}</dd>
                  <dt class="text-slate-600">Spent</dt>
                  <dd class="text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.spent | rand }}</dd>
                  <dt class="text-slate-600">Reserved</dt>
                  <dd class="text-right whitespace-nowrap tabular-nums text-slate-900">{{ budget.reserved | rand }}</dd>
                  <dt class="font-semibold text-slate-800">Available</dt>
                  <dd class="text-right font-semibold whitespace-nowrap tabular-nums" [class]="budget.available < 0 ? 'text-red-700' : 'text-slate-900'">{{ budget.available | rand }}</dd>
                </dl>
              </li>
            }
          </ul>
        </div>
      }

      @if (editingBudget(); as budget) {
        <app-budget-edit-dialog [budget]="budget" (closed)="editing.set(null)" />
      }
    </section>
  `,
})
export class BudgetsPage implements OnInit {
  protected readonly store = inject(BudgetsStore);
  private readonly auth = inject(AuthStore);
  protected readonly skeletonRows = [1, 2, 3, 4];

  protected readonly mayManage = computed(() => this.auth.hasPermission('budgets.manage'));
  protected readonly fiscalYear = computed(() => fiscalYearLabel(this.store.entities()[0]?.fiscalYear ?? currentFiscalYear()));
  /** The cost center being edited. */
  protected readonly editing = signal<string | null>(null);
  protected readonly editingBudget = computed(() => {
    const code = this.editing();
    return code && this.mayManage() ? (this.store.entityMap()[code] ?? null) : null;
  });

  protected edit(code: string): void {
    this.editing.set(code);
  }

  protected retry(): void {
    void this.store.load();
  }

  ngOnInit(): void {
    void this.store.load();
  }
}
