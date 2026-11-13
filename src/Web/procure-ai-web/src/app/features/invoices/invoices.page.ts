import { Component, computed, inject, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { AuthStore } from '../../core/auth/auth.store';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { Pager } from '../../core/ui/pager';
import { StatusBadge } from '../requests/status-badge';
import { InvoiceScanPanel } from './invoice-scan';
import { matchStatus, paymentStatus } from './invoice-match';
import { InvoicesStore, InvoiceStatusFilter } from './invoices.store';

/**
 * Screen 10: supplier invoices with their match and payment state (needs `invoices.read`). A table from the lg
 * breakpoint up; below it, one card per invoice. The supplier name comes from the supplier list that is cached for the
 * session (the invoice holds only the supplier code). Manual entry (`invoices.create`) is one click away.
 */
@Component({
  selector: 'app-invoices-page',
  imports: [StatusBadge, Pager, RouterLink, InvoiceScanPanel],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">Invoices</h1>
          <p class="mt-1 text-slate-600">Amounts include VAT.</p>
        </div>
        <div class="flex flex-wrap items-end gap-3">
          <div>
            <label for="invoice-status" class="field-label">Status</label>
            <select id="invoice-status" class="field-input w-auto" [value]="store.status()" (change)="setStatus($event)">
              @for (option of statusOptions; track option.value) {
                <option [value]="option.value">{{ option.label }}</option>
              }
            </select>
          </div>
          @if (mayCreate()) {
            <a routerLink="/app/invoices/new" class="btn-primary whitespace-nowrap">Enter an invoice</a>
          }
        </div>
      </div>

      @if (mayCreate()) {
        <div class="mt-6 max-w-2xl">
          <app-invoice-scan-panel />
        </div>
      }

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load(store.page())">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading invoices</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          @if (store.status() !== 'all') {
            <p class="text-lg font-semibold text-slate-900">No invoices match this status</p>
            <button type="button" class="btn-secondary mt-4" (click)="store.setStatus('all')">Show all invoices</button>
          } @else {
            <p class="text-lg font-semibold text-slate-900">No invoices yet</p>
            <p class="mt-1 text-slate-600">Invoices arrive from suppliers, or are entered by finance, and appear here.</p>
          }
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [attr.aria-busy]="store.isLoading()" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Invoices, page {{ store.page() }} of {{ store.pageCount() }}</caption>
              <colgroup>
                <col class="w-64" />
                <col />
                <col class="w-28" />
                <col class="w-36" />
                <col class="w-36" />
                <col class="w-36" />
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Invoice</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Supplier</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Order</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Amount</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Match</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Payment</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (invoice of store.entities(); track invoice.id) {
                  <tr class="hover:bg-slate-50" [attr.data-invoice]="invoice.id">
                    <td class="px-4 py-3 font-medium break-all">
                      <a [routerLink]="['/app/invoices', invoice.id]" class="text-indigo-700 hover:underline" [title]="invoice.supplierInvoiceNumber">{{ invoice.supplierInvoiceNumber }}</a>
                    </td>
                    <td class="truncate px-4 py-3 text-slate-800" [title]="supplierName(invoice)">{{ supplierName(invoice) }}</td>
                    <td class="px-4 py-3 whitespace-nowrap text-slate-800">{{ invoice.orderNumber ?? 'Not known yet' }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ invoice.totalInclVatText }}</td>
                    <td class="px-4 py-3"><app-status-badge [status]="match(invoice)" /></td>
                    <td class="px-4 py-3"><app-status-badge [status]="payment(invoice)" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 lg:hidden" aria-label="Invoices">
            @for (invoice of store.entities(); track invoice.id) {
              <li class="card p-4">
                <div class="flex items-center justify-between gap-3">
                  <a [routerLink]="['/app/invoices', invoice.id]" class="min-w-0 break-all font-semibold text-indigo-700 hover:underline">{{ invoice.supplierInvoiceNumber }}</a>
                  <span class="font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ invoice.totalInclVatText }}</span>
                </div>
                <p class="mt-1 truncate text-slate-800" [title]="supplierName(invoice)">{{ supplierName(invoice) }}</p>
                <p class="text-sm whitespace-nowrap text-slate-600">{{ invoice.orderNumber ?? 'Order not known yet' }}</p>
                <div class="mt-2 flex flex-wrap items-center gap-2">
                  <app-status-badge [status]="match(invoice)" />
                  <app-status-badge [status]="payment(invoice)" />
                </div>
              </li>
            }
          </ul>
        </div>

        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="invoice" nounPlural="invoices" (pageChange)="store.load($event)" />
      }
    </section>
  `,
})
export class InvoicesPage implements OnInit {
  protected readonly store = inject(InvoicesStore);
  private readonly reference = inject(ReferenceDataStore);
  private readonly auth = inject(AuthStore);
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  protected readonly statusOptions: { value: InvoiceStatusFilter; label: string }[] = [
    { value: 'all', label: 'Any status' },
    { value: 'AwaitingOrder', label: 'Awaiting order' },
    { value: 'Matched', label: 'Matched' },
    { value: 'Mismatched', label: 'Mismatched' },
    { value: 'PartiallyPaid', label: 'Partially paid' },
    { value: 'Paid', label: 'Paid' },
  ];

  protected readonly mayCreate = computed(() => this.auth.hasPermission('invoices.create'));
  private readonly names = computed(() => new Map(this.reference.suppliers().map((s) => [s.code, s.name])));

  protected supplierName(invoice: InvoiceDto): string {
    return this.names().get(invoice.supplierId) ?? invoice.supplierId;
  }
  protected match(invoice: InvoiceDto): string {
    return matchStatus(invoice);
  }
  protected payment(invoice: InvoiceDto): string {
    return paymentStatus(invoice);
  }
  protected setStatus(event: Event): void {
    void this.store.setStatus((event.target as HTMLSelectElement).value as InvoiceStatusFilter);
  }

  ngOnInit(): void {
    void this.store.load(1);
    void this.reference.loadSuppliers();
  }
}
