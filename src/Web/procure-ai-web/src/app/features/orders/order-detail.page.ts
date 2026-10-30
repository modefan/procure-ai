import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { BusyButton } from '../../core/ui/busy-button';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { StatusBadge } from '../requests/status-badge';
import { OrderDetailStore, ReceiptInput } from './order-detail.store';
import { outstandingTotal, receivedText } from './order-quantities';
import { ReceiptForm } from './receipt-form';

/** The reason length the Purchasing API accepts for closing an order short (the OpenAPI document does not state it). */
const CLOSE_SHORT_REASON_MIN = 3;
const CLOSE_SHORT_REASON_MAX = 500;

/**
 * Screen 9, one order: lines (ordered, received, invoiced, paid), receipts, the linked request and invoices, and the two
 * writes: record delivery and close short (both need `orders.receive`). The route resolver passes the order it already
 * loaded (`order`); the invoices load here, and everything refreshes by itself when the server says the order changed.
 */
@Component({
  selector: 'app-order-detail-page',
  imports: [StatusBadge, BusyButton, ReceiptForm, RouterLink, DatePipe],
  providers: [OrderDetailStore],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/orders" class="text-sm font-medium text-indigo-700 hover:underline">&larr; All orders</a>

      @if (store.order(); as order) {
        <header class="mt-3 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
          <div class="min-w-0">
            <p class="text-sm font-semibold text-slate-600">
              {{ order.number }} &middot; {{ supplierName() }}
              @if (store.canSeeRequest()) {
                &middot; <a [routerLink]="['/app/requests', order.requestId]" class="text-indigo-700 hover:underline">{{ order.requestNumber }}</a>
              } @else {
                &middot; {{ order.requestNumber }}
              }
            </p>
            <h1 class="text-2xl font-bold tracking-tight break-words text-slate-900">Order {{ order.number }}</h1>
            <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
              <span class="text-xl font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ order.totalInclVatDisplay }}</span>
              <app-status-badge [status]="order.status" />
              <span class="text-sm whitespace-nowrap text-slate-700">Received {{ received() }}</span>
            </div>
          </div>
          @if (store.showCloseShort()) {
            <div data-action="close-short">
              <app-busy-button variant="secondary" [busy]="store.busy() === 'closing'" [disabled]="store.busy() !== 'idle'" busyLabel="Closing…" (pressed)="closeShort()">Close short</app-busy-button>
            </div>
          }
        </header>

        <div class="mt-4 flex flex-wrap items-center gap-x-3 gap-y-2" data-section="supplier">
          <span class="text-sm font-medium text-slate-700">Supplier:</span>
          <app-status-badge [status]="order.supplierStatus" />
          @if (order.supplierReference) {
            <span class="text-sm whitespace-nowrap text-slate-600">reference {{ order.supplierReference }}</span>
          }
          @if (order.expectedDelivery) {
            <span class="text-sm whitespace-nowrap text-slate-600">expected {{ order.expectedDelivery }}</span>
          }
        </div>
        @if (order.supplierLastError) {
          <p class="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm break-words text-amber-900">
            After {{ order.supplierAttempts }} {{ order.supplierAttempts === 1 ? 'attempt' : 'attempts' }} the supplier has not answered. Last error: {{ order.supplierLastError }}
          </p>
        }
        @if (order.cancelReason) {
          <p class="mt-2 text-sm text-slate-700">Reason: {{ order.cancelReason }}</p>
        }

        @if (store.generalError() && !store.showReceiptForm()) {
          <div role="alert" class="mt-6 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">{{ store.generalError() }}</div>
        }
        @if (store.invoicesFailed()) {
          <div role="status" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <span>The invoices could not be loaded.</span>
            <button type="button" class="btn-secondary" (click)="store.reload()">Try again</button>
          </div>
        }

        <section class="card mt-6" aria-labelledby="lines-heading" data-section="lines">
          <h2 id="lines-heading" class="mb-3 text-base font-semibold text-slate-900">Items</h2>
          <table class="w-full table-fixed text-left text-sm">
            <caption class="sr-only">Lines of {{ order.number }}</caption>
            <thead class="border-b border-slate-200 text-xs font-semibold tracking-wide text-slate-600 uppercase">
              <tr>
                <th scope="col" class="py-2 pr-2">Item</th>
                <th scope="col" class="w-10 py-2 pr-2 text-right whitespace-nowrap sm:w-20"><span class="sm:hidden" aria-hidden="true">Ord.</span><span class="sr-only sm:not-sr-only">Ordered</span></th>
                <th scope="col" class="w-10 py-2 pr-2 text-right whitespace-nowrap sm:w-20"><span class="sm:hidden" aria-hidden="true">Rec.</span><span class="sr-only sm:not-sr-only">Received</span></th>
                @if (store.canSeeInvoices()) {
                  <th scope="col" class="w-10 py-2 pr-2 text-right whitespace-nowrap sm:w-20"><span class="sm:hidden" aria-hidden="true">Inv.</span><span class="sr-only sm:not-sr-only">Invoiced</span></th>
                  <th scope="col" class="w-10 py-2 text-right whitespace-nowrap sm:w-20">Paid</th>
                }
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-100">
              @for (line of order.lines; track line.lineNo) {
                <tr [attr.data-line]="line.lineNo">
                  <td class="py-2 pr-2 break-words text-slate-800" [title]="line.description">
                    {{ line.description }}
                    @if (line.closedShort > 0) {
                      <span class="block text-xs whitespace-nowrap text-amber-800">{{ line.closedShort }} closed short</span>
                    }
                  </td>
                  <td class="py-2 pr-2 text-right tabular-nums">{{ line.ordered }}</td>
                  <td class="py-2 pr-2 text-right tabular-nums">{{ line.received }}</td>
                  @if (store.canSeeInvoices()) {
                    <td class="py-2 pr-2 text-right tabular-nums">{{ store.invoicedByLine().get(line.lineNo)?.invoiced ?? 0 }}</td>
                    <td class="py-2 text-right tabular-nums">{{ store.invoicedByLine().get(line.lineNo)?.paid ?? 0 }}</td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </section>

        @if (store.showReceiptForm()) {
          <section class="card mt-6" aria-labelledby="receipt-heading" data-section="receipt-form">
            <h2 id="receipt-heading" class="mb-1 text-base font-semibold text-slate-900">Record delivery</h2>
            <p class="mb-4 text-sm text-slate-600">{{ outstanding() }} still to be delivered. Enter what arrived.</p>
            <app-receipt-form
              [order]="order"
              [busy]="store.busy() === 'receiving'"
              [serverErrors]="store.fieldErrors()"
              [generalError]="store.generalError()"
              (submitted)="receive($event)"
            />
          </section>
        }

        <div class="mt-6 grid gap-6 lg:grid-cols-2">
          <section class="card" aria-labelledby="receipts-heading" data-section="receipts">
            <h2 id="receipts-heading" class="mb-3 text-base font-semibold text-slate-900">Deliveries received</h2>
            @if (order.receipts.length === 0) {
              <p class="text-sm text-slate-600">Nothing has been delivered yet.</p>
            } @else {
              <ul class="divide-y divide-slate-100">
                @for (receipt of order.receipts; track receipt.id) {
                  <li class="py-2 text-sm">
                    <p class="flex flex-wrap items-baseline justify-between gap-x-3">
                      <span class="font-medium text-slate-900">{{ receipt.receivedAt | date: 'd MMM y, HH:mm' }}</span>
                      @if (receipt.deliveryNote) {
                        <span class="text-slate-600">Delivery note {{ receipt.deliveryNote }}</span>
                      }
                    </p>
                    <p class="text-slate-700">{{ receiptText(order, receipt) }}</p>
                  </li>
                }
              </ul>
            }
          </section>

          @if (store.canSeeInvoices()) {
            <section class="card" aria-labelledby="invoices-heading" data-section="invoices">
              <h2 id="invoices-heading" class="mb-3 text-base font-semibold text-slate-900">Invoices</h2>
              @if (store.invoices().length === 0) {
                <p class="text-sm text-slate-600">No invoice has arrived for this order.</p>
              } @else {
                <ul class="divide-y divide-slate-100">
                  @for (invoice of store.invoices(); track invoice.id) {
                    <li class="flex flex-wrap items-center justify-between gap-x-3 gap-y-1 py-2 text-sm">
                      <a [routerLink]="['/app/invoices', invoice.id]" class="font-medium whitespace-nowrap text-indigo-700 hover:underline">{{ invoice.supplierInvoiceNumber }}</a>
                      <span class="whitespace-nowrap tabular-nums text-slate-800">{{ invoice.totalInclVatText }}</span>
                      <app-status-badge [status]="invoice.status" />
                    </li>
                  }
                </ul>
              }
            </section>
          }
        </div>
      } @else if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      } @else {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the order</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      }
    </section>
  `,
})
export class OrderDetailPage {
  /** The route's `:id`. */
  readonly id = input.required<string>();
  /** What the route resolver loaded (null when it could not). */
  readonly order = input<PurchaseOrderDto | null>(null);

  protected readonly store = inject(OrderDetailStore);
  private readonly confirm = inject(ConfirmDialog);
  private readonly reference = inject(ReferenceDataStore);
  private readonly title = inject(Title);
  protected readonly skeletonRows = [1, 2, 3, 4];

  protected readonly received = computed(() => (this.store.order() ? receivedText(this.store.order()!) : ''));
  protected readonly outstanding = computed(() => (this.store.order() ? outstandingTotal(this.store.order()!) : 0));
  protected readonly supplierName = computed(() => {
    const code = this.store.order()?.supplierId;
    return this.reference.suppliers().find((s) => s.code === code)?.name ?? code ?? '';
  });

  constructor() {
    void this.reference.loadSuppliers();
    effect(() => {
      const id = this.id();
      untracked(() => void this.store.init(id, this.order()?.id === id ? this.order() : null));
    });
    effect(() => {
      const number = this.store.order()?.number;
      if (number) {
        this.title.setTitle(`${number} - Order`);
      }
    });
  }

  protected retry(): void {
    void this.store.init(this.id(), null);
  }

  protected receive(input: ReceiptInput): void {
    void this.store.receive(input);
  }

  protected receiptText(order: PurchaseOrderDto, receipt: PurchaseOrderDto['receipts'][number]): string {
    return receipt.lines
      .map((l) => `${l.quantity} × ${order.lines.find((o) => o.lineNo === l.lineNo)?.description ?? `line ${l.lineNo}`}`)
      .join(', ');
  }

  protected async closeShort(): Promise<void> {
    const order = this.store.order();
    if (!order) {
      return;
    }
    const answer = await this.confirm.askWithInput({
      title: `Close ${order.number} short?`,
      body: `The ${this.outstanding()} units still outstanding will not be delivered, and the order is finished with what has been received. This cannot be undone.`,
      confirm: 'Close short',
      cancel: 'Keep open',
      input: { label: 'Reason', minLength: CLOSE_SHORT_REASON_MIN, maxLength: CLOSE_SHORT_REASON_MAX, required: true },
    });
    if (answer.confirmed) {
      await this.store.closeShort(answer.value);
    }
  }
}
