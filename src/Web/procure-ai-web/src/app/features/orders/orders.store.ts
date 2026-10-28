import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { OrderStatus } from '../../api/purchasing/models/order-status';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { AppConfigService } from '../../core/config/app-config';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';

export type OrderStatusFilter = 'all' | OrderStatus;

interface OrdersState {
  status: OrderStatusFilter;
  page: number;
  pageSize: number;
  total: number;
}

/**
 * The purchase orders list, one page at a time (the server limits requesters to the orders of their own requests).
 * Stale-while-revalidate like the requests list: asking again within the stale time does nothing, an `order` push or a
 * reconnect refreshes the page on screen in the background, moving to another page or filter loads in the foreground.
 */
export const OrdersStore = signalStore(
  { providedIn: 'root' },
  withEntities<PurchaseOrderDto>(),
  withState<OrdersState>(() => ({ status: 'all' as OrderStatusFilter, page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0 })),
  withRequestStatus(),
  withComputed(({ total, pageSize }) => ({
    pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
  })),
  withCache(),
  withMethods((store) => {
    const api = inject(OrdersService);
    const log = inject(Logger).withSource('OrdersStore');
    let latest = 0;
    let shown: { key: string; epoch: number } | null = null;
    let currentKey: string | null = null;

    const methods = {
      /** Shows one page (1-based). Only the newest call may change the state; a failure sets `error` and keeps the rows. */
      async load(page: number): Promise<void> {
        const pageSize = store.pageSize();
        const status = store.status();
        const key = `list:${page}:${pageSize}:${status}`;
        const onScreen = () => shown !== null && shown.key === key && shown.epoch === store.cacheEpoch();
        if (currentKey !== key) {
          store.clearCache();
          currentKey = key;
        }
        try {
          await store.cacheLoad(key, async () => {
            const call = ++latest;
            const epoch = store.cacheEpoch();
            if (!onScreen()) {
              store.setLoading();
            }
            try {
              const result = await api.listOrders({ page, pageSize, ...(status !== 'all' ? { status } : {}) });
              if (call !== latest || epoch !== store.cacheEpoch()) {
                return;
              }
              patchState(store, setAllEntities(result.items), { page: result.page, pageSize: result.pageSize, total: result.total });
              shown = { key, epoch };
              store.setLoaded();
            } catch (error) {
              if (call === latest && epoch === store.cacheEpoch()) {
                log.error('Loading the orders failed', error);
                store.setError(error, 'The orders could not be loaded.');
              }
              throw error;
            }
          });
        } catch {
          // Already recorded in `error`.
        }
      },

      /** Changes the status filter and shows page 1 of the result. */
      async setStatus(status: OrderStatusFilter): Promise<void> {
        patchState(store, { status });
        await methods.load(1);
      },
    };
    return methods;
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        off = inject(LiveConnection).onEntityChanged('order', () => store.invalidate('list'));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
