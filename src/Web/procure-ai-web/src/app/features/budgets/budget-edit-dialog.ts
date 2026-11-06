import { Component, computed, effect, ElementRef, inject, input, OnDestroy, output, signal, untracked, viewChild } from '@angular/core';
import { form, FormField, submit, validate } from '@angular/forms/signals';
import { BudgetDto } from '../../api/budgets/models/budget-dto';
import { BusyButton } from '../../core/ui/busy-button';
import { formatRand } from '../../core/ui/money.pipe';
import { BudgetsStore } from './budgets.store';
import { fiscalYearLabel } from './fiscal-year';

/**
 * Change the amount of one cost center budget (needs `budgets.manage`). A native modal dialog like the confirm and conflict
 * dialogs: the browser traps the focus and Esc closes it (not while a save runs). The amount is checked for being a number only;
 * whether it is below what is spent plus reserved is the server's call, and its message (422) is shown inside the dialog.
 * `budget` is the live row of the store, so after a conflict the dialog shows the latest figures. A typed amount stays (Save then goes out
 * on the latest version); an amount the user did not change takes the latest budget, so Save never writes an old value back.
 */
@Component({
  selector: 'app-budget-edit-dialog',
  imports: [FormField, BusyButton],
  template: `
    <dialog
      #dialog
      class="m-auto w-[calc(100%-2rem)] max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-xl backdrop:bg-slate-900/50"
      aria-labelledby="budget-edit-title"
      (cancel)="onCancel($event)"
      (close)="closed.emit()"
    >
      <h2 id="budget-edit-title" class="text-lg font-semibold text-slate-900">Change budget of {{ budget().costCenterCode }}</h2>
      <p class="mt-1 text-sm text-slate-600">{{ label() }}@if (budget().ownerName) { &middot; {{ budget().ownerName }} }</p>

      <dl class="mt-4 grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
        <dt class="text-slate-600">Spent</dt>
        <dd class="text-right whitespace-nowrap tabular-nums text-slate-900">{{ money(budget().spent) }}</dd>
        <dt class="text-slate-600">Reserved</dt>
        <dd class="text-right whitespace-nowrap tabular-nums text-slate-900">{{ money(budget().reserved) }}</dd>
        <dt class="font-medium text-slate-700">Spent plus reserved</dt>
        <dd class="text-right font-medium whitespace-nowrap tabular-nums text-slate-900">{{ money(budget().spent + budget().reserved) }}</dd>
      </dl>

      <form novalidate class="mt-4" (submit)="$event.preventDefault()">
        <label for="budget-amount" class="field-label">New budget (Rand)</label>
        <input
          id="budget-amount"
          type="number"
          inputmode="decimal"
          step="0.01"
          class="field-input text-right tabular-nums"
          [formField]="amountForm.amount"
          [attr.aria-invalid]="amountError() ? 'true' : null"
          [attr.aria-describedby]="amountError() ? 'budget-amount-error' : 'budget-amount-hint'"
        />
        @if (amountError(); as message) {
          <p id="budget-amount-error" class="field-error">{{ message }}</p>
        } @else {
          <p id="budget-amount-hint" class="mt-1 text-sm text-slate-600">It cannot be lower than spent plus reserved.</p>
        }
        @if (refreshed(); as kept) {
          <p class="mt-1 text-sm text-amber-800" data-hint="refreshed" role="status">
            The figures were just updated by someone else.
            @if (kept === 'typed') {
              The budget is now {{ money(budget().amount) }}; your amount is kept, Save applies it.
            } @else {
              The amount shows the latest budget.
            }
          </p>
        }
        @if (store.saveError(); as message) {
          <p role="alert" class="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900">{{ keepAmounts(message) }}</p>
        }
        <div class="mt-6 flex flex-wrap justify-end gap-3">
          <button type="button" class="btn-secondary" [disabled]="busy()" (click)="close()">Cancel</button>
          <div data-action="save-budget">
            <app-busy-button type="submit" [busy]="busy()" busyLabel="Saving…" (pressed)="save()">Save budget</app-busy-button>
          </div>
        </div>
      </form>
    </dialog>
  `,
})
export class BudgetEditDialog implements OnDestroy {
  protected readonly store = inject(BudgetsStore);
  readonly budget = input.required<BudgetDto>();
  /** The dialog is closed (saved, cancelled or Esc); the parent removes it. */
  readonly closed = output<void>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly model = signal<{ amount: number }>({ amount: 0 });
  protected readonly tried = signal(false);
  /** The figures were refreshed under the open dialog after a conflict: whether the typed amount was kept or replaced by the latest. */
  protected readonly refreshed = signal<'typed' | 'latest' | null>(null);
  /** The amount the form started from: the budget when the dialog opened, then the latest after each conflict. */
  private baseAmount = 0;

  protected readonly amountForm = form(this.model, (p) => {
    validate(p.amount, (ctx) => {
      const value = ctx.value();
      return typeof value !== 'number' || !Number.isFinite(value) ? { kind: 'number', message: 'Enter an amount' } : undefined;
    });
  });

  protected readonly label = computed(() => fiscalYearLabel(this.budget().fiscalYear));
  protected readonly busy = computed(() => this.store.busyCode() === this.budget().costCenterCode);
  protected readonly amountError = computed(() => {
    const state = this.amountForm.amount();
    return (state.touched() || this.tried()) && state.errors().length > 0 ? (state.errors()[0].message ?? 'Check this value') : null;
  });

  private started = false;

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      if (!this.started) {
        this.started = true;
        this.store.clearSaveError();
        this.model.set({ amount: this.budget().amount });
        this.baseAmount = this.budget().amount;
        if (!element.open) {
          element.showModal();
        }
      }
    });

    // A save that lost to someone else (412): the store has loaded the latest budget. A typed amount stays; an untouched one takes the latest.
    let seenConflicts = this.store.conflicts();
    effect(() => {
      const conflicts = this.store.conflicts();
      if (conflicts !== seenConflicts) {
        seenConflicts = conflicts;
        const amount = untracked(() => this.budget().amount);
        untracked(() => {
          const typed = this.model().amount !== this.baseAmount;
          if (!typed) {
            this.model.set({ amount });
          }
          this.baseAmount = amount;
          this.refreshed.set(typed ? 'typed' : 'latest');
        });
      }
    });
  }

  ngOnDestroy(): void {
    this.store.clearSaveError();
  }

  protected money(amount: number): string {
    return formatRand(amount);
  }

  /** The server writes "R 96 600,00" with a plain space after the R; keep the R with its number so a message never wraps between them. */
  protected keepAmounts(message: string): string {
    return message.replace(/R (?=\d)/g, 'R ');
  }

  /** Esc does nothing while a save runs. */
  protected onCancel(event: Event): void {
    if (this.busy()) {
      event.preventDefault();
    }
  }

  protected close(): void {
    this.dialog().nativeElement.close();
  }

  protected async save(): Promise<void> {
    this.tried.set(true);
    await submit(this.amountForm, async () => {
      if (await this.store.save(this.budget().costCenterCode, this.model().amount)) {
        this.close();
      }
    });
  }
}
