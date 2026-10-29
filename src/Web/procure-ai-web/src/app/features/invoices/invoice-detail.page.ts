import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { form, FormField, submit, validate } from '@angular/forms/signals';
import { Title } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PayLineBody } from '../../api/invoicing/models/pay-line-body';
import { AuthStore } from '../../core/auth/auth.store';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { BusyButton } from '../../core/ui/busy-button';
import { StatusBadge } from '../requests/status-badge';
import { VAT_RATE_PERCENT } from '../requests/request-lines';
import { InvoiceDetailStore } from './invoice-detail.store';
import { isLineProblem, lineResult, matchStatus, payAllLabel, paymentStatus } from './invoice-match';
import { PayLinesForm } from './pay-lines-form';

const MIN_REASON = 3;
const MAX_REASON = 500;

/**
 * Screen 11, one invoice: the three-way match per line (ordered, received, invoiced, result), VAT and totals, the
 * payments with their ERP export, and finance's actions (`invoices.release-payment`): pay everything payable, pay chosen
 * quantities, hold with a reason, release the hold. The route resolver passes the invoice it already loaded.
 */
@Component({
  selector: 'app-invoice-detail-page',
  imports: [StatusBadge, BusyButton, PayLinesForm, RouterLink, DatePipe, FormField],
  providers: [InvoiceDetailStore],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/invoices" class="text-sm font-medium text-indigo-700 hover:underline">&larr; All invoices</a>

      @if (store.invoice(); as invoice) {
        <header class="mt-3 flex flex-wrap items-start justify-between gap-x-6 gap-y-4">
          <div class="min-w-0">
            <p class="text-sm font-semibold text-slate-600">
              {{ supplierName() }} &middot;
              @if (store.canSeeOrder() && invoice.orderNumber) {
                <a [routerLink]="['/app/orders', invoice.orderId]" class="text-indigo-700 hover:underline">{{ invoice.orderNumber }}</a>
              } @else {
                {{ invoice.orderNumber ?? 'Order not known yet' }}
              }
              &middot; {{ sourceText(invoice) }}
            </p>
            <h1 class="text-2xl font-bold tracking-tight break-words text-slate-900">{{ invoice.supplierInvoiceNumber }}</h1>
            <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
              <span class="text-xl font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ invoice.totalInclVatText }}</span>
              <app-status-badge [status]="match()" />
              <app-status-badge [status]="payment()" />
              <span class="text-sm whitespace-nowrap text-slate-600">Invoice date {{ invoice.invoiceDate | date: 'd MMM y' }}</span>
            </div>
          </div>
        </header>

        @if (invoice.onHold) {
          <div class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900" data-section="hold">
            <span class="font-semibold">Held by finance.</span> {{ invoice.holdReason }}
          </div>
        }
        @if (invoice.mismatchReasons.length > 0) {
          <div class="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-900" data-section="reasons">
            <p class="font-semibold">Why it does not match</p>
            <ul class="mt-1 list-disc space-y-0.5 pl-5">
              @for (reason of invoice.mismatchReasons; track reason) {
                <li class="break-words">{{ reason }}</li>
              }
            </ul>
          </div>
        }

        <section class="card mt-6" aria-labelledby="lines-heading" data-section="lines">
          <h2 id="lines-heading" class="mb-3 text-base font-semibold text-slate-900">Three-way match</h2>
          <div class="hidden lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Lines of {{ invoice.supplierInvoiceNumber }}: ordered, received and invoiced</caption>
              <colgroup>
                <col />
                <col class="w-40" />
                <col class="w-24" />
                <col class="w-40" />
                <col class="w-20" />
                <col class="w-64" />
              </colgroup>
              <thead class="border-b border-slate-200 text-xs font-semibold tracking-wide text-slate-600 uppercase">
                <tr>
                  <th scope="col" class="py-2 pr-3">Item</th>
                  <th scope="col" class="py-2 pr-3 whitespace-nowrap">Ordered (PO)</th>
                  <th scope="col" class="py-2 pr-6 text-right whitespace-nowrap">Received</th>
                  <th scope="col" class="py-2 pr-3 whitespace-nowrap">Invoiced</th>
                  <th scope="col" class="py-2 pr-3 text-right whitespace-nowrap">Paid</th>
                  <th scope="col" class="py-2">Result</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (line of invoice.lines; track line.lineNo) {
                  <tr class="align-top" [attr.data-line]="line.lineNo">
                    <td class="py-2 pr-3 break-words text-slate-800">{{ line.description }}</td>
                    <td class="py-2 pr-3 whitespace-nowrap tabular-nums">{{ orderedText(line) }}</td>
                    <td class="py-2 pr-6 text-right tabular-nums">{{ line.received ?? '–' }}</td>
                    <td class="py-2 pr-3 whitespace-nowrap tabular-nums">{{ line.quantity }} &times; {{ line.unitPriceText }}</td>
                    <td class="py-2 pr-3 text-right tabular-nums">{{ line.paid }}</td>
                    <td class="py-2 font-medium" [class]="problem(line) ? 'text-rose-800' : 'text-emerald-800'">{{ result(line) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <ul class="divide-y divide-slate-100 lg:hidden" aria-label="Invoice lines">
            @for (line of invoice.lines; track line.lineNo) {
              <li class="py-3 text-sm" [attr.data-line]="line.lineNo">
                <p class="font-medium break-words text-slate-900">{{ line.description }}</p>
                <dl class="mt-2 grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1">
                  <dt class="text-slate-600">Ordered (PO)</dt>
                  <dd class="text-right whitespace-nowrap tabular-nums">{{ orderedText(line) }}</dd>
                  <dt class="text-slate-600">Received</dt>
                  <dd class="text-right tabular-nums">{{ line.received ?? '–' }}</dd>
                  <dt class="text-slate-600">Invoiced</dt>
                  <dd class="text-right whitespace-nowrap tabular-nums">{{ line.quantity }} &times; {{ line.unitPriceText }}</dd>
                  <dt class="text-slate-600">Paid</dt>
                  <dd class="text-right tabular-nums">{{ line.paid }}</dd>
                </dl>
                <p class="mt-2 font-medium" [class]="problem(line) ? 'text-rose-800' : 'text-emerald-800'">{{ result(line) }}</p>
              </li>
            }
          </ul>

          <dl class="mt-4 ml-auto max-w-sm space-y-1 border-t border-slate-200 pt-4 text-sm" data-section="totals">
            <div class="flex justify-between gap-4"><dt class="text-slate-600">Total excl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ invoice.totalExclVatText }}</dd></div>
            <div class="flex justify-between gap-4"><dt class="text-slate-600">VAT {{ vatRate }} %</dt><dd class="whitespace-nowrap tabular-nums">{{ invoice.vatText }}</dd></div>
            <div class="flex justify-between gap-4 text-base font-semibold text-slate-900"><dt>Total incl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ invoice.totalInclVatText }}</dd></div>
          </dl>
        </section>

        @if (store.mayRelease()) {
          <section class="card mt-6" aria-labelledby="actions-heading" data-section="actions">
            <h2 id="actions-heading" class="mb-3 text-base font-semibold text-slate-900">Payment</h2>
            @if (store.generalError(); as message) {
              <div role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm break-words text-red-900">{{ message }}</div>
            }
            @if (invoice.onHold) {
              <p class="mb-3 text-sm text-slate-700">Nothing is paid while the invoice is on hold. Releasing it matches the invoice again.</p>
              <div data-action="release-hold">
                <app-busy-button variant="secondary" [busy]="store.busy() === 'releasing'" [disabled]="store.busy() !== 'idle'" busyLabel="Releasing…" (pressed)="releaseHold()">Release hold</app-busy-button>
              </div>
            } @else {
              <div class="flex flex-wrap items-center gap-3">
                @if (store.canPay()) {
                  <div data-action="pay-all">
                    <app-busy-button [busy]="store.busy() === 'paying' && !choosing()" [disabled]="store.busy() !== 'idle'" busyLabel="Paying…" (pressed)="payAll()">{{ payLabel() }}</app-busy-button>
                  </div>
                  <button type="button" class="btn-secondary whitespace-nowrap" data-action="choose-quantities" [attr.aria-expanded]="choosing()" [disabled]="store.busy() !== 'idle'" (click)="choosing.set(!choosing())">
                    {{ choosing() ? 'Hide chosen quantities' : 'Choose quantities' }}
                  </button>
                } @else {
                  <p class="text-sm text-slate-700">{{ nothingPayableText() }}</p>
                }
                @if (!holding() && invoice.status !== 'Paid') {
                  <button type="button" class="btn-secondary whitespace-nowrap" data-action="hold" [disabled]="store.busy() !== 'idle'" (click)="startHold()">Hold invoice</button>
                }
              </div>

              @if (choosing() && store.canPay()) {
                <div class="mt-4 border-t border-slate-200 pt-4">
                  <app-pay-lines-form [invoice]="invoice" [busy]="store.busy() === 'paying' && choosing()" [disabled]="store.busy() !== 'idle'" (submitted)="payChosen($event)" />
                </div>
              }

              @if (holding()) {
                <form class="mt-4 max-w-xl space-y-3 border-t border-slate-200 pt-4" novalidate (submit)="$event.preventDefault()">
                  <div>
                    <label for="hold-reason" class="field-label">Why is it held?</label>
                    <textarea id="hold-reason" rows="2" class="field-input" [formField]="holdForm.reason" [attr.aria-invalid]="holdError() ? 'true' : null" [attr.aria-describedby]="holdError() ? 'hold-reason-error' : null"></textarea>
                    @if (holdError(); as message) {
                      <p id="hold-reason-error" class="field-error">{{ message }}</p>
                    }
                  </div>
                  <div class="flex flex-wrap justify-end gap-3">
                    <button type="button" class="btn-ghost" [disabled]="store.busy() !== 'idle'" (click)="holding.set(false)">Cancel</button>
                    <div data-action="confirm-hold">
                      <app-busy-button [busy]="store.busy() === 'holding'" [disabled]="store.busy() !== 'idle'" busyLabel="Holding…" (pressed)="confirmHold()">Hold invoice</app-busy-button>
                    </div>
                  </div>
                </form>
              }
            }
          </section>
        }

        <section class="card mt-6" aria-labelledby="payments-heading" data-section="payments">
          <h2 id="payments-heading" class="mb-3 text-base font-semibold text-slate-900">Payments</h2>
          @if (invoice.payments.length === 0) {
            <p class="text-sm text-slate-600">Nothing has been paid yet.</p>
          } @else {
            <ul class="divide-y divide-slate-100">
              @for (payment of invoice.payments; track payment.id) {
                <li class="py-3 text-sm">
                  <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
                    <span class="font-medium whitespace-nowrap text-slate-900">{{ payment.number }}</span>
                    <span class="font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ payment.totalInclVatText }}</span>
                    <app-status-badge [status]="payment.exportStatus === 'Exported' ? 'Exported' : 'Export pending'" />
                  </div>
                  <p class="mt-1 text-slate-600">
                    {{ payment.releasedAt | date: 'd MMM y, HH:mm' }}{{ releasedBy(payment) }}
                    @if (payment.erpReference) {
                      &middot; ERP reference <span class="whitespace-nowrap">{{ payment.erpReference }}</span>
                    }
                  </p>
                  <p class="text-slate-700">{{ paymentLines(payment) }}</p>
                  @if (payment.exportStatus !== 'Exported' && payment.lastExportError) {
                    <p class="mt-1 break-words text-amber-800">Export attempt {{ payment.exportAttempts }} failed: {{ payment.lastExportError }}</p>
                  }
                </li>
              }
            </ul>
          }
        </section>
      } @else if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      } @else {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the invoice</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      }
    </section>
  `,
})
export class InvoiceDetailPage {
  /** The route's `:id`. */
  readonly id = input.required<string>();
  /** What the route resolver loaded (null when it could not). */
  readonly invoice = input<InvoiceDto | null>(null);

  protected readonly store = inject(InvoiceDetailStore);
  private readonly reference = inject(ReferenceDataStore);
  private readonly auth = inject(AuthStore);
  private readonly title = inject(Title);
  protected readonly skeletonRows = [1, 2, 3, 4];
  protected readonly vatRate = VAT_RATE_PERCENT;

  protected readonly choosing = signal(false);
  protected readonly holding = signal(false);
  private readonly holdModel = signal({ reason: '' });
  private readonly holdTried = signal(false);
  protected readonly holdForm = form(this.holdModel, (p) => {
    validate(p.reason, ({ value }) => {
      const text = value().trim();
      if (text.length < MIN_REASON) {
        return { kind: 'required', message: `Enter a reason (at least ${MIN_REASON} characters)` };
      }
      return text.length > MAX_REASON ? { kind: 'maxLength', message: `Use at most ${MAX_REASON} characters` } : undefined;
    });
  });
  protected readonly holdError = computed(() => {
    const state = this.holdForm.reason();
    return (state.touched() || this.holdTried()) && state.errors().length > 0 ? (state.errors()[0].message ?? 'Check this value') : null;
  });

  protected readonly match = computed(() => (this.store.invoice() ? matchStatus(this.store.invoice()!) : ''));
  protected readonly payment = computed(() => (this.store.invoice() ? paymentStatus(this.store.invoice()!) : ''));
  protected readonly payLabel = computed(() => (this.store.invoice() ? payAllLabel(this.store.invoice()!) : ''));
  protected readonly supplierName = computed(() => {
    const code = this.store.invoice()?.supplierId;
    return this.reference.suppliers().find((s) => s.code === code)?.name ?? code ?? '';
  });
  protected readonly nothingPayableText = computed(() => {
    const invoice = this.store.invoice();
    if (invoice && invoice.status === 'Paid') {
      return 'This invoice is paid in full.';
    }
    return invoice?.status === 'AwaitingOrder' ? 'Nothing can be paid yet: the order is not known.' : 'Nothing can be paid yet: nothing received is waiting for payment.';
  });

  constructor() {
    void this.reference.loadSuppliers();
    effect(() => {
      const id = this.id();
      untracked(() => void this.store.init(id, this.invoice()?.id === id ? this.invoice() : null));
    });
    effect(() => {
      const number = this.store.invoice()?.supplierInvoiceNumber;
      if (number) {
        this.title.setTitle(`${number} - Invoice`);
      }
    });
  }

  protected result = lineResult;
  protected problem = isLineProblem;

  protected orderedText(line: InvoiceDto['lines'][number]): string {
    return line.ordered === null || line.orderUnitPriceText === null ? '–' : `${line.ordered} × ${line.orderUnitPriceText}`;
  }

  protected sourceText(invoice: InvoiceDto): string {
    return { Webhook: 'From the supplier', Manual: 'Entered by finance', Scan: 'Scanned' }[invoice.source];
  }

  protected paymentLines(payment: InvoiceDto['payments'][number]): string {
    const invoice = this.store.invoice();
    return payment.lines.map((l) => `${l.quantity} × ${invoice?.lines.find((i) => i.lineNo === l.lineNo)?.description ?? `line ${l.lineNo}`}`).join(', ');
  }

  /**
   * " · released by you". The payment holds the releaser's user ID, not a name (names come from Access, which the API
   * does not call for this): the caller's own payments say "you", an ID is never shown, a readable name is.
   */
  protected releasedBy(payment: InvoiceDto['payments'][number]): string {
    if (payment.releasedBy === this.auth.user()?.userId) {
      return ' · released by you';
    }
    return /^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(payment.releasedBy) ? '' : ` · released by ${payment.releasedBy}`;
  }

  protected retry(): void {
    void this.store.init(this.id(), null);
  }

  protected async payAll(): Promise<void> {
    await this.store.pay(null);
  }

  protected async payChosen(lines: PayLineBody[]): Promise<void> {
    if (await this.store.pay(lines)) {
      this.choosing.set(false);
    }
  }

  protected startHold(): void {
    this.holdModel.set({ reason: '' });
    this.holdTried.set(false);
    this.choosing.set(false);
    this.holding.set(true);
  }

  protected async confirmHold(): Promise<void> {
    this.holdTried.set(true);
    await submit(this.holdForm, async () => {
      if (await this.store.hold(this.holdModel().reason)) {
        this.holding.set(false);
      }
    });
  }

  protected async releaseHold(): Promise<void> {
    await this.store.releaseHold();
  }
}
