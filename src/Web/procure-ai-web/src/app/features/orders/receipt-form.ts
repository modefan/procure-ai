import { Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { applyEach, form, FormField, maxLength, submit, validate } from '@angular/forms/signals';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { BusyButton } from '../../core/ui/busy-button';
import { KeepRandPipe } from '../../core/ui/money.pipe';
import { ReceiptInput } from './order-detail.store';

interface Row {
  lineNo: number;
  description: string;
  outstanding: number;
  quantity: number;
}

interface Model {
  deliveryNote: string;
  lines: Row[];
}

const MAX_NOTE = 100;

const emptyModel = (order: PurchaseOrderDto): Model => ({
  deliveryNote: '',
  lines: order.lines.filter((l) => l.outstanding > 0).map((l) => ({ lineNo: l.lineNo, description: l.description, outstanding: l.outstanding, quantity: 0 })),
});

/**
 * "Record delivery": a quantity per line that still has something outstanding (0 to the outstanding quantity, whole
 * numbers) and an optional delivery note. At least one quantity must be above zero. The parent posts the receipt
 * (`submitted`) and passes what the server said back in (`serverErrors` by `line.<number>`, `generalError`).
 */
@Component({
  selector: 'app-receipt-form',
  imports: [FormField, BusyButton, KeepRandPipe],
  template: `
    <form novalidate (submit)="$event.preventDefault()" class="space-y-4">
      <div class="hidden grid-cols-[minmax(0,1fr)_7rem_8rem] gap-3 border-b border-slate-200 pb-2 text-xs font-semibold uppercase tracking-wide text-slate-600 sm:grid">
        <span>Item</span>
        <span class="text-right whitespace-nowrap">Outstanding</span>
        <span class="text-right whitespace-nowrap">Delivered now</span>
      </div>
      <div class="divide-y divide-slate-100">
        @for (line of receiptForm.lines; track $index; let i = $index) {
          <div class="grid grid-cols-[minmax(0,1fr)_8rem] items-start gap-x-3 gap-y-1 py-3 sm:grid-cols-[minmax(0,1fr)_7rem_8rem]" [attr.data-line]="line().value().lineNo">
            <div class="min-w-0">
              <p class="truncate font-medium text-slate-900" [title]="line().value().description">{{ line().value().description }}</p>
              <p class="text-sm whitespace-nowrap text-slate-600 sm:hidden">{{ line().value().outstanding }} outstanding</p>
            </div>
            <p class="hidden pt-2 text-right text-sm whitespace-nowrap tabular-nums text-slate-700 sm:block">{{ line().value().outstanding }} outstanding</p>
            <div>
              <input
                type="number"
                inputmode="numeric"
                class="field-input text-right tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none"
                [attr.aria-label]="'Delivered now, ' + line().value().description"
                [attr.aria-invalid]="lineError(i) ? 'true' : null"
                [attr.aria-describedby]="lineError(i) ? 'receipt-line-' + line().value().lineNo + '-error' : null"
                [formField]="line.quantity"
              />
              @if (lineError(i); as message) {
                <p [id]="'receipt-line-' + line().value().lineNo + '-error'" class="field-error text-right">{{ message }}</p>
              }
            </div>
          </div>
        }
      </div>

      @if (tried() && noQuantity()) {
        <p role="alert" class="field-error">Enter a quantity for at least one item.</p>
      }

      <div class="max-w-sm">
        <label for="delivery-note" class="field-label">Delivery note (optional)</label>
        <input id="delivery-note" type="text" class="field-input" autocomplete="off" [formField]="receiptForm.deliveryNote" />
        @if (receiptForm.deliveryNote().touched() && receiptForm.deliveryNote().errors().length > 0) {
          <p class="field-error">{{ receiptForm.deliveryNote().errors()[0].message }}</p>
        }
      </div>

      @if (generalError(); as message) {
        <div role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">{{ message | keepRand }}</div>
      }

      <div class="flex justify-end">
        <app-busy-button type="submit" [busy]="busy()" busyLabel="Recording…" (pressed)="send()">Record delivery</app-busy-button>
      </div>
    </form>
  `,
})
export class ReceiptForm {
  readonly order = input.required<PurchaseOrderDto>();
  readonly busy = input(false);
  /** What the server said about a line, by `line.<order line number>`. */
  readonly serverErrors = input<Record<string, string[]>>({});
  readonly generalError = input<string | null>(null);
  readonly submitted = output<ReceiptInput>();

  protected readonly model = signal<Model>({ deliveryNote: '', lines: [] });
  protected readonly tried = signal(false);

  protected readonly receiptForm = form(this.model, (p) => {
    maxLength(p.deliveryNote, MAX_NOTE, { message: `Use at most ${MAX_NOTE} characters` });
    applyEach(p.lines, (line) => {
      validate(line.quantity, (ctx) => {
        const value = ctx.value();
        const outstanding = ctx.valueOf(line.outstanding);
        if (typeof value !== 'number' || !Number.isFinite(value)) {
          return { kind: 'number', message: 'Enter a whole number' };
        }
        if (!Number.isInteger(value)) {
          return { kind: 'whole', message: 'Enter a whole number' };
        }
        if (value < 0) {
          return { kind: 'min', message: 'Enter 0 or more' };
        }
        return value > outstanding ? { kind: 'max', message: `Only ${outstanding} outstanding` } : undefined;
      });
    });
  });

  protected readonly noQuantity = computed(() => !this.model().lines.some((l) => typeof l.quantity === 'number' && l.quantity > 0));

  constructor() {
    // A new order, or a receipt just recorded, starts the form over; a push that changes nothing here keeps what is typed.
    const startKey = computed(() => `${this.order().id}:${this.order().receipts.length}`);
    effect(() => {
      startKey();
      untracked(() => {
        this.model.set(emptyModel(this.order()));
        this.tried.set(false);
      });
    });
  }

  protected lineError(index: number): string | null {
    const state = this.receiptForm.lines[index].quantity();
    if (state.touched() && state.errors().length > 0) {
      return state.errors()[0].message ?? 'Check this value';
    }
    const lineNo = this.model().lines[index]?.lineNo;
    return this.serverErrors()[`line.${lineNo}`]?.[0] ?? null;
  }

  protected async send(): Promise<void> {
    this.tried.set(true);
    await submit(this.receiptForm, async () => {
      if (this.noQuantity()) {
        return;
      }
      const value = this.model();
      this.submitted.emit({
        deliveryNote: value.deliveryNote.trim() === '' ? null : value.deliveryNote.trim(),
        lines: value.lines.filter((l) => l.quantity > 0).map((l) => ({ lineNo: l.lineNo, quantity: l.quantity })),
      });
    });
  }
}
