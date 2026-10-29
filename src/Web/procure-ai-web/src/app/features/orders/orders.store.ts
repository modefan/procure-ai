import { computed, inject } from '@angular/core';
import { signalStore, withComputed, withMethods } from '@ngrx/signals';
import { OrderStatus } from '../../api/purchasing/models/order-status';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { withPagedList } from '../../core/state/with-paged-list';

export type OrderStatusFilter = 'all' | OrderStatus;

interface OrdersFilter {
  status: OrderStatusFilter;
}

/**
 * The purchase orders list, one page at a time (the server limits requesters to the orders of their own requests; see
 * `withPagedList`). An `order` push or a reconnect refreshes the page on screen in the background, moving to another page or
 * filter loads in the foreground.
 */
export const OrdersStore = signalStore(
  { providedIn: 'root' },
  withPagedList(() => {
    const api = inject(OrdersService);
    return {
      entity: 'order',
      trailing: true,
      errorMessage: 'The orders could not be loaded.',
      initialFilter: { status: 'all' } as OrdersFilter,
      load: (f: OrdersFilter, page: number, pageSize: number) => api.listOrders({ page, pageSize, ...(f.status !== 'all' ? { status: f.status } : {}) }),
    };
  }),
  withComputed(({ filter }) => ({ status: computed(() => filter().status) })),
  withMethods((store) => ({
    /** Changes the status filter and shows page 1 of the result. */
    setStatus: (status: OrderStatusFilter) => store.setFilter({ status }),
  })),
);
