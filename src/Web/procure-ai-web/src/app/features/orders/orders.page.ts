import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { AuthStore } from '../../core/auth/auth.store';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { Pager } from '../../core/ui/pager';
import { StatusBadge } from '../requests/status-badge';
import { canReceive, receivedText } from './order-quantities';
import { OrdersStore, OrderStatusFilter } from './orders.store';

/**
 * Screen 9: purchase orders with the supplier's state and how much has been received (needs `orders.read`).
 * A table from the lg breakpoint up; below it, one card per order. The supplier name comes from the supplier list
 * that is cached for the session (the order holds only the supplier code).
 */
@Component({
  selector: 'app-orders-page',
  imports: [StatusBadge, Pager, RouterLink, NgTemplateOutlet],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">Purchase orders</h1>
          <p class="mt-1 text-slate-600">Amounts include VAT.</p>
        </div>
        <div>
          <label for="order-status" class="field-label">Status</label>
          <select id="order-status" class="field-input w-auto" [value]="store.status()" (change)="setStatus($event)">
            @for (option of statusOptions; track option.value) {
              <option [value]="option.value">{{ option.label }}</option>
            }
          </select>
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load(store.page())">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading orders</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          @if (store.status() !== 'all') {
            <p class="text-lg font-semibold text-slate-900">No orders match this status</p>
            <button type="button" class="btn-secondary mt-4" (click)="store.setStatus('all')">Show all orders</button>
          } @else {
            <p class="text-lg font-semibold text-slate-900">No purchase orders yet</p>
            <p class="mt-1 text-slate-600">An approved request becomes an order and appears here.</p>
          }
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [attr.aria-busy]="store.isLoading()" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Purchase orders, page {{ store.page() }} of {{ store.pageCount() }}</caption>
              <colgroup>
                <col class="w-36" />
                <col class="w-40" />
                <col class="w-32" />
                <col />
                <col class="w-24" />
                <col class="w-40" />
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Order</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Supplier</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Amount</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Supplier status</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Received</th>
                  <th scope="col" class="px-4 py-3"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (order of store.entities(); track order.id) {
                  <tr class="align-top hover:bg-slate-50" [attr.data-order]="order.id">
                    <td class="px-4 py-3 font-medium whitespace-nowrap">
                      <a [routerLink]="['/app/orders', order.id]" class="text-indigo-700 hover:underline">{{ order.number }}</a>
                      <div class="mt-1"><app-status-badge [status]="order.status" /></div>
                    </td>
                    <td class="truncate px-4 py-3 text-slate-800" [title]="supplierName(order)">{{ supplierName(order) }}</td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900">{{ order.totalInclVatDisplay }}</td>
                    <td class="px-4 py-3">
                      <ng-container [ngTemplateOutlet]="supplierState" [ngTemplateOutletContext]="{ $implicit: order }" />
                    </td>
                    <td class="px-4 py-3 whitespace-nowrap tabular-nums text-slate-800">{{ received(order) }}</td>
                    <td class="px-4 py-3 text-right">
                      @if (mayReceive() && canReceiveOrder(order)) {
                        <a [routerLink]="['/app/orders', order.id]" class="btn-secondary whitespace-nowrap">Record delivery</a>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 lg:hidden" aria-label="Purchase orders">
            @for (order of store.entities(); track order.id) {
              <li class="card p-4">
                <div class="flex items-center justify-between gap-3">
                  <a [routerLink]="['/app/orders', order.id]" class="font-semibold whitespace-nowrap text-indigo-700 hover:underline">{{ order.number }}</a>
                  <span class="font-semibold whitespace-nowrap tabular-nums text-slate-900">{{ order.totalInclVatDisplay }}</span>
                </div>
                <div class="mt-1"><app-status-badge [status]="order.status" /></div>
                <p class="mt-1 truncate text-slate-800" [title]="supplierName(order)">{{ supplierName(order) }}</p>
                <div class="mt-2">
                  <ng-container [ngTemplateOutlet]="supplierState" [ngTemplateOutletContext]="{ $implicit: order }" />
                </div>
                <div class="mt-3 flex flex-wrap items-center justify-between gap-3 text-sm">
                  <span class="whitespace-nowrap tabular-nums text-slate-700">Received {{ received(order) }}</span>
                  @if (mayReceive() && canReceiveOrder(order)) {
                    <a [routerLink]="['/app/orders', order.id]" class="btn-secondary whitespace-nowrap">Record delivery</a>
                  }
                </div>
              </li>
            }
          </ul>
        </div>

        <app-pager [page]="store.page()" [pageCount]="store.pageCount()" [total]="store.total()" [loading]="store.isLoading()" noun="order" nounPlural="orders" (pageChange)="store.load($event)" />
      }
    </section>

    <ng-template #supplierState let-order>
      <app-status-badge [status]="order.supplierStatus" />
      @if (order.supplierLastError) {
        <details class="mt-1 text-xs text-slate-700">
          <summary class="cursor-pointer text-amber-800">Why? ({{ order.supplierAttempts }} failed {{ order.supplierAttempts === 1 ? 'attempt' : 'attempts' }} so far)</summary>
          <p class="mt-1 break-words">Last error: {{ order.supplierLastError }}</p>
        </details>
      }
    </ng-template>
  `,
})
export class OrdersPage implements OnInit {
  protected readonly store = inject(OrdersStore);
  private readonly reference = inject(ReferenceDataStore);
  private readonly auth = inject(AuthStore);
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  protected readonly statusOptions: { value: OrderStatusFilter; label: string }[] = [
    { value: 'all', label: 'Any status' },
    { value: 'PendingSupplier', label: 'Pending supplier' },
    { value: 'SentToSupplier', label: 'Sent to supplier' },
    { value: 'Confirmed', label: 'Confirmed' },
    { value: 'PartiallyReceived', label: 'Partially received' },
    { value: 'Received', label: 'Received' },
    { value: 'ClosedShort', label: 'Closed short' },
    { value: 'Rejected', label: 'Rejected' },
    { value: 'Cancelled', label: 'Cancelled' },
  ];

  protected readonly mayReceive = computed(() => this.auth.hasPermission('orders.receive'));
  private readonly names = computed(() => new Map(this.reference.suppliers().map((s) => [s.code, s.name])));

  protected supplierName(order: PurchaseOrderDto): string {
    return this.names().get(order.supplierId) ?? order.supplierId;
  }
  protected received(order: PurchaseOrderDto): string {
    return receivedText(order);
  }
  protected canReceiveOrder(order: PurchaseOrderDto): boolean {
    return canReceive(order);
  }
  protected setStatus(event: Event): void {
    void this.store.setStatus((event.target as HTMLSelectElement).value as OrderStatusFilter);
  }

  ngOnInit(): void {
    void this.store.load(1);
    void this.reference.loadSuppliers();
  }
}
