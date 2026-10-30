import { Component, computed, effect, inject, OnInit, signal, untracked } from '@angular/core';
import { applyEach, form, FormField, maxLength, required, submit, validate } from '@angular/forms/signals';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { InvoiceScanDto } from '../../api/assistant/models/invoice-scan-dto';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { HasUnsavedChanges } from '../../core/auth/unsaved-changes.guard';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { BusyButton } from '../../core/ui/busy-button';
import { formatRand, KeepRandPipe, RandPipe } from '../../core/ui/money.pipe';
import { VAT_RATE_PERCENT } from '../requests/request-lines';
import { InvoiceFormStore } from './invoice-form.store';
import { matchScannedLines } from './invoice-scan-match';
import { InvoiceScanStore } from './invoice-scan.store';
import { invoiceTotals } from './invoice-totals';

interface Row {
  lineNo: number;
  description: string;
  ordered: number;
  quantity: number;
  unitPrice: number;
}

interface Model {
  orderId: string;
  number: string;
  date: string;
  /** What the invoice says its total is, as typed; empty means "the total shown here". */
  statedTotal: string;
  lines: Row[];
}

const MAX_NUMBER = 64;
const MAX_QUANTITY = 100_000;

const today = () => new Date().toLocaleDateString('en-CA'); // yyyy-mm-dd, in the user's own time zone
const emptyModel = (): Model => ({ orderId: '', number: '', date: today(), statedTotal: '', lines: [] });

/** "58 000,50", "58000.50" and "58000,5" all read as 58000.5; anything else is null. */
function parseAmount(text: string): number | null {
  const cleaned = text.replace(/[\s R]/g, '').replace(',', '.');
  return /^\d+(\.\d{1,2})?$/.test(cleaned) ? Number(cleaned) : null;
}

/** `PartiallyReceived` becomes `Partially received`. */
const statusWords = (status: string) => status.replace(/([a-z])([A-Z])/g, (_, a: string, b: string) => `${a} ${b.toLowerCase()}`);

/**
 * Screen 10, manual entry: finance types in an invoice against an order they can see (`invoices.create`). One row per
 * order line, prefilled with what was received and the order price; a quantity of 0 leaves the line out. VAT is worked
 * out once, on the total (display only, the server's numbers win). The total printed on the invoice can differ from the
 * computed one: the server flags that in the match. Leaving with edits asks first (`canDeactivate` and `beforeunload`).
 */
@Component({
  selector: 'app-invoice-form-page',
  imports: [FormField, RouterLink, BusyButton, RandPipe, KeepRandPipe],
  providers: [InvoiceFormStore],
  host: { '(window:beforeunload)': 'warnBeforeUnload($event)' },
  template: `
    <section class="mx-auto max-w-4xl px-4 py-8">
      <a routerLink="/app/invoices" class="text-sm font-medium text-indigo-700 hover:underline">&larr; Invoices</a>
      <h1 class="mt-2 text-2xl font-bold tracking-tight text-slate-900">Enter an invoice</h1>
      <p class="mt-1 text-slate-600">Type in an invoice the supplier sent. It is matched against the order and the goods received as soon as it is saved.</p>

      @if (store.moreOrders()) {
        <p class="mt-4 text-sm text-slate-600" data-testid="more-orders">Only the newest orders are listed. Open the order from the orders screen if yours is missing.</p>
      }

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.loadOrders()">Try again</button>
        </div>
      }

      @if (scanGone()) {
        <p class="mt-4 rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700" data-testid="scan-gone">
          The scan is no longer available (the page was reloaded). Scan the PDF again on the invoices screen, or enter the invoice here by hand.
        </p>
      }
      @if (scan(); as s) {
        <div class="mt-4 space-y-3" data-testid="scan-prefill">
          <p class="rounded-lg border border-indigo-200 bg-indigo-50 px-4 py-3 text-sm break-words text-indigo-950" data-testid="scan-note">
            Prefilled from the scan of {{ s.invoiceNumber }} from {{ s.supplierName }} (confidence {{ s.confidence.toLowerCase() }}). Check every field before you save.
            {{ orderNote() }}
          </p>
          @if (s.warnings.length > 0) {
            <ul class="space-y-1 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900" data-testid="scan-warnings" aria-label="Warnings from the scan">
              @for (warning of s.warnings; track $index) {
                <li class="break-words">{{ warning.message | keepRand }}</li>
              }
            </ul>
          }
        </div>
      }

      <form class="mt-6 space-y-6" novalidate (submit)="$event.preventDefault()">
        @if (store.generalError(); as message) {
          <div role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm break-words text-red-900">{{ message | keepRand }}</div>
        }

        <div class="card space-y-5">
          <div>
            <label for="invoice-order" class="field-label">Order</label>
            <select
              id="invoice-order"
              class="field-input"
              [formField]="invoiceForm.orderId"
              [attr.aria-invalid]="errorOf(invoiceForm.orderId(), 'orderId') ? 'true' : null"
              [attr.aria-describedby]="errorOf(invoiceForm.orderId(), 'orderId') ? 'invoice-order-error' : null"
            >
              <option value="">{{ store.isLoading() ? 'Loading orders…' : 'Choose an order' }}</option>
              @for (order of store.orders(); track order.id) {
                <option [value]="order.id">{{ orderLabel(order) }}</option>
              }
            </select>
            @if (errorOf(invoiceForm.orderId(), 'orderId'); as message) {
              <p id="invoice-order-error" class="field-error">{{ message }}</p>
            }
          </div>
          <div class="grid gap-5 sm:grid-cols-2">
            <div>
              <label for="invoice-number" class="field-label">Supplier invoice number</label>
              <input
                id="invoice-number"
                type="text"
                class="field-input"
                autocomplete="off"
                [formField]="invoiceForm.number"
                [attr.aria-invalid]="errorOf(invoiceForm.number(), 'supplierInvoiceNumber') ? 'true' : null"
                [attr.aria-describedby]="errorOf(invoiceForm.number(), 'supplierInvoiceNumber') ? 'invoice-number-error' : null"
              />
              @if (errorOf(invoiceForm.number(), 'supplierInvoiceNumber'); as message) {
                <p id="invoice-number-error" class="field-error">{{ message }}</p>
              }
            </div>
            <div>
              <label for="invoice-date" class="field-label">Invoice date</label>
              <input
                id="invoice-date"
                type="date"
                class="field-input"
                [formField]="invoiceForm.date"
                [attr.aria-invalid]="errorOf(invoiceForm.date(), 'invoiceDate') ? 'true' : null"
                [attr.aria-describedby]="errorOf(invoiceForm.date(), 'invoiceDate') ? 'invoice-date-error' : null"
              />
              @if (errorOf(invoiceForm.date(), 'invoiceDate'); as message) {
                <p id="invoice-date-error" class="field-error">{{ message }}</p>
              }
            </div>
          </div>
        </div>

        <div class="card">
          <h2 class="text-base font-semibold text-slate-900">Items</h2>
          @if (model().orderId === '') {
            <p class="mt-3 text-sm text-slate-600">Choose an order to enter what the supplier invoiced for it.</p>
          } @else {
            <div class="mt-3 hidden grid-cols-[minmax(0,1fr)_6rem_7rem_9rem_9rem] gap-3 border-b border-slate-200 pb-2 text-xs font-semibold uppercase tracking-wide text-slate-600 md:grid">
              <span>Item</span>
              <span class="text-right whitespace-nowrap">Ordered</span>
              <span class="text-right whitespace-nowrap">Qty invoiced</span>
              <span class="text-right whitespace-nowrap">Unit price excl. VAT</span>
              <span class="text-right whitespace-nowrap">Line total</span>
            </div>
            <ul class="divide-y divide-slate-100">
              @for (line of invoiceForm.lines; track $index; let i = $index) {
                <li
                  class="grid grid-cols-2 items-start gap-x-3 gap-y-2 py-3 md:grid-cols-[minmax(0,1fr)_6rem_7rem_9rem_9rem]"
                  [attr.data-line]="line().value().lineNo"
                >
                  <div class="col-span-2 min-w-0 md:col-span-1">
                    <p class="font-medium break-words text-slate-900">{{ line().value().description }}</p>
                    <p class="text-sm whitespace-nowrap text-slate-600 md:hidden">Ordered {{ line().value().ordered }}</p>
                  </div>
                  <p class="hidden pt-2 text-right text-sm tabular-nums text-slate-700 md:block">{{ line().value().ordered }}</p>
                  <div>
                    <label class="mb-1 block text-xs text-slate-600 md:hidden" [for]="'qty-' + i">Qty invoiced</label>
                    <input
                      [id]="'qty-' + i"
                      type="number"
                      inputmode="numeric"
                      data-field="quantity"
                      class="field-input text-right tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none"
                      [attr.aria-label]="'Quantity invoiced, ' + line().value().description"
                      [attr.aria-invalid]="lineError(i, 'quantity') ? 'true' : null"
                      [formField]="line.quantity"
                    />
                    @if (lineError(i, 'quantity'); as message) {
                      <p class="field-error text-right">{{ message }}</p>
                    }
                  </div>
                  <div>
                    <label class="mb-1 block text-xs text-slate-600 md:hidden" [for]="'price-' + i">Unit price excl. VAT</label>
                    <input
                      [id]="'price-' + i"
                      type="number"
                      inputmode="decimal"
                      step="0.01"
                      data-field="price"
                      class="field-input text-right tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none"
                      [attr.aria-label]="'Unit price excl. VAT, ' + line().value().description"
                      [attr.aria-invalid]="lineError(i, 'unitPrice') ? 'true' : null"
                      [formField]="line.unitPrice"
                    />
                    @if (lineError(i, 'unitPrice'); as message) {
                      <p class="field-error text-right">{{ message }}</p>
                    }
                  </div>
                  <p class="col-span-2 flex items-baseline justify-between gap-2 text-sm font-medium whitespace-nowrap tabular-nums text-slate-900 md:col-span-1 md:block md:pt-2 md:text-right">
                    <span class="text-xs font-normal text-slate-500 md:hidden">Line total</span>
                    <span>{{ (lineTotal(i) | rand) || '–' }}</span>
                  </p>
                </li>
              }
            </ul>
            @if (tried() && noQuantity()) {
              <p role="alert" class="field-error">Enter a quantity for at least one item.</p>
            }
            <dl class="mt-5 ml-auto max-w-sm space-y-1 border-t border-slate-200 pt-4 text-sm" data-testid="totals">
              <div class="flex justify-between gap-4"><dt class="text-slate-600">Total excl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().exclVat | rand }}</dd></div>
              <div class="flex justify-between gap-4"><dt class="text-slate-600">VAT {{ vatRate }} %</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().vat | rand }}</dd></div>
              <div class="flex justify-between gap-4 text-base font-semibold text-slate-900"><dt>Total incl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().inclVat | rand }}</dd></div>
            </dl>
            <div class="mt-5 max-w-xs">
              <label for="invoice-stated-total" class="field-label">Total printed on the invoice, incl. VAT (optional)</label>
              <input
                id="invoice-stated-total"
                type="text"
                inputmode="decimal"
                class="field-input text-right tabular-nums"
                autocomplete="off"
                [placeholder]="totalsPlaceholder()"
                [formField]="invoiceForm.statedTotal"
                [attr.aria-invalid]="errorOf(invoiceForm.statedTotal(), 'statedTotal') ? 'true' : null"
                [attr.aria-describedby]="'invoice-stated-total-hint' + (errorOf(invoiceForm.statedTotal(), 'statedTotal') ? ' invoice-stated-total-error' : '')"
              />
              <p id="invoice-stated-total-hint" class="mt-1 text-xs text-slate-600">Leave empty to use the total above. If the invoice shows another total, type it: the match will flag the difference.</p>
              @if (errorOf(invoiceForm.statedTotal(), 'statedTotal'); as message) {
                <p id="invoice-stated-total-error" class="field-error">{{ message }}</p>
              }
            </div>
          }
        </div>

        <div class="flex flex-wrap items-center justify-end gap-3">
          <a routerLink="/app/invoices" class="btn-ghost">Cancel</a>
          <div data-testid="save-invoice">
            <app-busy-button type="submit" [busy]="store.busy()" busyLabel="Saving…" (pressed)="save()">Save invoice</app-busy-button>
          </div>
        </div>
      </form>
    </section>
  `,
})
export class InvoiceFormPage implements OnInit, HasUnsavedChanges {
  protected readonly store = inject(InvoiceFormStore);
  private readonly reference = inject(ReferenceDataStore);
  private readonly router = inject(Router);
  protected readonly vatRate = VAT_RATE_PERCENT;
  protected readonly model = signal<Model>(emptyModel());
  protected readonly tried = signal(false);
  private readonly scans = inject(InvoiceScanStore);
  private readonly route = inject(ActivatedRoute);
  /** The scan this form was opened from (`?scan=<id>`), taken from the scan store; null for a manual entry. */
  protected readonly scan = signal<InvoiceScanDto | null>(null);
  /** The URL names a scan the store no longer holds (page reload): the form starts empty. */
  protected readonly scanGone = signal(false);

  protected readonly invoiceForm = form(this.model, (p) => {
    required(p.orderId, { message: 'Choose an order' });
    required(p.number, { message: 'Enter the supplier invoice number' });
    validate(p.number, ({ value }) => (value() !== '' && value().trim() === '' ? { kind: 'required', message: 'Enter the supplier invoice number' } : undefined));
    maxLength(p.number, MAX_NUMBER, { message: `Use at most ${MAX_NUMBER} characters` });
    required(p.date, { message: 'Enter the invoice date' });
    validate(p.statedTotal, ({ value }) => (value().trim() === '' || parseAmount(value()) !== null ? undefined : { kind: 'amount', message: 'Enter an amount like 58 000,50' }));
    applyEach(p.lines, (line) => {
      validate(line.quantity, ({ value }) => {
        const v = value();
        if (typeof v !== 'number' || !Number.isInteger(v)) {
          return { kind: 'whole', message: 'Enter a whole number' };
        }
        if (v < 0) {
          return { kind: 'min', message: 'Enter 0 or more' };
        }
        return v > MAX_QUANTITY ? { kind: 'max', message: `Enter at most ${MAX_QUANTITY.toLocaleString('en-ZA')}` } : undefined;
      });
      validate(line.unitPrice, ({ value }) => {
        const v = value();
        return typeof v !== 'number' || !Number.isFinite(v) || v < 0 ? { kind: 'price', message: 'Enter a price of 0 or more' } : undefined;
      });
    });
  });

  protected readonly noQuantity = computed(() => !this.model().lines.some((l) => typeof l.quantity === 'number' && l.quantity > 0));
  protected readonly totals = computed(() => invoiceTotals(this.model().lines.map((l) => ({ quantity: l.quantity, unitPriceExclVat: l.unitPrice }))));
  protected readonly totalsPlaceholder = computed(() => formatRand(this.totals().inclVat));

  /** The model as last started or saved; null until the form has its starting values. */
  private readonly baseline = signal<string | null>(null);
  private readonly dirty = computed(() => this.baseline() !== null && JSON.stringify(this.model()) !== this.baseline());
  private previousOrder = '';

  constructor() {
    // Typing clears the server's complaints about the old values.
    effect(() => {
      this.model();
      untracked(() => this.store.clearErrors());
    });
    // Another order means other lines: start them from what was received and the order price.
    effect(() => {
      const orderId = this.model().orderId;
      untracked(() => {
        if (orderId === this.previousOrder) {
          return;
        }
        this.previousOrder = orderId;
        const order = this.store.orders().find((o) => o.id === orderId);
        this.model.update((m) => ({
          ...m,
          // The total printed on another invoice says nothing about this order; only a scan's own total stays.
          statedTotal: this.scanTotal(),
          lines: this.rowsFor(order),
        }));
      });
    });
  }

  async ngOnInit(): Promise<void> {
    void this.reference.loadSuppliers();
    this.baseline.set(JSON.stringify(this.model()));
    const id = this.route.snapshot.queryParamMap.get('scan');
    const held = this.scans.current();
    if (id && held?.id === id) {
      this.scan.set(held);
    } else if (id) {
      this.scanGone.set(true);
    }
    await this.store.loadOrders();
    const s = this.scan();
    if (s) {
      this.applyScan(s);
    }
  }

  /** The total printed on the invoice, as the form's field wants it; empty without a scan. */
  private scanTotal(): string {
    const s = this.scan();
    return s ? s.totalInclVat.toFixed(2) : '';
  }

  /**
   * One row per order line. Without a scan: what was received (else ordered) at the order price. With a scan: the scanned line
   * that matches the order line by description gives quantity and price; an order line the invoice does not mention gets 0.
   */
  private rowsFor(order: PurchaseOrderDto | undefined): Row[] {
    const lines = order?.lines ?? [];
    const s = this.scan();
    const matches = s ? matchScannedLines(lines.map((l) => l.description), s.lines) : [];
    return lines.map((l, i) => {
      const hit = matches[i] ?? null;
      return {
        lineNo: l.lineNo,
        description: l.description,
        ordered: l.ordered,
        quantity: s ? (hit?.quantity ?? 0) : l.received > 0 ? l.received : l.ordered,
        unitPrice: hit ? hit.unitPriceExclVat : l.unitPriceExclVat,
      };
    });
  }

  /** Puts the scan into the form: number, date, the order it names (when the caller sees it), the printed total, the lines. */
  private applyScan(s: InvoiceScanDto): void {
    const wanted = s.orderNumber?.trim().toLowerCase();
    const order = wanted ? this.store.orders().find((o) => o.number.toLowerCase() === wanted) : undefined;
    this.previousOrder = order?.id ?? '';
    this.model.update((m) => ({
      orderId: order?.id ?? '',
      number: s.invoiceNumber,
      date: s.invoiceDate ?? m.date,
      statedTotal: this.scanTotal(),
      lines: this.rowsFor(order),
    }));
    // The prefilled form is the starting point: only the user's own edits count as unsaved.
    this.baseline.set(JSON.stringify(this.model()));
  }

  /** Why the order is (not) chosen, and how many scanned lines found no line to go to. */
  protected readonly orderNote = computed(() => {
    const s = this.scan();
    if (!s) {
      return '';
    }
    const order = this.store.orders().find((o) => o.id === this.model().orderId);
    if (!order) {
      return s.orderNumber
        ? `The invoice names ${s.orderNumber}, which is not among the orders you can see. Choose the order.`
        : 'The invoice names no order. Choose the order.';
    }
    const matched = matchScannedLines(order.lines.map((l) => l.description), s.lines).filter((l) => l !== null).length;
    const left = s.lines.length - matched;
    return left > 0 ? `${left} scanned ${left === 1 ? 'line matches' : 'lines match'} no line of ${order.number} and ${left === 1 ? 'is' : 'are'} left out.` : '';
  });

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  protected warnBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) {
      event.preventDefault();
    }
  }

  protected orderLabel(order: PurchaseOrderDto): string {
    const supplier = this.reference.suppliers().find((s) => s.code === order.supplierId)?.name ?? order.supplierId;
    return `${order.number} · ${supplier} · ${statusWords(order.status)} · ${order.totalInclVatDisplay}`;
  }

  protected lineTotal(index: number): number | null {
    const line = this.model().lines[index];
    return line && Number.isFinite(line.quantity) && Number.isFinite(line.unitPrice) ? invoiceTotals([{ quantity: line.quantity, unitPriceExclVat: line.unitPrice }]).exclVat : null;
  }

  /** The message under a field: its own validation (once touched), else what the server said about it. */
  protected errorOf(state: { touched(): boolean; errors(): readonly { message?: string }[] }, serverKey: string): string | null {
    if ((state.touched() || this.tried()) && state.errors().length > 0) {
      return state.errors()[0].message ?? 'Check this value';
    }
    return this.store.fieldErrors()[serverKey]?.[0] ?? null;
  }

  protected lineError(index: number, prop: 'quantity' | 'unitPrice'): string | null {
    const row = this.model().lines[index];
    const state = prop === 'quantity' ? this.invoiceForm.lines[index].quantity() : this.invoiceForm.lines[index].unitPrice();
    if (state.touched() && state.errors().length > 0) {
      return state.errors()[0].message ?? 'Check this value';
    }
    return row ? (this.store.fieldErrors()[`line.${row.lineNo}.${prop}`]?.[0] ?? null) : null;
  }

  protected async save(): Promise<void> {
    this.tried.set(true);
    await submit(this.invoiceForm, async () => {
      if (this.noQuantity()) {
        return;
      }
      const m = this.model();
      const stated = m.statedTotal.trim() === '' ? this.totals().inclVat : (parseAmount(m.statedTotal) ?? this.totals().inclVat);
      const created = await this.store.create({
        orderId: m.orderId,
        supplierInvoiceNumber: m.number.trim(),
        invoiceDate: m.date,
        statedTotalInclVat: stated,
        ...(this.scan() ? { scanId: this.scan()!.id } : {}),
        lines: m.lines.filter((l) => l.quantity > 0).map((l) => ({ lineNo: l.lineNo, quantity: l.quantity, unitPriceExclVat: l.unitPrice })),
      });
      if (created) {
        if (this.scan()) {
          this.scans.clear();
        }
        this.baseline.set(JSON.stringify(this.model()));
        await this.router.navigate(['/app/invoices', created.id]);
      }
    });
  }
}
