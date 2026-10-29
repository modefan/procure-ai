import { Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { applyEach, form, FormField, submit, validate } from '@angular/forms/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PayLineBody } from '../../api/invoicing/models/pay-line-body';
import { BusyButton } from '../../core/ui/busy-button';
import { formatRand } from '../../core/ui/money.pipe';
import { invoiceTotals } from './invoice-totals';

interface Row {
  lineNo: number;
  description: string;
  payable: number;
  /** The order's unit price, for the preview of what the chosen quantities cost. */
  unitPrice: number;
  quantity: number;
}

interface Model {
  lines: Row[];
}

const rowsOf = (invoice: InvoiceDto): Row[] =>
  invoice.lines
    .filter((l) => l.payable > 0)
    .map((l) => ({ lineNo: l.lineNo, description: l.description, payable: l.payable, unitPrice: l.orderUnitPriceExclVat ?? l.unitPriceExclVat, quantity: 0 }));

/**
 * "Pay chosen quantities": a quantity per line that can be paid now (0 to the payable quantity, whole numbers), at
 * least one above zero. The preview of the amount is display only (the order price, VAT on the total); the server
 * works out the payment. The parent posts it (`submitted`).
 */
@Component({
  selector: 'app-pay-lines-form',
  imports: [FormField, BusyButton],
  template: `
    <form novalidate (submit)="$event.preventDefault()" class="space-y-3">
      <div class="hidden grid-cols-[minmax(0,1fr)_7rem_8rem] gap-3 border-b border-slate-200 pb-2 text-xs font-semibold uppercase tracking-wide text-slate-600 sm:grid">
        <span>Item</span>
        <span class="text-right whitespace-nowrap">Can be paid</span>
        <span class="text-right whitespace-nowrap">Pay now</span>
      </div>
      <div class="divide-y divide-slate-100">
        @for (line of payForm.lines; track $index; let i = $index) {
          <div class="grid grid-cols-[minmax(0,1fr)_8rem] items-start gap-x-3 gap-y-1 py-3 sm:grid-cols-[minmax(0,1fr)_7rem_8rem]" [attr.data-pay-line]="line().value().lineNo">
            <div class="min-w-0">
              <p class="truncate font-medium text-slate-900" [title]="line().value().description">{{ line().value().description }}</p>
              <p class="text-sm whitespace-nowrap text-slate-600 sm:hidden">{{ line().value().payable }} can be paid</p>
            </div>
            <p class="hidden pt-2 text-right text-sm whitespace-nowrap tabular-nums text-slate-700 sm:block">{{ line().value().payable }}</p>
            <div>
              <input
                type="number"
                inputmode="numeric"
                class="field-input text-right tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none"
                [attr.aria-label]="'Quantity to pay, ' + line().value().description"
                [attr.aria-invalid]="lineError(i) ? 'true' : null"
                [formField]="line.quantity"
              />
              @if (lineError(i); as message) {
                <p class="field-error text-right">{{ message }}</p>
              }
            </div>
          </div>
        }
      </div>
      @if (tried() && noQuantity()) {
        <p role="alert" class="field-error">Enter a quantity for at least one item.</p>
      }
      <div class="flex flex-wrap items-center justify-end gap-3">
        <div data-action="pay-chosen">
          <app-busy-button type="submit" [busy]="busy()" [disabled]="disabled()" busyLabel="Paying…" (pressed)="send()">Pay chosen quantities ({{ preview() }})</app-busy-button>
        </div>
      </div>
    </form>
  `,
})
export class PayLinesForm {
  readonly invoice = input.required<InvoiceDto>();
  readonly busy = input(false);
  /** Another action is running. */
  readonly disabled = input(false);
  readonly submitted = output<PayLineBody[]>();

  protected readonly model = signal<Model>({ lines: [] });
  protected readonly tried = signal(false);

  protected readonly payForm = form(this.model, (p) => {
    applyEach(p.lines, (line) => {
      validate(line.quantity, (ctx) => {
        const value = ctx.value();
        const payable = ctx.valueOf(line.payable);
        if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isInteger(value)) {
          return { kind: 'whole', message: 'Enter a whole number' };
        }
        if (value < 0) {
          return { kind: 'min', message: 'Enter 0 or more' };
        }
        return value > payable ? { kind: 'max', message: `Only ${payable} can be paid` } : undefined;
      });
    });
  });

  protected readonly noQuantity = computed(() => !this.model().lines.some((l) => typeof l.quantity === 'number' && l.quantity > 0));
  protected readonly preview = computed(() => {
    // A quantity that is not valid (more than payable, not a number) counts as at most what can be paid: the preview never shows more than a payment could.
    const clamp = (l: Row) => (Number.isFinite(l.quantity) ? Math.min(Math.max(Math.floor(l.quantity), 0), l.payable) : 0);
    const totals = invoiceTotals(this.model().lines.map((l) => ({ quantity: clamp(l), unitPriceExclVat: l.unitPrice })));
    return formatRand(totals.inclVat);
  });

  constructor() {
    // A new version of the invoice (a payment just made) starts the form over; a push that changes nothing keeps what is typed.
    const startKey = computed(() => `${this.invoice().id}:${this.invoice().version}`);
    effect(() => {
      startKey();
      untracked(() => {
        this.model.set({ lines: rowsOf(this.invoice()) });
        this.tried.set(false);
      });
    });
  }

  protected lineError(index: number): string | null {
    const state = this.payForm.lines[index].quantity();
    return state.touched() && state.errors().length > 0 ? (state.errors()[0].message ?? 'Check this value') : null;
  }

  protected async send(): Promise<void> {
    this.tried.set(true);
    await submit(this.payForm, async () => {
      if (this.noQuantity()) {
        return;
      }
      this.submitted.emit(this.model().lines.filter((l) => l.quantity > 0).map((l) => ({ lineNo: l.lineNo, quantity: l.quantity })));
    });
  }
}
